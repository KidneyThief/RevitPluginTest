using System.Reflection;
using System.Runtime.Loader;

namespace RevitPluginTest
{
    // Collectible so the Core assembly's file lock can be released and rebuilt
    // without restarting Revit. Load() always returns null so every dependency
    // (RevitAPI, the host assembly, the BCL) resolves against whatever is
    // already loaded in the default context instead of being duplicated here -
    // only Core's own assembly ends up living in this context.
    public sealed class PluginLoadContext : AssemblyLoadContext
    {
        public PluginLoadContext() : base(isCollectible: true)
        {
        }

        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}
