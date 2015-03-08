using Microsoft.Owin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace LabWeb.Bun
{
    public class BunHandler : IHttpHandler
    {
        private static Regex pathRegex = new Regex(@"^/([^\$]*?)(\$([^\$]*))?(\.[^\$]*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public void ProcessRequest(HttpContext context)
        {
            HttpRequest req = context.Request;
            HttpResponse resp = context.Response;

            var path = req.PathInfo;

            // path has the form: /<path>$<version>.<ext>

            var pathMatches = pathRegex.Match(path);

            if (!pathMatches.Success)
            {
                resp.StatusCode = 404;
                resp.SuppressContent = true;
                return;
            }

            var p = pathMatches.Groups[1].Value + pathMatches.Groups[4].Value;
            bool hasVersion = pathMatches.Groups[3].Success;
            var version = pathMatches.Groups[3].Value;

            Blob blob = BunStore.Instance.Resolve(p);
            if (blob == null)
            {
                resp.StatusCode = 404;
                resp.SuppressContent = true;
                return;
            }

            var reqEtag = req.Headers["If-None-Match"];

            if (reqEtag == blob.Suffix)
            {
                MaximumCache(resp, blob);
                resp.StatusCode = 304;
                resp.SuppressContent = true;
            }
            else
            {
                if (hasVersion && version == blob.Suffix)
                {
                    MaximumCache(resp, blob);
                }
                else if (hasVersion)
                {
                    // If the version doesn't match, don't cache for long
                    resp.Cache.SetCacheability(HttpCacheability.Public);
                    resp.Cache.SetExpires(DateTime.UtcNow.AddHours(1));
                    resp.Cache.SetMaxAge(new TimeSpan(0, 1, 0, 0));
                    //resp.Cache.SetETag(blob.Suffix);
                }
                else
                {
                    NoCache(resp);
                }

                WriteBlob(new HttpRequestWrapper(req), new HttpResponseWrapper(resp), blob);
            }
        }

        public static void MaximumCache(HttpResponse resp, Blob blob)
        {
            resp.Cache.SetCacheability(HttpCacheability.Public);
            resp.Cache.SetExpires(DateTime.UtcNow.AddYears(1));
            resp.Cache.SetMaxAge(new TimeSpan(365, 0, 0, 0));
            resp.Cache.SetETag(blob.Suffix);
        }

        public static void NoCache(HttpResponse resp)
        {
            resp.AddHeader("Pragma", "no-cache");
            resp.CacheControl = "no-cache";
            resp.Expires = -1;
        }

        public static void WriteBlob(HttpRequestBase request, HttpResponseBase response, Blob blob)
        {
            var acceptEncoding = request.Headers["Accept-Encoding"];
            if (acceptEncoding != null || acceptEncoding.Contains("gzip"))
            {
                response.AppendHeader("Content-Encoding", "gzip");
                response.AppendHeader("Vary", "Accept-Encoding");

                response.ContentType = blob.MimeType;
                response.BinaryWrite(blob.GZipData);
                return; // DONE!
            }

            response.ContentType = blob.MimeType;
            response.BinaryWrite(blob.ToArray());
        }

        public bool IsReusable
        {
            // To enable pooling, return true here.
            // This keeps the handler in memory.
            get { return true; }
        }
    }
}