namespace LabWeb.Bun
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Web;

    public class BunStore
    {
        public static Lazy<BunStore> Instance;

        private Dictionary<string, Blob> store = new Dictionary<string, Blob>();
        public readonly bool CaseSensitive = false;

        // NOTE: This should only be used for dictionary keys and values
        // compared to dictionary keys.
        public string NormalizeCase(string str)
        {
            if (this.CaseSensitive)
                return str;
            return str.ToLowerInvariant();
        }
        
        public Blob Store(string blobPath, Blob blob)
        {
            string key = this.NormalizeCase(blobPath);
            store.Add(key, blob);
            return blob;
        }

        public Blob Resolve(string blobPath)
        {
            Blob result;
            string key = this.NormalizeCase(blobPath);
            store.TryGetValue(key, out result);
            return result;
        }

        public HtmlString IncludeBlob(string blobPath)
        {
            var blob = this.Resolve(blobPath);
            Debug.Assert(blob.MimeType == FileBlob.MimeHtml);

            return new HtmlString(blob.String);
        }
    }
}