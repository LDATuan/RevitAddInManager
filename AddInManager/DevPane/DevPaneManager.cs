using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitAddinManager.Model;
using Frame = System.Windows.Controls.Frame;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using TaskDialogResult = Autodesk.Revit.UI.TaskDialogResult;
using Timer = System.Threading.Timer;
using UserControl = System.Windows.Controls.UserControl;
#if R25 || R26 || R27
using AssemblyLoadContext = RevitAddinManager.Model.AssemblyLoadContext;
#endif

namespace RevitAddinManager.DevPane;

/// <summary>
/// Hot-reloadable dockable panes. Revit only lets a pane be registered once per session, so Add-in Manager registers
/// a few generic panes at startup and swaps the developer's freshly built pane content into them on demand,
/// without restarting Revit.
/// </summary>
public static class DevPaneManager
{
    public const int SlotCount = 3;
    private const string ProviderInterface = "Autodesk.Revit.UI.IDockablePaneProvider";
    private static readonly List<DevPaneSlot> Slots = new List<DevPaneSlot>();
    private static readonly Dictionary<int, FileSystemWatcher> Watchers = new Dictionary<int, FileSystemWatcher>();
    private static readonly Dictionary<int, Timer> Debounce = new Dictionary<int, Timer>();
    private static readonly ConcurrentQueue<Action<UIApplication>> Queue = new ConcurrentQueue<Action<UIApplication>>();
    private static ExternalEvent loadEvent;

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAddinManager", "devpanes.json");

    public static void Initialize(UIControlledApplication application)
    {
        for (var number = 1; number <= SlotCount; number++)
        {
            var slot = new DevPaneSlot(number);
            if (DockablePane.PaneIsRegistered(slot.Id)) continue;
            application.RegisterDockablePane(slot.Id, slot.Title, slot);
            slot.ReloadClicked += () => OnReloadClicked(slot);
            slot.LoadClicked += () => OnLoadClicked(slot);
            slot.AutoReloadChanged += enabled => SetAutoReload(slot, enabled);
            Slots.Add(slot);
        }
        loadEvent = ExternalEvent.Create(new QueueHandler());

        foreach (var config in ReadConfig())
        {
            var slot = Find(config.Slot);
            if (slot == null) continue;
            slot.Config = config;
            slot.ShowIdle();
            slot.SetAutoReload(config.AutoReload);
            if (config.AutoReload) Watch(slot);
        }
    }

    public static FrameworkElement GetRoot(int number) => Find(number)?.Root;

    public static IEnumerable<(int Slot, string TypeName)> LoadedPanes() =>
        Slots.Where(s => s.Current != null).Select(s => (s.Number, s.Current.TypeName)).ToList();

    public static string LastAssemblyPath(int number) => Find(number)?.Config?.AssemblyPath;

    public static string LastClassName(int number) => Find(number)?.Config?.ClassName;

    public static IEnumerable<object> Describe() => Slots.Select(s => (object)new
    {
        slot = s.Number,
        windowId = -s.Number,
        loaded = s.Current?.TypeName,
        version = s.Current?.Version,
        loadedAt = s.Current?.LoadedAt,
        assemblyPath = s.Current?.AssemblyPath ?? s.Config?.AssemblyPath,
        autoReload = s.Config?.AutoReload ?? false,
    });

    /// <summary>
    /// Loads the latest build of <paramref name="assemblyPath"/> into a slot. Must run in a Revit API context.
    /// </summary>
    public static object Load(UIApplication app, int number, string assemblyPath, string className, bool? autoReload, bool interactive)
    {
        var slot = Find(number) ?? throw new InvalidOperationException($"Dev pane slot must be 1..{SlotCount}.");
        var stopwatch = Stopwatch.StartNew();
        assemblyPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(assemblyPath)) throw new FileNotFoundException("File not found: " + assemblyPath);

        LoadedPane loaded;
        List<string> warnings;
        try
        {
            loaded = CreatePane(app, assemblyPath, className, interactive, out warnings);
            if (loaded == null) return new { slot = number, cancelled = true };
        }
        catch (Exception ex)
        {
            var error = (ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex).ToString();
            slot.ShowError($"Load failed, the previous pane (if any) is still shown:\n{error}");
            throw new InvalidOperationException(error);
        }

