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
        private Dictionary<string, Blob> mapping; // TODO: Store mapping as string -> BlobNode
        private Dictionary<string, Shim> shims;

        //private HashSet<string> flattened = new HashSet<string>();
        private Regex defineRegex;

        private Dictionary<Blob, BlobNode> blobNodes = new Dictionary<Blob, BlobNode>();

        public class BlobNode
        {
            public BlobNode(Blob blob, bool isAmd, int namePos, string moduleName)
            {
                this.Blob = blob;
                this.ModuleName = null;
                this.IsAmd = isAmd;
                this.NamePos = namePos;
                this.ModuleName = moduleName;
            }

            public Blob Blob;
            public string ModuleName;
            public readonly bool IsAmd;
            public readonly int NamePos; // Position where name would be added
            public readonly List<BlobNode> Dependencies = new List<BlobNode>();
            public bool Flattened = false;
        }

        public RequireBuilder(
            BunBuilder bun,
            Dictionary<string, Blob> mapping,
            Dictionary<string, Shim> shims,
            IEnumerable<string> neverFlatten = null)
        {
            this.bun = bun;
            this.mapping = mapping;
            this.shims = shims;
            /* TODO: Handle neverFlatten
            if (neverFlatten != null)
            {
                // Pretend they are already flattened
                this.flattened = new HashSet<string>(neverFlatten);
            }
            */

            var ws = @"\s*";
            var quote = "[\"']";

            defineRegex = new Regex(
                @"define\(" + ws
                    + "()(?:" + ws + quote + "(.*?)" + quote + ws + "," + ")?" // Optional name
                    + ws + @"\[(.*?)\]"
                    + ws + ",",
                    RegexOptions.Compiled | RegexOptions.CultureInvariant);

            // Parse everything in mapping
            // TODO: Have a CreateNode that creates a node with a context name. GetNode should not create nodes.
            foreach (var m in mapping)
            {
                var node = this.GetNode(m.Value, m.Key);
                if (neverFlatten != null)
                    node.Flattened = neverFlatten.Contains(node.ModuleName);
            }
        }

        private static string UnifyModuleNames(string currentName, string newName)
        {
            if (currentName == null)
                return newName;
            else if (newName == null || newName == currentName)
                return currentName;
            else
                throw new ApplicationException("Module referred to as '" + newName + "' has the name '" + currentName + "' elsewhere.");
        }

        public BlobNode GetNode(string name)
        {
            Blob blob;
            if (!this.mapping.TryGetValue(name, out blob))
            {
                throw new ApplicationException("Module '" + name + "' is missing from mapping but used as a dependency");
            }

            return this.GetNode(blob, name);
        }

        public BlobNode GetNode(Blob blob, string nameInContext = null)
        {
            BlobNode node;
            if (!this.blobNodes.TryGetValue(blob.OriginalBlob, out node))
            {
                //

                var strData = blob.String;
                var defineParse = defineRegex.Match(strData);

                bool isAmd = false;
                int namePos = -1;
                string moduleName = nameInContext;

                if (defineParse.Success)
                {
                    isAmd = true;

                    if (defineParse.Groups[2].Success)
                    {
                        string nameInBlob = defineParse.Groups[2].Value;

                        moduleName = UnifyModuleNames(nameInBlob, moduleName);
                    }
                    else
                    {
                        // If the bundle does not have a name, give it one.
                        namePos = defineParse.Groups[1].Index;

                        
                    }
                }

                node = new BlobNode(blob, isAmd, namePos, moduleName);
                this.blobNodes[blob] = node; // Add first to handle cycles

                // TODO: What if we don't have a module name yet?
                Shim shim;
                if (this.shims.TryGetValue(node.ModuleName, out shim))
                {
                    node.Dependencies.AddRange(shim.deps.Select(d => this.GetNode(d)));
                }

                if (defineParse.Success)
                {
                    foreach (var dep in defineParse.Groups[3].Value.Split(new[]{','}, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var n = dep.Trim();
                        var name = n.Substring(1, n.Length - 2); // Remove quotes

                        // Ignore built-in modules
                        if (name != "require" && name != "exports")
                        {
                            node.Dependencies.Add(this.GetNode(name));
                        }
                    }
                }
            }
            else
            {
                node.ModuleName = UnifyModuleNames(node.ModuleName, nameInContext);
            }

            return node;
        }

        public Blob BlobWithName(BlobNode node)
        {
            if (node.IsAmd)
            {
                var strData = node.Blob.String;

                // TODO: This loses the connection with the original blob. Is it needed ever?
                return new StringBlob(
                    strData.Substring(0, node.NamePos) + '"' + node.ModuleName + "\", " + strData.Substring(node.NamePos),
                    node.Blob.MimeType); 
            }
            else
            {
                return node.Blob;
            }
        }

        protected void TraverseModuleTree(
            BlobNode node,
            HashSet<BlobNode> seen,
            Action<BlobNode> visitor)
        {
            if (seen.Contains(node))
                return;

            visitor(node);
            seen.Add(node);

            foreach (var dep in node.Dependencies)
            {
                TraverseModuleTree(dep, seen, visitor);
            }
        }
        
        public Blob GenerateRequireJsMapping(
            string basePath,
            Blob includeRequireJsBlob,
            HashSet<string> exclude)
        {
            var dest = new StringBuilder();

            if (includeRequireJsBlob != null)
            {
                dest.Append(includeRequireJsBlob.String);
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

        protected HashSet<BlobNode> FlattenModules(string[] modules, List<Blob> nonAmdBlobs, List<Blob> amdBlobs, bool addName)
        {
            var seen = new HashSet<BlobNode>();

            foreach (var m in modules)
            {
                var node = this.GetNode(m);

                TraverseModuleTree(
                    node,
                    seen: seen,
                    visitor: n =>
                    {
                        if (n.Flattened)
                            return; // SKIP since we already have this flattened.

                        if (n.IsAmd)
                            amdBlobs.Add(addName ? this.BlobWithName(n) : n.Blob);
                        else
                            nonAmdBlobs.Add(n.Blob);

                        n.Flattened = true;
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
                    exclude: new HashSet<string>(seen.Select(n => n.ModuleName))));

                // Exclude mappings that can be found in the require.js mapping already
                nonAmdBlobs.Add(bun.GenerateFileMappingFunction(
                    this.mapping
                        .Select(kv => kv.Value.OriginalVirtualPathMaybe)
                        .Where(p => p != null)));
                
                var blob = Blob.Concat(nonAmdBlobs.Concat(amdBlobs).ToArray());
                bun.Store(blobPath, blob);
                        
                // TODO: This depends on the HTML base path relative to the virtual path hierarchy, which isn't the same as 'basePath'.
                // We assume it's "" for now.
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