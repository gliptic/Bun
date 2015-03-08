using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace LabWeb.Bun
{
    public class Shim
    {
        internal string[] deps;
        internal string exports;

        public Shim(string[] deps = null, string exports = null)
        {
            this.deps = deps ?? new string[0];
            this.exports = exports;
        }
    }

    public class RequireBuilder
    {
        private BunBuilder bun;
        private Dictionary<string, Blob> mapping;
        private Dictionary<string, Shim> shims;

        private HashSet<string> flattened = new HashSet<string>();
        private Regex defineRegex;

        public RequireBuilder(
            BunBuilder bun,
            Dictionary<string, Blob> mapping,
            Dictionary<string, Shim> shims,
            IEnumerable<string> neverFlatten = null)
        {
            this.bun = bun;
            this.mapping = mapping;
            this.shims = shims;
            if (neverFlatten != null)
            {
                // Pretend they are already flattened
                this.flattened = new HashSet<string>(neverFlatten);
            }

            var ws = @"\s*";
            var quote = "[\"']";

            defineRegex = new Regex(
                @"define\(" + ws
                    + "()(?:" + ws + quote + "(.*?)" + quote + ws + "," + ")?" // Optional name
                    + ws + @"\[(.*?)\]"
                    + ws + ",",
                    RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }
        
        public Blob GenerateRequireJsMapping(
            string basePath,
            Blob includeRequireJsBlob,
            HashSet<string> exclude)
        {
            var dest = new StringBuilder();

            if (includeRequireJsBlob != null)
            {
                dest.Append(includeRequireJsBlob.ToString());
                dest.Append("\n");
            }

            dest.Append("require.config({ baseUrl: '");
            dest.Append(basePath);
            dest.Append("', paths: {\n");

            foreach (var m in mapping)
            {
                if (!exclude.Contains(m.Key))
                {
                    dest.Append("\'");
                    dest.Append(m.Key);
                    dest.Append("\': \'");
                    Blob blob = bun.FindLargestContainingBlob(m.Value);

                    if (blob != null)
                    {
                        dest.Append(bun.GetBlobPath(blob, basePath));
                    }
                    else
                    {
                        var relPath = BunBuilder.RelPath(m.Value.VirtualPath, basePath);
                        dest.Append(relPath);
                    }
                    dest.Append("\',\n");
                }
            }

#if false
            dest.Append("}, shim: {\n");

            foreach (var s in shims)
            {
                // TODO: We still need shims if they have exports
                if (!exclude.Contains(s.Key))
                {
                    dest.Append("\'");
                    dest.Append(s.Key);
                    dest.Append("\': {\n deps: [ ");
                    foreach (var d in s.Value.deps)
                    {
                        dest.Append('\'' + d + "', ");
                    }
                    dest.Append("]");
                    if (s.Value.exports != null)
                    {
                        dest.Append(",\n exports: ");
                        dest.Append('\'' + s.Value.exports + "', ");
                    }
                    dest.Append("},\n");
                }
            }
#endif
            dest.Append("}}, []);\n");

            foreach (var s in shims)
            {
                //if (exclude.Contains(s.Key))
                //{
                    dest.Append("define(\'");
                    dest.Append(s.Key);
                    dest.Append("\', [");
                    foreach (var d in s.Value.deps)
                    {
                        dest.Append('\'' + d + "', ");
                    }
                    dest.Append("], function () { return ");
                    dest.Append(s.Value.exports != null ? "window." + s.Value.exports : "undefined");
                    dest.Append("; });");
                //}
            }

            return new StringBlob(dest.ToString(), FileBlob.MimeJavaScript);
        }

        protected Blob LookupRequireModule(string name)
        {
            Blob blob = null;
            mapping.TryGetValue(name, out blob);

            return blob;
        }

        protected void TraverseModuleTree(
            string moduleName,
            HashSet<string> seen,
            Action<string, Blob, Action<string[]>> visitor)
        {
            if (seen.Contains(moduleName))
                return;

            Blob blob = LookupRequireModule(moduleName);
            if (blob == null)
            {
                throw new ApplicationException("Essential module '" + moduleName + "' is either not in mapping or has no stored blob");
            }
            
            visitor(moduleName, blob, deps =>
            {
                seen.Add(moduleName);
                foreach (var dep in deps)
                {
                    TraverseModuleTree(dep, seen, visitor);
                }
            });
        }
        
        protected HashSet<string> FlattenModules(string[] modules, List<Blob> nonAmdBlobs, List<Blob> amdBlobs, bool addName)
        {
            var seen = new HashSet<string>();

            // Default modules
            seen.Add("exports");
            seen.Add("require");

            foreach (var m in modules)
            {
                TraverseModuleTree(
                    m,
                    seen: seen,
                    visitor: (moduleName, blob, visitChildren) =>
                    {
                        if (flattened.Contains(moduleName))
                            return; // SKIP since we already have this flattened.

                        var strData = blob.ToString();

                        var defineParse = defineRegex.Match(strData);

                        bool isAmd = false;

                        if (defineParse.Success)
                        {
                            isAmd = true;

                            if (defineParse.Groups[2].Success)
                            {
                                string nameInBlob = defineParse.Groups[2].Value;

                                if (nameInBlob != moduleName)
                                {
                                    throw new ApplicationException("Module referred to as '" + moduleName + "' has the wrong name in file");
                                }
                            }
                            else if (addName)
                            {
                                // If the bundle does not have a name, give it one.
                                var namePos = defineParse.Groups[1].Index;
                                blob = new StringBlob(
                                    strData.Substring(0, namePos) + '"' + moduleName + "\", " + strData.Substring(namePos),
                                    blob.MimeType);
                            }
                        }

                        flattened.Add(moduleName);

                        var deps = new List<string>();

                        Shim shim;
                        if (this.shims.TryGetValue(moduleName, out shim))
                        {
                            deps.AddRange(shim.deps);
                        }

                        if (defineParse.Success)
                        {
                            deps.AddRange(defineParse.Groups[3].Value.Split(new[]{','}, StringSplitOptions.RemoveEmptyEntries).Select(x => {
                                var n = x.Trim();
                                return n.Substring(1, n.Length - 2); // Remove quotes
                            }));
                        }
                    
                        visitChildren(deps.ToArray());

                        if (isAmd)
                            amdBlobs.Add(blob);
                        else
                            nonAmdBlobs.Add(blob);
                    });
            }

            return seen;
        }

        public Blob CombineModules(string[] modules)
        {
            var bundles = new List<Blob>();

            this.FlattenModules(modules, bundles, bundles, addName: true);

            return Blob.Concat(bundles.ToArray());
        }

        public Blob RequireJsBoot(
            string blobPath,
            string basePath,
            string[] essential,
            bool combined,
            Blob requireJsBlob)
        {
            // 1. Find transitive closure of essential modules
            // 2. Pack them into a boot blob

            var nonAmdBlobs = new List<Blob>();
            var amdBlobs = new List<Blob>();

            var seen = this.FlattenModules(essential, nonAmdBlobs, amdBlobs, addName: combined);

            if (combined)
            {
                // FlattenModules will add in post-order, which will produce a reverse topological sort
                // of dependencies so that dependee is before dependent.
                nonAmdBlobs.Add(this.GenerateRequireJsMapping(
                    basePath,
                    includeRequireJsBlob: requireJsBlob,
                    exclude: seen));

                // TODO: Exclude mappings that can be found in the require.js mapping
                nonAmdBlobs.Add(bun.GenerateFileMappingFunction());
                
                var blob = Blob.Concat(nonAmdBlobs.Concat(amdBlobs).ToArray());
                bun.Store(blobPath, blob);
                        
                // TODO: This depends on a base path, but not necessarily "basePath"
                return new ScriptTagBlob(bun.GetBlobPath(blob, ""));
            }
            else
            {

                var blobs = nonAmdBlobs.Concat(amdBlobs);
                var tags = blobs.Select(b => (Blob)new ScriptTagBlob(bun.GetBlobPath(b, ""))).ToList();

                tags.Add(new ScriptTagBlob(scriptPath: requireJsBlob.OriginalVirtualPath));
                tags.Add(new ScriptTagBlob(
                    scriptBody: this.GenerateRequireJsMapping(
                        basePath,
                        includeRequireJsBlob: null,
                        exclude: new HashSet<string>())));

                return Blob.Concat(tags.ToArray());
            }
        }
    }
}