        Unload(slot);
        slot.Current = loaded;
        slot.ContentHost.Content = loaded.Content;
        slot.ShowLoaded(loaded, warnings);

        slot.Config = new DevPaneConfig
        {
            Slot = number,
            AssemblyPath = assemblyPath,
            ClassName = loaded.TypeName,
            AutoReload = autoReload ?? slot.Config?.AutoReload ?? false,
        };
        slot.SetAutoReload(slot.Config.AutoReload);
        if (slot.Config.AutoReload) Watch(slot);
        else StopWatching(slot);
        SaveConfig();

        try
        {
            app.GetDockablePane(slot.Id).Show();
        }
        catch (Exception ex)
        {
            warnings.Add("Could not show the pane: " + ex.Message);
        }

        return new
        {
            slot = number,
            windowId = -number,
            type = loaded.TypeName,
            version = loaded.Version,
            loadedInMs = stopwatch.ElapsedMilliseconds,
            autoReload = slot.Config.AutoReload,
            warnings,
        };
    }

    /// <summary>
    /// Queues a load for the next time Revit is idle; used from UI clicks and the file watcher, which are not API contexts.
    /// </summary>
    public static void RequestLoad(int number, string assemblyPath, string className, bool interactive)
    {
        Queue.Enqueue(app =>
        {
            try
            {
                Load(app, number, assemblyPath, className, null, interactive);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DevPane] Reload of slot {number} failed: {ex.Message}");
            }
        });
        loadEvent.Raise();
        WakeRevit();
    }

    private static LoadedPane CreatePane(UIApplication app, string assemblyPath, string className, bool interactive, out List<string> warnings)
    {
        warnings = new List<string>();
        WaitUntilReadable(assemblyPath);

#if R25 || R26 || R27
        var alc = new AssemblyLoadContext(assemblyPath, isCollectible: false);
        Assembly assembly;
        using (var stream = new MemoryStream(File.ReadAllBytes(assemblyPath)))
        {
            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            using var pdb = File.Exists(pdbPath) ? new MemoryStream(File.ReadAllBytes(pdbPath)) : null;
            assembly = alc.LoadFromStream(stream, pdb);
        }
        Action release = () => { };
        // XAML resolves assemblies by name; make that resolve inside the plugin's context while the pane is built.
        using var scope = alc.EnterContextualReflection();
#else
        var loader = new AssemLoader();
        loader.HookAssemblyResolve();
        var assembly = loader.LoadAddinsToTempFolder(assemblyPath, false)
                       ?? throw new InvalidOperationException("Assembly could not be loaded or does not reference RevitAPI.");
        // The pane keeps loading types lazily, so the resolver stays hooked until the pane is replaced.
        Action release = loader.UnhookAssemblyResolve;
#endif

        var candidates = FindCandidates(assembly);
        var type = ChooseType(candidates, className, interactive);
        if (type == null)
        {
            release();
            return null;
        }

        object provider = null;
        FrameworkElement content;
        var activator = new PaneActivator(app, assembly, warnings);
        try
        {
            var instance = activator.Create(type);
            if (instance is IDockablePaneProvider paneProvider)
            {
                var data = new DockablePaneProviderData();
                paneProvider.SetupDockablePane(data);
                content = data.FrameworkElement ?? data.FrameworkElementCreator?.CreateFrameworkElement()
                          ?? throw new InvalidOperationException($"{type.FullName}.SetupDockablePane did not set FrameworkElement.");
                provider = paneProvider;
            }
            else
            {
                content = (FrameworkElement)instance;
            }
        }
        catch
        {
            // The previous pane stays; this failed build must not keep reacting to Revit.
            activator.DetachEvents();
            release();
            throw;
        }

        // A Page only accepts a Window or Frame as parent; Revit does the same wrapping for real panes.
        if (content is Page page) content = new Frame { Content = page, NavigationUIVisibility = NavigationUIVisibility.Hidden };

        warnings.AddRange(CheckForStaleTypes(content, assembly));

        return new LoadedPane
        {
            AssemblyPath = assemblyPath,
            TypeName = type.FullName,
            Version = assembly.GetName().Version?.ToString(),
            LoadedAt = DateTime.Now,
            Provider = provider,
            Content = content,
            // Unsubscribe what the add-in's composer attached, so the previous build stops reacting to Revit.
            ReleaseLoader = () =>
            {
                activator.DetachEvents();
                release();
            },
        };
    }

    private static void Unload(DevPaneSlot slot)
    {
        var old = slot.Current;
        if (old == null) return;
        // Removing the content raises Unloaded, where panes should unsubscribe from Revit events.
        slot.ContentHost.Content = null;
        foreach (var disposable in new[] { old.Provider, old.Content, old.Content?.DataContext }.OfType<IDisposable>().Distinct())
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DevPane] Dispose failed: {ex.Message}");
            }
        }
        old.ReleaseLoader?.Invoke();
        slot.Current = null;
    }

    private static List<Type> FindCandidates(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray();
        }

        // Constructors with parameters are fine: PaneActivator builds those the way the add-in does.
        var concrete = types.Where(t => t.IsPublic && !t.IsAbstract && !t.ContainsGenericParameters && t.GetConstructors().Length > 0).ToList();
        // Providers first: without a class name, a provider is what the add-in registers, so it is the pane to pick.
        var providers = concrete.Where(IsProvider);
        var views = concrete.Where(t => typeof(UserControl).IsAssignableFrom(t) || typeof(Page).IsAssignableFrom(t));
        return providers.Concat(views).Distinct().ToList();
    }

    private static bool IsProvider(Type type) => type.GetInterface(ProviderInterface) != null;

    private static Type ChooseType(List<Type> candidates, string className, bool interactive)
    {
        if (!string.IsNullOrEmpty(className))
        {
            return candidates.FirstOrDefault(t => t.FullName == className)
                   ?? candidates.FirstOrDefault(t => t.Name == className)
                   ?? throw new InvalidOperationException(
                       $"'{className}' is not a public IDockablePaneProvider, UserControl or Page. " +
                       $"Candidates: {string.Join(", ", candidates.Select(t => t.FullName))}");
        }
        if (candidates.Count == 0)
            throw new InvalidOperationException("No public IDockablePaneProvider, UserControl or Page found.");
        if (candidates.Any(IsProvider)) candidates = candidates.Where(IsProvider).ToList();
        if (candidates.Count == 1) return candidates[0];
        if (!interactive)
            throw new InvalidOperationException($"className is required, candidates: {string.Join(", ", candidates.Select(t => t.FullName))}");

        var dialog = new TaskDialog("Dev Pane") { MainInstruction = "Which pane should be loaded?" };
        var links = new[] { TaskDialogCommandLinkId.CommandLink1, TaskDialogCommandLinkId.CommandLink2, TaskDialogCommandLinkId.CommandLink3, TaskDialogCommandLinkId.CommandLink4 };
        for (var i = 0; i < Math.Min(links.Length, candidates.Count); i++) dialog.AddCommandLink(links[i], candidates[i].Name, candidates[i].FullName);
        var result = dialog.Show();
        var index = result - TaskDialogResult.CommandLink1;
        return index >= 0 && index < candidates.Count ? candidates[index] : null;
    }

    /// <summary>
    /// Detects the classic hot-reload trap: XAML that instantiated types from an earlier build of the same assembly.
    /// WPF resolves pack URIs to the most recently loaded copy on both .NET Framework and .NET 8, so this should stay quiet;
    /// it is here to make the failure visible if a dependency or custom resource loader breaks that.
    /// </summary>
    private static IEnumerable<string> CheckForStaleTypes(FrameworkElement content, Assembly current)
    {
        var name = current.GetName().Name;
        var stale = new HashSet<string>();
        var stack = new Stack<object>();
        stack.Push(content);
        var visited = 0;
        while (stack.Count > 0 && visited++ < 5000)
        {
            var item = stack.Pop();
            if (item == null) continue;
            var assembly = item.GetType().Assembly;
            if (assembly != current && assembly.GetName().Name == name) stale.Add(item.GetType().FullName);
            if (item is FrameworkElement { DataContext: { } dataContext } && dataContext.GetType().Assembly != current &&
                dataContext.GetType().Assembly.GetName().Name == name)
                stale.Add(dataContext.GetType().FullName);
            if (item is DependencyObject dependencyObject)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(dependencyObject)) stack.Push(child);
            }
        }

        if (stale.Count == 0) yield break;
        yield return $"{stale.Count} type(s) came from an older build of {name} ({string.Join(", ", stale.Take(5))}). " +
                     "WPF resolved XAML against a previously loaded copy. Give every build a new assembly version, e.g. in the Debug " +
                     "configuration: <Deterministic>false</Deterministic> and <AssemblyVersion>1.0.*</AssemblyVersion>.";
    }

    private static void OnReloadClicked(DevPaneSlot slot)
    {
        var config = slot.Config;
        if (config?.AssemblyPath == null)
        {
            OnLoadClicked(slot);
            return;
        }
        RequestLoad(slot.Number, config.AssemblyPath, config.ClassName, interactive: true);
    }

    private static void OnLoadClicked(DevPaneSlot slot)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"{slot.Title}: choose the add-in .dll that contains the pane",
            Filter = "Assembly (*.dll)|*.dll",
            InitialDirectory = slot.Config?.AssemblyPath != null ? Path.GetDirectoryName(slot.Config.AssemblyPath) : null,
        };
        if (dialog.ShowDialog() != true) return;
        RequestLoad(slot.Number, dialog.FileName, null, interactive: true);
    }

    private static void SetAutoReload(DevPaneSlot slot, bool enabled)
    {
        if (slot.Config == null)
        {
            slot.SetAutoReload(false);
            return;
        }
        slot.Config.AutoReload = enabled;
        if (enabled) Watch(slot);
        else StopWatching(slot);
        SaveConfig();
    }

    private static void Watch(DevPaneSlot slot)
    {
        StopWatching(slot);
        var path = slot.Config.AssemblyPath;
        var folder = Path.GetDirectoryName(path);
        if (folder == null || !Directory.Exists(folder)) return;

        var watcher = new FileSystemWatcher(folder, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        // A build touches the file several times; reload once it has been quiet for a moment.
        var timer = new Timer(_ => RequestLoad(slot.Number, slot.Config.AssemblyPath, slot.Config.ClassName, interactive: false));
        FileSystemEventHandler changed = (_, _) => timer.Change(800, Timeout.Infinite);
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Renamed += (_, _) => timer.Change(800, Timeout.Infinite);
        watcher.EnableRaisingEvents = true;
        Watchers[slot.Number] = watcher;
        Debounce[slot.Number] = timer;
    }

    private static void StopWatching(DevPaneSlot slot)
    {
        if (Watchers.TryGetValue(slot.Number, out var watcher))
        {
            watcher.Dispose();
            Watchers.Remove(slot.Number);
        }
        if (Debounce.TryGetValue(slot.Number, out var timer))
        {
            timer.Dispose();
            Debounce.Remove(slot.Number);
        }
    }

    private static void WaitUntilReadable(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(150);
            }
        }
    }

    private static DevPaneSlot Find(int number) => Slots.FirstOrDefault(s => s.Number == number);

    private static List<DevPaneConfig> ReadConfig()
    {
        try
        {
            return File.Exists(ConfigPath)
                ? JsonConvert.DeserializeObject<List<DevPaneConfig>>(File.ReadAllText(ConfigPath)) ?? new List<DevPaneConfig>()
                : new List<DevPaneConfig>();
        }
        catch (Exception)
        {
            return new List<DevPaneConfig>();
        }
    }

    private static void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Slots.Where(s => s.Config != null).Select(s => s.Config), Formatting.Indented));
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[DevPane] Could not save config: {ex.Message}");
        }
    }

    /// <summary>
    /// Revit services external events from its idle loop, which may not run while Revit sits in the background
    /// (e.g. while the developer is in Visual Studio). Posting a no-op message makes it go idle again.
    /// </summary>
    private static void WakeRevit()
    {
        const uint WM_NULL = 0x0000;
        PostMessage(Process.GetCurrentProcess().MainWindowHandle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private sealed class QueueHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            while (Queue.TryDequeue(out var action)) action(app);
        }

        public string GetName() => "RevitAddinManager.DevPane";
    }
}
