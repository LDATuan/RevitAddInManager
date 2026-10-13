using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitAddinManager.Model;
using RevitElementBipChecker.Model;
#if R25 || R26 || R27
using System.Windows.Threading;
using RevitAddinManager.Command;
using AssemblyLoadContext = RevitAddinManager.Model.AssemblyLoadContext;
#endif

namespace RevitAddinManager.Bridge;

public sealed class BridgeCommand
{
    public BridgeCommand(bool needsRevitThread, Func<UIApplication, JObject, object> handler, bool runsAddinCode = false)
    {
        NeedsRevitThread = needsRevitThread;
        Handler = handler;
        RunsAddinCode = runsAddinCode;
    }

    public bool NeedsRevitThread { get; }

    /// <summary>
    /// Runs add-in code that may open its own UI; the bridge then returns early with a runId instead of blocking.
    /// </summary>
    public bool RunsAddinCode { get; }

    /// <summary>
    /// UIApplication is null when NeedsRevitThread is false.
    /// </summary>
    public Func<UIApplication, JObject, object> Handler { get; }
}

public sealed class BridgeCommands
{
    private const int MaxElements = 50;
    private const int MaxSelection = 500;
    private readonly Dictionary<string, BridgeCommand> commands;

    public BridgeCommands(UiInspector ui, string revitVersion)
    {
        commands = new Dictionary<string, BridgeCommand>(StringComparer.Ordinal)
        {
            ["status"] = new BridgeCommand(true, (app, _) => Status(app)),
            ["get_trace_log"] = new BridgeCommand(false, (_, p) => GetTraceLog(p)),
            ["list_commands"] = new BridgeCommand(true, (_, p) => ListCommands(p)),
            ["run_command"] = new BridgeCommand(true, RunCommand, runsAddinCode: true),
            ["execute_script"] = new BridgeCommand(true, ExecuteScript, runsAddinCode: true),
            ["get_compile_references"] = new BridgeCommand(false, (_, _) => ScriptRunner.GetCompileReferences(revitVersion)),
            ["undo_last_run"] = new BridgeCommand(true, (app, _) => UndoTracker.UndoLast(app)),
            ["capture_view"] = new BridgeCommand(true, CaptureView),
            ["get_selection"] = new BridgeCommand(true, (app, _) => GetSelection(app)),
            ["get_element_params"] = new BridgeCommand(true, GetElementParams),
            ["load_dev_pane"] = new BridgeCommand(true, (app, p) => DevPane.DevPaneManager.Load(app, (int?)p["slot"] ?? 1,
                (string)p["assemblyPath"] ?? throw new InvalidOperationException("assemblyPath is required."),
                (string)p["className"], (bool?)p["autoReload"], interactive: false)),
            ["list_dev_panes"] = new BridgeCommand(false, (_, _) => DevPane.DevPaneManager.Describe()),
            ["list_windows"] =new BridgeCommand(false, (_, p) => ui.ListWindows((bool?)p["includeRevit"] ?? false)),
            ["get_window_tree"] = new BridgeCommand(false, (_, p) => ui.GetTree(RequireLong(p, "windowId"))),
            ["capture_window"] = new BridgeCommand(false, (_, p) => new { path = ui.Capture(RequireLong(p, "windowId")) }),
            ["ui_action"] = new BridgeCommand(false, (_, p) => ui.Act(RequireLong(p, "windowId"), (int?)p["ref"],
                (string)p["name"], (string)p["text"], (string)p["action"] ?? "click", (string)p["value"])),
        };
    }

    public bool TryGet(string method, out BridgeCommand command) =>
        commands.TryGetValue(method ?? string.Empty, out command);

    private static object Status(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        return new
        {
            revitVersion = app.Application.VersionNumber,
            revitBuild = app.Application.VersionBuild,
            pid = Process.GetCurrentProcess().Id,
            addinManagerVersion = DefaultSetting.Version.Trim(),
            activeDocument = uidoc?.Document.Title,
            activeDocumentPath = uidoc?.Document.PathName,
            activeView = uidoc?.ActiveView?.Name,
            traceSeq = TraceBuffer.LastSeq,
        };
    }

    private static object GetTraceLog(JObject p)
    {
        var since = (long?)p["sinceSeq"] ?? 0;
        var max = Math.Min((int?)p["max"] ?? 200, 2000);
        var entries = TraceBuffer.ReadSince(since, max);
        return new
        {
            lastSeq = TraceBuffer.LastSeq,
            entries = entries.Select(x => new { seq = x.Seq, time = x.Time, message = x.Message }),
        };
    }

