namespace LabWeb.Bun
{
    using System.Diagnostics;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Linq;
    using System;
    using System.Collections.Generic;

    public class ScriptTagBlob : Blob
    {
        private string scriptPath;
        private Blob scriptBody;

        public ScriptTagBlob(string scriptPath = null, Blob scriptBody = null)
        : base(FileBlob.MimeHtml)
        {
            // Exactly one of scriptPath or scriptBody
            Debug.Assert((scriptPath != null) != (scriptBody != null));
            this.scriptPath = scriptPath;
            this.scriptBody = scriptBody;
        }

        protected override string ToStringImpl()
        {
            if (this.scriptPath != null)
            {
                // TODO: Path is relative to what?
                return "<script src=\"" + this.scriptPath + "\" type=\"text/javascript\"></script>";
            }
            else
            {
                // TODO: Escape </
                var r = "<script type=\"text/javascript\">\n" + this.scriptBody.ToString() + "</script>";
                this.scriptBody = null;
                return r;
            }
        }
    }

    public static class HtmlBlobExt
    {
        public class InlineSrcBlob : TransformBlob
        {
            private Regex patterns;
            private IBlobResolver blobResolver;

            public InlineSrcBlob(Blob source, IBlobResolver blobResolver, string pattern)
            : base(source)
            {
                Debug.Assert(this.source.MimeType == FileBlob.MimeHtml
                          || this.source.MimeType == FileBlob.MimeCss);

                var regexStr = Regex.Escape(pattern);
                regexStr = regexStr.Replace("\\*", ".*"); // * was escaped to \*, but we want it to mean .*

                this.patterns = new Regex("^(?:" + regexStr + ")$", RegexOptions.CultureInvariant);
                this.blobResolver = blobResolver;
            }

            protected override string ToStringImpl()
            {
                var text = this.source.ToString();
                var result = new StringBuilder();
                int prevEnd = 0;

                Action<int, int> writeUpTo = (pos, skipLen) =>
                {
                    if (pos != prevEnd)
                    {
                        result.Append(text, prevEnd, pos - prevEnd);
                    }
                    prevEnd = pos + skipLen;
                };

                string urlRegex;
                if (this.source.MimeType == FileBlob.MimeCss)
                {
                    urlRegex = @"url\(([^\)]*?)\)";
                }
                else if (this.source.MimeType == FileBlob.MimeHtml)
                {
                    urlRegex = "src=\"([^\"]*?)\"";
                }
                else
                {
                    throw new NotImplementedException();
                }

                foreach (Match m in Regex.Matches(text, urlRegex))
                {
                    var url = m.Groups[1].Value;
                    if (!url.Contains("//") // Must be relative URL
                      && this.patterns.IsMatch(url))
                    {
                        var trans = blobResolver.GetTransformedFile(url).DataUrl();

                        writeUpTo(m.Groups[1].Index, m.Groups[1].Length);

                        result.Append(trans.ToString());
                    }
                }

                // Write rest
                writeUpTo(text.Length, 0);

                return result.ToString();
            }
        }

        public static InlineSrcBlob InlineSrc(this Blob blob, IBlobResolver fromContext, string pattern)
        {
            return new InlineSrcBlob(blob, fromContext, pattern);
        }
    }
}