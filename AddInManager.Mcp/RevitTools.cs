using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RevitAddinManager.Mcp;

[McpServerToolType]
public sealed class RevitTools(RevitBridgeClient client, ScriptCompiler compiler)
{
    internal static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(6);

    internal const string PidDescription =
        "Process id of the target Revit (from list_revit_instances). Omit to use the most recently started Revit.";

    private const string DryRunDescription =
        "Run, record every change with before/after parameter values, then roll everything back. The model is left untouched.";

    private const string ScreenshotDescription =
        "Attach images of the active view: none (default), after, or beforeAfter.";

    private const string WatchDescription =
        "Element ids whose parameters are snapshotted before the run, for before/after values without dryRun. Defaults to the selection.";

    private const string RunResultDescription =
        " Returns the Result, message, exception with stack trace, Debug/Trace output, Revit warnings, and the model changes " +
        "(added/modified/deleted elements, parameter diffs). A successful run is one undo step (undo_last_run). " +
        "If the code opens a window, returns early with status waitingForUI, a runId and a screenshot of the window: " +
        "drive it with ui_action, then call get_run_result.";

    [McpServerTool(Name = "list_revit_instances", ReadOnly = true)]
    [Description("List running Revit sessions that have Add-in Manager loaded, with pid and Revit version.")]
    public string ListRevitInstances() =>
        JsonSerializer.Serialize(
            client.ListInstances().Select(x => new { x.Pid, x.RevitVersion, x.StartedUtc }),
            ToolResults.Indented);

    [McpServerTool(Name = "revit_status", ReadOnly = true)]
    [Description("Get Revit version, the active document and view, and the current trace sequence number.")]
    public Task<string> Status([Description(PidDescription)] int? pid = null, CancellationToken ct = default) =>
        client.CallAsync("status", null, pid, QuickTimeout, ct);

    [McpServerTool(Name = "list_commands", ReadOnly = true)]
    [Description("List the IExternalCommand classes in a compiled add-in assembly (.dll).")]
    public Task<string> ListCommands(
        [Description("Absolute path to the add-in .dll, usually the project's bin output.")] string assemblyPath,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default) =>
        client.CallAsync("list_commands", new JsonObject { ["assemblyPath"] = assemblyPath }, pid, QuickTimeout, ct);

    [McpServerTool(Name = "run_command", Destructive = true)]
    [Description(
        "Hot-load an add-in .dll and execute one of its IExternalCommand classes inside Revit, without restarting Revit. " +
        "Rebuild the project first; the latest .dll on disk is loaded on every call." + RunResultDescription)]
    public async Task<CallToolResult> RunCommand(
        [Description("Absolute path to the add-in .dll.")] string assemblyPath,
        [Description("Full or short name of the IExternalCommand class. Optional when the assembly has only one command.")]
        string? className = null,
        [Description(DryRunDescription)] bool dryRun = false,
        [Description(ScreenshotDescription)] string screenshot = "none",
        [Description(WatchDescription)] long[]? watchElementIds = null,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default)
    {
        var instance = client.Resolve(pid);
        var parameters = RunParameters(dryRun, screenshot, watchElementIds);
        parameters["assemblyPath"] = assemblyPath;
        parameters["className"] = className;
        var result = await client.CallNodeAsync("run_command", parameters, instance, RunTimeout, ct);
        return await RunResultAsync(result, instance, ct);
    }

    [McpServerTool(Name = "execute_csharp", Destructive = true)]
    [Description(
        "Compile and run a C# snippet inside Revit, like a REPL for the Revit API. The snippet is a method body with " +
        "uiapp (UIApplication), uidoc (UIDocument) and doc (Document) in scope; `return` a value to get it back as JSON " +
        "(elements, ids, XYZ, lists and anonymous objects are converted). Usings for System, Linq, Autodesk.Revit.DB, " +
        "Autodesk.Revit.UI, DB.Architecture and DB.Structure are included. It is compiled against the exact Revit API of " +
        "the target Revit; compile errors come back with line numbers and nothing runs. Debug.WriteLine output is captured. " +
        "Use it to explore the API and the model before writing add-in code." + RunResultDescription)]
    public async Task<CallToolResult> ExecuteCsharp(
        [Description("C# statements forming the body of a method returning object.")] string code,
        [Description("Extra namespaces to import, e.g. [\"Autodesk.Revit.DB.Mechanical\"].")] string[]? usings = null,
        [Description("Wrap the code in a Transaction (default true). Set false for read-only code or when the code manages its own transactions.")]
        bool transaction = true,
        [Description(DryRunDescription)] bool dryRun = false,
        [Description(ScreenshotDescription)] string screenshot = "none",
        [Description(WatchDescription)] long[]? watchElementIds = null,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default)
    {
        var instance = client.Resolve(pid);
        var references = await compiler.GetReferencesAsync(instance.Pid,
            () => client.CallNodeAsync("get_compile_references", null, instance, QuickTimeout, ct));

        CompiledScript compiled;
        try
        {
            compiled = compiler.Compile(code, usings, references, instance.RevitVersion);
        }
        catch (ScriptCompilationException ex)
        {
            return ToolResults.Error($"Compilation failed (nothing was run in Revit):\n{ex.Message}");
        }

        var parameters = RunParameters(dryRun, screenshot, watchElementIds);
        parameters["assemblyBase64"] = Convert.ToBase64String(compiled.Assembly);
        parameters["pdbBase64"] = Convert.ToBase64String(compiled.Pdb);
        parameters["transaction"] = transaction;
        var result = await client.CallNodeAsync("execute_script", parameters, instance, RunTimeout, ct);
        if (compiled.Warnings.Count > 0 && result is JsonObject obj)
            obj["compilerWarnings"] = new JsonArray(compiled.Warnings.Select(w => (JsonNode)w).ToArray());
        return await RunResultAsync(result, instance, ct);
    }

