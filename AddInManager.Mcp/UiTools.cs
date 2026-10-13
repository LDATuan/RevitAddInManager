using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static RevitAddinManager.Mcp.RevitTools;

namespace RevitAddinManager.Mcp;

/// <summary>
/// See and drive add-in windows. These calls do not need Revit to be idle, so they work while
/// a command is blocked on its own modal dialog.
/// </summary>
[McpServerToolType]
public sealed class UiTools(RevitBridgeClient client)
{
    [McpServerTool(Name = "list_windows", ReadOnly = true)]
    [Description("List open add-in windows in Revit: WPF windows, WinForms forms, Win32 dialogs and TaskDialogs, with their windowId.")]
    public Task<string> ListWindows(
        [Description("Also list Revit's own WPF windows.")] bool includeRevit = false,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default) =>
        client.CallAsync("list_windows", new JsonObject { ["includeRevit"] = includeRevit }, pid, QuickTimeout, ct);

    [McpServerTool(Name = "reload_pane", Destructive = true)]
    [Description(
        "Hot-reload a dockable pane without restarting Revit. Revit allows registering a pane only once per session, so " +
        "Add-in Manager hosts the pane in one of its Dev Pane slots: it loads the latest build of the .dll, calls the pane's " +
        "IDockablePaneProvider.SetupDockablePane (or creates the UserControl/Page), swaps it in, shows the pane and returns a screenshot. " +
        "The old content is unloaded first (Unloaded event, Dispose). Afterwards use windowId -slot with get_window_tree, ui_action and capture_window. " +
        "Warnings report XAML that was resolved against an older build.")]
    public async Task<CallToolResult> ReloadPane(
        [Description("Absolute path to the add-in .dll containing the pane.")] string assemblyPath,
        [Description("IDockablePaneProvider, UserControl or Page class. Optional when the .dll has only one.")] string? className = null,
        [Description("Dev Pane slot 1-3.")] int slot = 1,
        [Description("Also reload automatically whenever the .dll is rebuilt.")] bool? autoReload = null,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default)
    {
        var instance = client.Resolve(pid);
        var parameters = new JsonObject { ["assemblyPath"] = assemblyPath, ["className"] = className, ["slot"] = slot };
        if (autoReload != null) parameters["autoReload"] = autoReload;
        var result = await client.CallNodeAsync("load_dev_pane", parameters, instance, QuickTimeout, ct);

        // Let WPF lay out and render the new content before taking the picture.
        await Task.Delay(500, ct);
        try
        {
            var capture = await client.CallNodeAsync("capture_window", new JsonObject { ["windowId"] = -slot }, instance, QuickTimeout, ct);
            var path = capture?["path"]?.GetValue<string>();
            if (path != null) return ToolResults.From(result, ($"Dev Pane {slot}", path));
        }
        catch (ModelContextProtocol.McpException ex) when (result is JsonObject obj)
        {
            obj["screenshot"] = ex.Message;
        }
        return ToolResults.From(result);
    }

    [McpServerTool(Name = "capture_window", ReadOnly = true)]
    [Description("Get an image of a window, e.g. to read a dialog or check an add-in's layout.")]
    public async Task<CallToolResult> CaptureWindow(
        [Description("windowId from list_windows; -1..-3 for Dev Panes.")] long windowId,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default)
    {
        var result = await client.CallNodeAsync("capture_window", new JsonObject { ["windowId"] = windowId }, client.Resolve(pid), QuickTimeout, ct);
        return ToolResults.From(result);
    }

    [McpServerTool(Name = "get_window_tree", ReadOnly = true)]
    [Description(
        "List the controls of a window (buttons, text boxes, combo boxes, lists, tabs, labels...) with their text, state and a ref " +
        "to target them with ui_action.")]
    public Task<string> GetWindowTree(
        [Description("windowId from list_windows; -1..-3 for Dev Panes.")] long windowId,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default) =>
        client.CallAsync("get_window_tree", new JsonObject { ["windowId"] = windowId }, pid, QuickTimeout, ct);

    [McpServerTool(Name = "ui_action", Destructive = true)]
    [Description(
        "Interact with a control in an add-in window, like a user would. Target the control by ref (from get_window_tree), " +
        "x:Name/AutomationId, or exact visible text. Actions: click, setText, select (list/combo/tab item by text or index), " +
        "check, uncheck, focus, close (closes the window). For a TaskDialog use click with value ok/cancel/yes/no/close/commandLink1..4.")]
    public Task<string> UiAction(
        [Description("windowId from list_windows; -1..-3 for Dev Panes.")] long windowId,
        [Description("click | setText | select | check | uncheck | focus | close")] string action = "click",
        [Description("ref of the control from get_window_tree.")] int? @ref = null,
        [Description("x:Name or AutomationId of the control.")] string? name = null,
        [Description("Exact visible text of the control, e.g. a button caption.")] string? text = null,
        [Description("Text for setText, item text or index for select, button for TaskDialog click.")] string? value = null,
        [Description(PidDescription)] int? pid = null,
        CancellationToken ct = default)
    {
        var parameters = new JsonObject
        {
            ["windowId"] = windowId,
            ["action"] = action,
            ["ref"] = @ref,
            ["name"] = name,
            ["text"] = text,
            ["value"] = value,
        };
        return client.CallAsync("ui_action", parameters, pid, QuickTimeout, ct);
    }
}
