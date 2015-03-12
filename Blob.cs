namespace LabWeb.Bun
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Web.Hosting;
    using System.Linq;

    public abstract class Blob
    {
        public static UTF8Encoding Utf8 = new System.Text.UTF8Encoding(false);

        // Cached values
        private volatile byte[] data;
        private volatile string suffix;
        private volatile byte[] gzipData;
        private volatile string strData;

        protected string mimeType;
        protected List<Blob> subBlobs = new List<Blob>();
        protected int leaves = 1;

        protected Blob()
        {
        }

        protected Blob(Blob source)
        {
            this.mimeType = source.MimeType;
            this.subBlobs.AddRange(source.SubBlobs);
            this.subBlobs.Add(source);

            this.leaves = source.Leaves;
        }

        protected Blob(string mimeType)
        {
            this.mimeType = mimeType;
        }

        public void WriteTo(StringBuilder dest)
        {
            dest.Append(this.String);
        }

        public string MimeType { get { return this.mimeType; } }
        public IEnumerable<Blob> SubBlobs { get { return this.subBlobs; } }
        public int Leaves { get { return this.leaves; } }

        public string VirtualPath
        {
            get
            {
                var v = this.VirtualPathMaybe;
                if (v == null) throw new ApplicationException("This blob has no virtual path");
                return v;
            }
        }

        public virtual string VirtualPathMaybe
        {
            // Default to none
            get { return null; }
        }

        public string OriginalVirtualPath
        {
            get
            {
                var v = this.OriginalVirtualPathMaybe;
                if (v == null) throw new ApplicationException("This blob has no original virtual path");
                return v;
            }
        }

        public virtual Blob OriginalBlob
        {
            get { return this; }
        }

        public string OriginalVirtualPathMaybe
        {
            get { return this.OriginalBlob.VirtualPathMaybe; }
        }
        
        public string Suffix
        {
            get
            {
                if (this.suffix == null)
                {
                    lock (this)
                    if (this.suffix == null)
                    {
                        // SHA1 is OK because we don't need protection against attacks
                        var sha1 = new SHA1CryptoServiceProvider();
                        var data = this.Array;

                        Trace.WriteLine("Hashing " + data.Length + " bytes");
                
                        // Compute suffix
                        var hash = sha1.ComputeHash(data);

                        // 6 bytes offers a collision resistance of 2^48.
                        // ~2^24 hashes are required before there's a 50% chance that two of them collide.
                        var base64 = System.Convert.ToBase64String(hash, 0, 6); // 6 is divisible by three, so we avoid padding
                        this.suffix = base64.Replace('+', '-').Replace('/', '_'); // Use URL-friendly chars
                    }
                }

                return this.suffix;
            }
        }

        public byte[] GZipData
        {
            get
            {
                if (this.gzipData == null)
                {
                    lock (this)
                    if (this.gzipData == null) // Someone else might have filled it while we were waiting to lock
                    {
                        var data = this.Array;

                        using (var resultStream = new MemoryStream())
                        {
                            using (var gzipStream = new GZipStream(resultStream, CompressionMode.Compress, false))
                            {
                                gzipStream.Write(data, 0, data.Length);
                            }

                            this.gzipData = resultStream.ToArray();

                            Trace.WriteLine("Gzipping " + data.Length + " bytes to " + this.gzipData.Length.ToString() + " bytes");
                        }
                    }
                }
                
                return this.gzipData;
            }
        }

        public string String
        {
            get
            {
                if (this.strData == null)
                {
                    lock (this)
                    if (this.strData == null)
                    {
                        if (this.data != null)
                        {
                            Trace.WriteLine("Converting " + this.data.Length + " bytes to chars");
                            this.strData = Utf8.GetString(this.data);
                        }
                        else
                        {
                            this.strData = this.ToStringImpl();
                        }
                    }
                }
            
                return this.strData;
            }
        }

        public byte[] Array
        {
            get
            {
                if (this.data == null)
                {
                    lock (this)
                    if (this.data == null)
                    {
                        if (this.strData != null)
                        {
                            Trace.WriteLine("Converting " + this.strData.Length + " chars to bytes");
                            this.data = Utf8.GetBytes(this.strData);
                        }
                        else
                        {
                            this.data = this.ToArrayImpl();
                        }
                    }
                }
            
                return this.data;
            }
        }

        // NOTE: Either ToStringImpl() or ToArrayImpl() MUST be overridden.
        // One of these will be called once, so any dependent data can be discarded afterwards.

        protected virtual byte[] ToArrayImpl()
        {
            var str = this.ToStringImpl();
            Trace.WriteLine("Converting " + str.Length + " chars to bytes");
            return Utf8.GetBytes(str);
        }

        protected virtual string ToStringImpl()
        {
            var arr = this.ToArrayImpl();
            Trace.WriteLine("Converting " + arr.Length + " bytes to chars");
            return Utf8.GetString(arr);
        }

        public static Blob Concat(params Blob[] blobs)
        {
            return new ConcatBlob(blobs);
        }
    }

    public class ConcatBlob : Blob
    {
        private Blob[] children;

        public ConcatBlob(Blob[] children)
        {
            this.children = children;

            this.mimeType = children[0].MimeType;
            for (int i = 1; i < children.Length; ++i)
            {
                this.mimeType = FileBlob.CombineMimes(this.mimeType, children[i].MimeType);

                this.subBlobs.AddRange(children[i].SubBlobs);
                this.leaves += children[i].Leaves;
            }
        }

        protected override string ToStringImpl()
        {
            Trace.WriteLine("Concatenating " + this.children.Length.ToString() + " blobs to string");
            var builder = new StringBuilder();

            bool first = true;
            foreach (var child in this.children)
            {
                if (!first)
                {
                    builder.Append("\r\n");
                }

                first = false;

                builder.Append(child.String);
            }

            return builder.ToString();
        }

        protected override byte[] ToArrayImpl()
        {
            Trace.WriteLine("Concatenating " + this.children.Length.ToString() + " blobs to array");

            var arrays = new List<byte[]>();

            foreach (var child in this.children)
            {
                arrays.Add(child.Array);
            }

            var totalSize = arrays.Sum(a => a.Length) + (arrays.Count - 1) * 2;

            var totalArray = new byte[totalSize];
            int pos = 0;

            bool first = true;
            foreach (var arr in arrays)
            {
                if (!first)
                {
                    totalArray[pos++] = (byte)'\r';
                    totalArray[pos++] = (byte)'\n';
                }

                first = false;

                System.Buffer.BlockCopy(arr, 0, totalArray, pos, arr.Length);
                pos += arr.Length;
            }

            return totalArray;
        }
    }

    public class StringBlob : Blob
    {
        private string data;

        public StringBlob(string data, string mimeType)
        : base(mimeType)
        {
            this.data = data;
        }

        public void SetData(string newData)
        {
            this.data = newData;
        }

        protected override string ToStringImpl()
        {
            var r = this.data;
            this.data = null;
            return r;
        }
    }

    public abstract class TransformBlob : Blob
    {
        protected Blob source;

        public TransformBlob(Blob source)
        : base(source)
        {
            this.source = source;
        }

        public override Blob OriginalBlob
        {
            get { return this.source.OriginalBlob; }
        }
    }

    public class IdentityBlob : TransformBlob
    {
        public IdentityBlob(Blob source)
        : base(source)
        {
        }

        protected override string ToStringImpl()
        {
            var r = this.source.String;
            return r;
        }

        protected override byte[] ToArrayImpl()
        {
            var r = this.source.Array;
            return r;
        }
    }
    
    public class FileBlob : Blob
    {
        private string sourcePath;
        private string virtualPath;

        public const string MimeJavaScript = "text/javascript";
        public const string MimeCss = "text/css";
        public const string MimePng = "image/png";
        public const string MimeOctetStream = "application/octet-stream";
        public const string MimeHtml = "text/html";

        public static string GuessMimeFromFilename(string filename)
        {
            if (filename.EndsWith(".js")) return MimeJavaScript;
            else if (filename.EndsWith(".css")) return MimeCss;
            else if (filename.EndsWith(".html")) return MimeHtml;
            else if (filename.EndsWith(".png")) return MimePng;
            return MimeOctetStream;
        }

        public static string CombineMimes(string mime1, string mime2)
        {
            if (mime1 == mime2) return mime1;
            return MimeOctetStream;
        }

        public FileBlob(string virtualPath, string sourcePath, string mimeType = null)
        {
            this.sourcePath = sourcePath;
            this.mimeType = mimeType ?? GuessMimeFromFilename(sourcePath);
            this.virtualPath = virtualPath;
        }

        public override string VirtualPathMaybe
        {
            get { return this.virtualPath; }
        }

        protected override byte[] ToArrayImpl()
        {
            Trace.WriteLine("Reading file as byte[]: " + sourcePath);
            return File.ReadAllBytes(sourcePath);
        }

        protected override string ToStringImpl()
        {
            // Reading some files as UTF-8 may break them, so we have a whitelist
            switch (this.mimeType)
            {
                case MimeHtml:
                case MimeCss:
                case MimeJavaScript:
                    Trace.WriteLine("Reading file as string: " + sourcePath);

                    using (var f = File.OpenText(sourcePath))
                    {
                        return f.ReadToEnd();
                    }

                default:
                    return base.ToStringImpl();
            }
        }
    }
}