    [McpServerTool(Name = "get_run_result")]
    [Description("Wait for a run that returned status waitingForUI or running, and get its final result.")]
    public async Task<CallToolResult> GetRunResult(
        [Description("runId returned by run_command or execute_csharp.")] string runId,
        [Description("Seconds to wait before returning the current status (max 120).")] int waitSeconds = 20,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default)
    {
        var instance = client.Resolve(pid);
        var result = await client.CallNodeAsync("get_run_result", new JsonObject { ["runId"] = runId, ["waitSeconds"] = waitSeconds },
            instance, TimeSpan.FromSeconds(waitSeconds + 30), ct);
        return await RunResultAsync(result, instance, ct);
    }

    [McpServerTool(Name = "undo_last_run", Destructive = true)]
    [Description("Undo the last successful run_command/execute_csharp as a single Revit undo step. Refused if the model changed since.")]
    public Task<string> UndoLastRun([Description(PidDescription)] int? pid = null, CancellationToken ct = default) =>
        client.CallAsync("undo_last_run", null, pid, QuickTimeout, ct);

    [McpServerTool(Name = "capture_view", ReadOnly = true)]
    [Description("Get an image of a Revit view (the active view by default) to see what the model looks like.")]
    public async Task<CallToolResult> CaptureView(
        [Description("Id of the view to capture. Omit for the active view.")] long? viewId = null,
        [Description("Image width in pixels (default 1400).")] int pixelSize = 1400,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default)
    {
        var parameters = new JsonObject { ["pixelSize"] = pixelSize };
        if (viewId != null) parameters["viewId"] = viewId;
        var result = await client.CallNodeAsync("capture_view", parameters, client.Resolve(pid), QuickTimeout, ct);
        return ToolResults.From(result);
    }

    [McpServerTool(Name = "get_trace_log", ReadOnly = true)]
    [Description("Read Debug/Trace output captured in Revit, oldest first. Pass the last seq you saw as sinceSeq to get only new lines.")]
    public Task<string> GetTraceLog(
        [Description("Only return entries with seq greater than this.")] long sinceSeq = 0,
        [Description("Maximum number of entries (default 200).")] int max = 200,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default) =>
        client.CallAsync("get_trace_log", new JsonObject { ["sinceSeq"] = sinceSeq, ["max"] = max }, pid, QuickTimeout, ct);

    [McpServerTool(Name = "get_selection", ReadOnly = true)]
    [Description("List the elements currently selected in Revit (id, name, category, type).")]
    public Task<string> GetSelection([Description(PidDescription)] int? pid = null, CancellationToken ct = default) =>
        client.CallAsync("get_selection", null, pid, QuickTimeout, ct);

    [McpServerTool(Name = "get_element_params", ReadOnly = true)]
    [Description(
        "Read all parameters of Revit elements: name, BuiltInParameter, storage type, unit, value, read-only, shared GUID. " +
        "Useful for finding the right BuiltInParameter or checking what a command changed.")]
    public Task<string> GetElementParams(
        [Description("Element ids to read (max 50). Omit to use the current selection in Revit.")] long[]? elementIds = null,
        [Description("Also include parameters of the element's type.")] bool includeType = true,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default)
    {
        var parameters = new JsonObject { ["includeType"] = includeType };
        if (elementIds is { Length: > 0 })
            parameters["elementIds"] = new JsonArray(elementIds.Select(x => (JsonNode)x).ToArray());
        return client.CallAsync("get_element_params", parameters, pid, QuickTimeout, ct);
    }

    private static JsonObject RunParameters(bool dryRun, string screenshot, long[]? watchElementIds)
    {
        if (screenshot is not ("none" or "after" or "beforeAfter"))
            throw new McpException("screenshot must be none, after or beforeAfter.");
        var parameters = new JsonObject { ["dryRun"] = dryRun, ["screenshot"] = screenshot };
        if (watchElementIds is { Length: > 0 })
            parameters["watchElementIds"] = new JsonArray(watchElementIds.Select(x => (JsonNode)x).ToArray());
        return parameters;
    }

    /// <summary>
    /// When add-in code is stuck on a window, attach a picture of it so the AI can decide what to click right away.
    /// </summary>
    private async Task<CallToolResult> RunResultAsync(JsonNode? result, RevitInstance instance, CancellationToken ct)
    {
        if (result?["status"]?.GetValue<string>() == "waitingForUI" && result["windows"] is JsonArray { Count: > 0 } windows)
        {
            var windowId = windows[0]!["Id"]?.GetValue<long>();
            if (windowId != null)
            {
                try
                {
                    var capture = await client.CallNodeAsync("capture_window", new JsonObject { ["windowId"] = windowId }, instance, QuickTimeout, ct);
                    var path = capture?["path"]?.GetValue<string>();
                    if (path != null) return ToolResults.From(result, ($"window {windowId}", path));
                }
                catch (McpException)
                {
                    // The run result is still useful without the picture.
                }
            }
        }
        return ToolResults.From(result);
    }
}