    private static object ListCommands(JObject p)
    {
        var path = RequireExistingFile(p);
#if R25 || R26 || R27
        var alc = new AssemblyLoadContext(path);
        try
        {
            var assembly = AddinManagerBase.Load(alc, path);
            return new { assemblyPath = path, commands = FindCommandTypes(assembly).Select(x => x.FullName).ToList() };
        }
        finally
        {
            UnloadLater(alc);
        }
#else
        var loader = new AssemLoader();
        try
        {
            loader.HookAssemblyResolve();
            var assembly = loader.LoadAddinsToTempFolder(path, true)
                           ?? throw new InvalidOperationException("Assembly could not be loaded or does not reference RevitAPI.");
            return new { assemblyPath = path, commands = FindCommandTypes(assembly).Select(x => x.FullName).ToList() };
        }
        finally
        {
            loader.UnhookAssemblyResolve();
        }
#endif
    }

    private static object RunCommand(UIApplication app, JObject p)
    {
        var path = RequireExistingFile(p);
        var className = (string)p["className"];
        var options = ReadRunOptions(p);

#if R25 || R26 || R27
        var alc = new AssemblyLoadContext(path);
        try
        {
            var assembly = AddinManagerBase.Load(alc, path);
            var type = ResolveCommandType(assembly, className);
            return RunSession.Run(app, type.FullName, options, () => Execute(app, type));
        }
        finally
        {
            UnloadLater(alc);
        }
#else
        var loader = new AssemLoader();
        try
        {
            loader.HookAssemblyResolve();
            var assembly = loader.LoadAddinsToTempFolder(path, false)
                           ?? throw new InvalidOperationException("Assembly could not be loaded or does not reference RevitAPI.");
            var type = ResolveCommandType(assembly, className);
            return RunSession.Run(app, type.FullName, options, () => Execute(app, type));
        }
        finally
        {
            loader.UnhookAssemblyResolve();
            loader.CopyGeneratedFilesBack();
        }
#endif
    }

    private static object ExecuteScript(UIApplication app, JObject p)
    {
        var assembly = Convert.FromBase64String((string)p["assemblyBase64"] ?? throw new InvalidOperationException("assemblyBase64 is required."));
        var pdbText = (string)p["pdbBase64"];
        var pdb = string.IsNullOrEmpty(pdbText) ? null : Convert.FromBase64String(pdbText);
        var useTransaction = (bool?)p["transaction"] ?? true;

        var body = ScriptRunner.Prepare(app, assembly, pdb, useTransaction, out var cleanup);
        try
        {
            return RunSession.Run(app, "execute_csharp", ReadRunOptions(p), body);
        }
        finally
        {
            cleanup();
        }
    }

    private static RunOutcome Execute(UIApplication app, Type type)
    {
        var command = (IExternalCommand)Activator.CreateInstance(type);

        // ExternalCommandData has no public constructor, but all its properties are settable.
        var data = (ExternalCommandData)Activator.CreateInstance(typeof(ExternalCommandData), nonPublic: true);
        data.Application = app;
        data.View = app.ActiveUIDocument?.ActiveView;
        data.JournalData = new Dictionary<string, string>();

        var message = string.Empty;
        var elements = app.Application.Create.NewElementSet();
        try
        {
            var result = command.Execute(data, ref message, elements);
            return new RunOutcome { Result = result.ToString(), Message = message };
        }
        catch (Exception ex)
        {
            return new RunOutcome { Result = "Exception", Message = message, Exception = ex.ToString() };
        }
    }

    private static RunOptions ReadRunOptions(JObject p) => new RunOptions
    {
        DryRun = (bool?)p["dryRun"] ?? false,
        Screenshot = (string)p["screenshot"] ?? "none",
        WatchIds = p["watchElementIds"] is JArray array && array.Count > 0
            ? array.Select(x => ToElementId((long)x)).ToList()
            : null,
    };

    private static object CaptureView(UIApplication app, JObject p)
    {
        var uidoc = RequireDocument(app);
        var doc = uidoc.Document;
        Autodesk.Revit.DB.View view;
        if (p["viewId"] != null)
        {
            view = doc.GetElement(ToElementId((long)p["viewId"])) as Autodesk.Revit.DB.View
                   ?? throw new InvalidOperationException("viewId is not a view.");
        }
        else
        {
            view = uidoc.ActiveView;
        }
        return new { view = view.Name, path = ViewCapture.Capture(doc, view, "view", (int?)p["pixelSize"] ?? 1400) };
    }

