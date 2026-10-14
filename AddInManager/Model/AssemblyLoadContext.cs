#if R25 || R26 || R27
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace RevitAddinManager.Model;

class AssemblyLoadContext : System.Runtime.Loader.AssemblyLoadContext
{
    /// <summary>
    /// Dependencies of dev panes, by assembly full name (which includes the version), shared by every reload.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Assembly> SharedDependencies = new ConcurrentDictionary<string, Assembly>();

    private readonly bool shareDependencies;

    public AssemblyLoadContext() : base(name: "RevitAddinManager", isCollectible: true)
    {
    }
    private AssemblyDependencyResolver _resolver;

    public AssemblyLoadContext(string pluginPath): this(pluginPath, isCollectible: true)
    {
    }

    /// <summary>
    /// Dev panes use a non-collectible context: WPF keeps references to loaded types, so unloading would not work anyway.
    /// They also share dependencies across reloads (only the pane's own assembly is loaded again): with a copy of, say,
    /// a control library per reload, XAML from one copy binds to attached properties registered by another, and
    /// templates silently lose those values (icons disappear).
    /// </summary>
    public AssemblyLoadContext(string pluginPath, bool isCollectible): base(name: "RevitAddinManager", isCollectible: isCollectible)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
        shareDependencies = !isCollectible;
    }

    protected override Assembly Load(AssemblyName assemblyName)
    {
        string assemblyPath = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            if(assemblyPath.Contains("RevitAPI"))
            {
                return null;
            }

            if (shareDependencies)
            {
                return SharedDependencies.GetOrAdd(AssemblyName.GetAssemblyName(assemblyPath).FullName, _ => LoadCopy(assemblyPath));
            }

            return LoadCopy(assemblyPath);
        }

        return null;
    }

    private Assembly LoadCopy(string assemblyPath)
    {
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read);
        return LoadFromStream(stream);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string libraryPath = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        return IntPtr.Zero;
    }

}
#endif
