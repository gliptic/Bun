namespace LabWeb.Bun
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Web.Hosting;

    public class AspNetBuilder : BunBuilder, IBlobResolver
    {
        public Dictionary<string, FileBlob> accessed = new Dictionary<string, FileBlob>();

        public AspNetBuilder(BunStore store)
        : base(store)
        {
        }

        public Blob GetFile(string virtualPath)
        {
            string key = store.NormalizeCase(virtualPath);

            FileBlob blob;
            if (accessed.TryGetValue(key, out blob))
            {
                return blob;
            }

            blob = new FileBlob(virtualPath, HostingEnvironment.MapPath("~/" + virtualPath));
            accessed.Add(key, blob);
            return blob;
        }

        public Blob GetTransformedFile(string virtualPath)
        {
            Blob blob = this.store.Resolve(virtualPath);
            if (blob == null)
            {
                // No transformed file in store, use plain one
                blob = this.GetFile(virtualPath);
            }

            return blob;
        }

        public IEnumerable<Blob> GetAll(string virtualPath, string pattern)
        {
            if (!virtualPath.EndsWith("/"))
            {
                virtualPath += '/';
            }

            var path = HostingEnvironment.MapPath("~/" + virtualPath);
            var flippedPath = path.Replace(Path.DirectorySeparatorChar, '/');

            foreach (var filePath in Directory.GetFiles(path, pattern, SearchOption.AllDirectories))
            {
                var flippedFilePath = filePath.Replace(Path.DirectorySeparatorChar, '/');
                var fileVirtualPath = virtualPath + BunBuilder.RelPath(flippedFilePath, flippedPath, '/');

                yield return this.GetFile(fileVirtualPath);
            }
        }
    }
}