    private static object GetSelection(UIApplication app)
    {
        var uidoc = RequireDocument(app);
        var doc = uidoc.Document;
        var ids = uidoc.Selection.GetElementIds();
        return new
        {
            count = ids.Count,
            elements = ids.Take(MaxSelection).Select(id => doc.GetElement(id)).Where(e => e != null).Select(e => new
            {
                id = e.Id.ToString(),
                name = e.Name,
                category = e.Category?.Name,
                typeName = doc.GetElement(e.GetTypeId())?.Name,
                className = e.GetType().Name,
            }),
        };
    }

    private static object GetElementParams(UIApplication app, JObject p)
    {
        var uidoc = RequireDocument(app);
        var doc = uidoc.Document;
        var includeType = (bool?)p["includeType"] ?? true;

        var ids = p["elementIds"] is JArray array && array.Count > 0
            ? array.Select(x => ToElementId((long)x)).ToList()
            : uidoc.Selection.GetElementIds().ToList();
        if (ids.Count == 0) throw new InvalidOperationException("No elementIds given and nothing is selected in Revit.");
        if (ids.Count > MaxElements) throw new InvalidOperationException($"Too many elements ({ids.Count}); limit is {MaxElements}.");

        return ids.Select(id =>
        {
            var element = doc.GetElement(id);
            if (element == null) return (object)new { id = id.ToString(), error = "Element not found" };

            var parameters = ReadParameters(element, element, doc, true);
            var elementType = includeType ? doc.GetElement(element.GetTypeId()) : null;
            if (elementType != null) parameters.AddRange(ReadParameters(element, elementType, doc, false));

            return new
            {
                id = element.Id.ToString(),
                name = element.Name,
                category = element.Category?.Name,
                typeName = elementType?.Name,
                parameters,
            };
        }).ToList();
    }

    private static List<object> ReadParameters(Element owner, Element source, Document doc, bool isInstance)
    {
        var list = new List<object>();
        foreach (Parameter parameter in source.Parameters)
        {
            try
            {
                var data = new ParameterData(owner, parameter, doc, isInstance);
                list.Add(new
                {
                    data.Name,
                    data.TypeOrInstance,
                    data.BuiltInParameter,
                    data.Id,
                    data.Type,
                    data.Unit,
                    data.ReadWrite,
                    data.Value,
                    data.StringValue,
                    data.GroupName,
                    data.Shared,
                    data.GUID,
                });
            }
            catch (Exception ex)
            {
                list.Add(new { Name = parameter.Definition?.Name, Error = ex.Message });
            }
        }
        return list;
    }

    private static IEnumerable<Type> FindCommandTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(x => x != null).ToArray();
        }
        // Match by name: the add-in may see RevitAPIUI through a different load context.
        return types.Where(x => !x.IsAbstract && x.GetInterface(StaticUtil.CommandFullName) != null);
    }

    private static Type ResolveCommandType(Assembly assembly, string className)
    {
        var candidates = FindCommandTypes(assembly).ToList();
        if (!string.IsNullOrEmpty(className))
        {
            return candidates.FirstOrDefault(x => x.FullName == className)
                   ?? candidates.FirstOrDefault(x => x.Name == className)
                   ?? throw new InvalidOperationException(
                       $"IExternalCommand '{className}' not found. Available: {string.Join(", ", candidates.Select(x => x.FullName))}");
        }
        if (candidates.Count == 1) return candidates[0];
        throw new InvalidOperationException(candidates.Count == 0
            ? "No IExternalCommand found in assembly."
            : $"className is required, assembly has several commands: {string.Join(", ", candidates.Select(x => x.FullName))}");
    }

    private static string RequireExistingFile(JObject p)
    {
        var path = (string)p["assemblyPath"];
        if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("assemblyPath is required.");
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
        return path;
    }

    private static long RequireLong(JObject p, string name) =>
        (long?)p[name] ?? throw new InvalidOperationException($"{name} is required.");

    private static UIDocument RequireDocument(UIApplication app) =>
        app.ActiveUIDocument ?? throw new InvalidOperationException("No active document in Revit.");

    private static ElementId ToElementId(long value)
    {
#if R19 || R20 || R21 || R22 || R23
        return new ElementId((int)value);
#else
        return new ElementId(value);
#endif
    }

#if R25 || R26 || R27
    internal static void UnloadLater(AssemblyLoadContext alc)
    {
        alc.Unload();
        var alcWeakRef = new WeakReference(alc, trackResurrection: true);
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            for (var counter = 0; alcWeakRef.IsAlive && counter < 10; counter++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        });
    }
#endif
}
