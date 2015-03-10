using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Web;

namespace LabWeb.Bun
{
    public static class BlobExt
    {
        public class JsMinifyBlob : TransformBlob
        {
            public JsMinifyBlob(Blob source)
            : base(source)
            {
                Debug.Assert(source.MimeType == FileBlob.MimeJavaScript);
            }

            protected override string ToStringImpl()
            {
                var min = new Microsoft.Ajax.Utilities.Minifier();
                var str = this.source.String;
                Trace.WriteLine("Minifying " + str.Length.ToString() + " chars of JavaScript");
                var r = min.MinifyJavaScript(str);
                return r;
            }
        }

        public class CssMinifyBlob : TransformBlob
        {
            public CssMinifyBlob(Blob source)
            : base(source)
            {
                Debug.Assert(source.MimeType == FileBlob.MimeCss);
            }

            protected override string ToStringImpl()
            {
                var min = new Microsoft.Ajax.Utilities.Minifier();
                var str = this.source.String;
                Trace.WriteLine("Minifying " + str.Length.ToString() + " chars of CSS");
                var r = min.MinifyStyleSheet(str);
                return r;
            }
        }

        public class DataUrlBlob : TransformBlob
        {
            public DataUrlBlob(Blob source)
            : base(source)
            {
                this.mimeType = FileBlob.MimeHtml;
            }

            protected override string ToStringImpl()
            {
                var url = "data:" + this.source.MimeType + ";base64," + Convert.ToBase64String(this.source.Array);
                this.source = null;
                return url;
            }
        }
        
        public static JsMinifyBlob MinifyJs(this Blob blob)
        {
            return new JsMinifyBlob(blob);
        }

        public static CssMinifyBlob MinifyCss(this Blob blob)
        {
            return new CssMinifyBlob(blob);
        }

        public static DataUrlBlob DataUrl(this Blob blob)
        {
            return new DataUrlBlob(blob);
        }
    }
}