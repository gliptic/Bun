namespace LabWeb.Bun
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Web;
    using System.Web.Hosting;

    public class BunBuilder
    {
        // Containing blobs
        private Dictionary<Blob, Blob> containingBlobLookup = new Dictionary<Blob, Blob>();
        private volatile bool isInitialized = false;
        
        public readonly string BunPrefix = "bun/";

        protected Dictionary<Blob, string> blobPaths = new Dictionary<Blob, string>();
        protected BunStore store;

#if false
        private FileSystemWatcher watcher;
        private List<string> watchedFiles = new List<string>();
#endif

        public BunBuilder(BunStore store)
        {
            this.store = store;
        }
        
        public void Init(Action init)
        {
#if false
            watcher = new FileSystemWatcher(HostingEnvironment.MapPath("~/"));
            watcher.Changed += (sender, e) =>
            {
                Trace.WriteLine(e.FullPath + " - " + e.Name + " - " + e.ChangeType.ToString());

                if (isInitialized
                 && e.ChangeType == WatcherChangeTypes.Changed
                 && watchedFiles.Any(x => x.StartsWith(e.FullPath)))
                {
                    _lock.EnterWriteLock();

                    try
                    {
                        Clear();
                    }
                    finally
                    {
                        _lock.ExitWriteLock();
                    }
                }
            };
            watcher.EnableRaisingEvents = true;
#endif
        }

        private void EnsureInit()
        {
            if (!isInitialized)
            {
                Trace.WriteLine("Re-init");

                isInitialized = true; // Before init() to avoid re-entry

#if false
                var paths = store.Values
                    .SelectMany(b => b.SubPaths)
                    .Select(p => HostingEnvironment.MapPath("~/" + p));

                watchedFiles = new List<string>(paths);
#endif
            }
        }

        // TODO: Allow storing with the same blob path multiple times, but with
        // different settings. E.g. a HTML blob with data-URI expanded images, and one without.
        public Blob Store(string blobPath, Blob blob)
        {
            Debug.Assert(!this.blobPaths.ContainsKey(blob), "Blob is already added to this context");

            this.blobPaths.Add(blob, blobPath);
            this.containingBlobLookup[blob] = blob;
    
            store.Store(blobPath, blob);

            foreach (var subBlob in blob.SubBlobs)
            {
                // Overwrite current blob if the new one covers more leaves.
                // When each blob is only subsumed in one other blob (e.g. via Concat or transformation),
                // this ensures that each blob is only contained in one blob in containingBlobLookup. This
                // is because the blobs form a tree where a sub-tree is strictly larger
                // than any of its children's subtrees and containing a superset of blobs.

                // TODO: In general, we need to overwrite only when the new blob has a superset
                // of subpaths from the old blob.
                Blob currentBlob;
                if (!containingBlobLookup.TryGetValue(subBlob, out currentBlob)
                  || currentBlob.Leaves < blob.Leaves)
                {
                    containingBlobLookup[subBlob] = blob;
                }
            }

            return blob;
        }

        public Blob Store(Blob blob)
        {
            return this.Store(blob.OriginalVirtualPathMaybe, blob);
        }

        public void Store(IEnumerable<Blob> blobs)
        {
            foreach (var blob in blobs)
            {
                this.Store(blob);
            }
        }

        public string GetBlobPath(Blob blob, string basePath)
        {
            string blobPath = this.blobPaths[blob];
            var p = BunBuilder.RelPath(BunPrefix + blobPath, basePath);
            var extpos = p.LastIndexOf('.');

            return p.Substring(0, extpos) + '$' + blob.Suffix + p.Substring(extpos);
        }

        public Blob FindLargestContainingBlob(Blob blob)
        {
            Blob containingBlob;
            this.containingBlobLookup.TryGetValue(blob, out containingBlob);
            return containingBlob;
        }

        public Blob GenerateFileMappingFunction(IEnumerable<string> ignoredVirtualPathsList)
        {
            var builder = new StringBuilder();
            var ignoredVirtualPaths = new HashSet<string>(ignoredVirtualPathsList);

            builder.Append("!function () { \n");
            builder.Append("  var suffixes = {\n");

            foreach (var blob in this.blobPaths)
            {
                var virtualPath = blob.Key.OriginalVirtualPathMaybe;
                var newVirtualPath = blob.Value;

                if (virtualPath != null
                 && virtualPath == newVirtualPath // Only include files that aren't renamed.
                 && virtualPath.Contains('.') // They must include a dot to be routable anyway
                 && !ignoredVirtualPaths.Contains(virtualPath))
                {
                    builder.Append("    '");
                    builder.Append(this.store.NormalizeCase(virtualPath));
                    builder.Append("': '");

                    //builder.Append(this.GetBlobPath(blob.Key, ""));
                    builder.Append(blob.Key.Suffix);
                    builder.Append("',\n");
                }
            }
            builder.Append("  };\n");
            builder.Append("  this.mapPath = function (path) {\n");
            builder.Append("    var s = suffixes[path" + (this.store.CaseSensitive ? string.Empty : ".toLowerCase()") + "], i;\n");
            builder.Append("    if (s) {\n");
            builder.Append("      i = path.lastIndexOf('.');\n");
            builder.Append("      return '" + this.BunPrefix + "' + path.substr(0, i) + '$' + s + path.substr(i);\n");
            builder.Append("    }\n");
            builder.Append("    return path;\n");
            builder.Append("  };\n");
            builder.Append("}.call(this);\n");

            return new StringBlob(builder.ToString(), FileBlob.MimeJavaScript);
        }

        // Convert path to a relative. E.g. /a/b/c/ from /d/e/f/ -> ../../../a/b/c
        // /a/b/c/ & /d/e/f/ -> d/e/f/ -> ../../../ -> ../../../a/b/c/
        // fromPath must end in /
        public static string RelPath(string absolutePath, string fromPath, char sep = '/')
        {
            Debug.Assert(fromPath.Length == 0 || fromPath.EndsWith(new string(sep, 1)), "Path must end with path separator unless empty");

            int max = Math.Min(absolutePath.Length, fromPath.Length);
            int commonLength = 0;
            for (int i = 0; i < max; ++i)
            {
                if (absolutePath[i] != fromPath[i])
                    break;

                if (fromPath[i] == sep)
                {
                    commonLength = i + 1;
                }
            }

            var relPath = new StringBuilder();

            for (int i = commonLength; i < fromPath.Length; ++i)
            {
                if (fromPath[i] == sep)
                    relPath.Append(".." + sep); // Undo each separator not found in absolutePath
            }

            return relPath.ToString() + absolutePath.Substring(commonLength); // Add absolutePath's unshared path
        }

    }
}