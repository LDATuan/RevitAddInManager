using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#if !(R25 || R26 || R27)
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace RevitAddinManager.Bridge;

/// <summary>
/// Local named-pipe endpoint that lets the out-of-process MCP server (AddInManager.Mcp) drive this Revit session.
/// Protocol: one JSON request line per connection, one JSON response line back.
/// Request: {"token": "...", "method": "...", "params": {...}}
/// Response: {"ok": true, "result": ...} or {"ok": false, "error": "..."}
/// </summary>
public sealed class BridgeServer
{
    public const string DisableEnvVar = "REVIT_ADDIN_MANAGER_BRIDGE";
    private static readonly TimeSpan RevitCallTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RunCallTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DetachRunAfter = TimeSpan.FromMinutes(2);
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    private readonly string pipeName;
    private readonly string token = Guid.NewGuid().ToString("N");
    private readonly string discoveryFile;
    private readonly string revitVersion;
    private readonly RevitDispatcher dispatcher;
    private readonly UiInspector ui;
    private readonly BridgeCommands commands;
    private readonly ConcurrentDictionary<string, PendingRun> pendingRuns = new ConcurrentDictionary<string, PendingRun>();
    private readonly CancellationTokenSource cts = new CancellationTokenSource();

    public static BridgeServer Instance { get; private set; }

    public static string DiscoveryFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAddinManager", "bridge");

    private BridgeServer(UIControlledApplication application)
    {
        var pid = Process.GetCurrentProcess().Id;
        pipeName = $"RevitAddinManager.Bridge.{pid}";
        discoveryFile = Path.Combine(DiscoveryFolder, $"{pid}.json");
        revitVersion = application.ControlledApplication.VersionNumber;
        dispatcher = new RevitDispatcher();
        ui = new UiInspector();
        commands = new BridgeCommands(ui, revitVersion);
        UndoTracker.Attach(application.ControlledApplication);
    }

    public static void Start(UIControlledApplication application)
    {
        if (Instance != null) return;
        if (Environment.GetEnvironmentVariable(DisableEnvVar) == "0") return;
        try
        {
            TraceBuffer.EnsureRegistered();
            var server = new BridgeServer(application);
            server.WriteDiscoveryFile();
            Task.Run(() => server.ListenLoop(server.cts.Token));
            Instance = server;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Bridge] Failed to start: {ex}");
            Instance = null;
        }
    }

    public static void Stop()
    {
        if (Instance == null) return;
        Instance.cts.Cancel();
        try
        {
            File.Delete(Instance.discoveryFile);
        }
        catch (IOException)
        {
        }
        Instance = null;
    }

    private void WriteDiscoveryFile()
    {
        Directory.CreateDirectory(DiscoveryFolder);
        var info = new
        {
            pid = Process.GetCurrentProcess().Id,
            pipe = pipeName,
            token,
            revitVersion,
            startedUtc = DateTime.UtcNow,
        };
        File.WriteAllText(discoveryFile, JsonConvert.SerializeObject(info, Formatting.Indented), Utf8);
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server = null;
            try
            {
                server = CreatePipe();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connected = server;
                server = null;
                _ = Task.Run(() => HandleConnection(connected, ct));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Bridge] Listen error: {ex.Message}");
                await Task.Delay(1000).ConfigureAwait(false);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
#if R25 || R26 || R27
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#else
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#endif
    }

    private async Task HandleConnection(NamedPipeServerStream stream, CancellationToken ct)
    {
        using (stream)
        {
            string response;
            try
            {
                var reader = new StreamReader(stream, Utf8);
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                response = await Dispatch(line, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response = Error(ex.Message);
            }

            try
            {
                var writer = new StreamWriter(stream, Utf8);
                await writer.WriteLineAsync(response).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                stream.WaitForPipeDrain();
            }
            catch (IOException)
            {
                // Client went away.
            }
        }
    }

    private async Task<string> Dispatch(string line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line)) return Error("Empty request");
        var request = JObject.Parse(line);
        if ((string)request["token"] != token) return Error("Invalid token");

        var method = (string)request["method"];
        var parameters = request["params"] as JObject ?? new JObject();
        if (method == "get_run_result") return Ok(await GetRunResult(parameters).ConfigureAwait(false));
        if (!commands.TryGet(method, out var command)) return Error($"Unknown method '{method}'");

        if (!command.NeedsRevitThread) return Ok(command.Handler(null, parameters));

        var blocking = pendingRuns.Values.FirstOrDefault(x => !x.Work.IsCompleted);
        if (blocking != null)
        {
            return Error($"Revit is still busy with run {blocking.Id} ({blocking.Method}), probably waiting on a dialog. " +
                         "Finish it with list_windows/ui_action, then get_run_result.");
        }

        if (command.RunsAddinCode) return Ok(await StartRun(method, command, parameters, ct).ConfigureAwait(false));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RevitCallTimeout);
        try
        {
            return Ok(await dispatcher.InvokeAsync(app => command.Handler(app, parameters), timeout.Token).ConfigureAwait(false));
        }
        catch (TaskCanceledException)
        {
            return Error("Timed out waiting for Revit. Revit may be busy or showing a modal dialog.");
        }
    }

    /// <summary>
    /// Runs add-in code, but returns early with a runId when the code opens a window (typically a modal dialog
    /// that would otherwise block forever) or simply runs for a long time.
    /// </summary>
    private async Task<object> StartRun(string method, BridgeCommand command, JObject parameters, CancellationToken ct)
    {
        var windowsBefore = ui.SnapshotWindowIds();
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RunCallTimeout);
        var work = dispatcher.InvokeAsync(app => command.Handler(app, parameters), timeout.Token);
        _ = work.ContinueWith(_ => timeout.Dispose(), TaskScheduler.Default);

        var started = Stopwatch.StartNew();
        while (true)
        {
            if (await Task.WhenAny(work, Task.Delay(500, ct)).ConfigureAwait(false) == work) return await work.ConfigureAwait(false);

            var newWindows = started.Elapsed > TimeSpan.FromSeconds(1) ? ui.FindNewWindows(windowsBefore) : new List<WindowInfo>();
            if (newWindows.Count == 0 && started.Elapsed < DetachRunAfter) continue;

            var run = new PendingRun(method, work, windowsBefore);
            pendingRuns[run.Id] = run;
            return run.Describe(newWindows);
        }
    }

    private async Task<object> GetRunResult(JObject parameters)
    {
        var runId = (string)parameters["runId"] ?? throw new InvalidOperationException("runId is required.");
        if (!pendingRuns.TryGetValue(runId, out var run)) throw new InvalidOperationException($"Unknown runId '{runId}'.");

        var wait = TimeSpan.FromSeconds(Math.Min((int?)parameters["waitSeconds"] ?? 20, 120));
        await Task.WhenAny(run.Work, Task.Delay(wait)).ConfigureAwait(false);
        if (!run.Work.IsCompleted) return run.Describe(ui.FindNewWindows(run.WindowsBefore));

        pendingRuns.TryRemove(runId, out _);
        return await run.Work.ConfigureAwait(false);
    }

    private static string Ok(object result) => JsonConvert.SerializeObject(new { ok = true, result });

    private static string Error(string message) => JsonConvert.SerializeObject(new { ok = false, error = message });
}

public sealed class PendingRun
{
    public PendingRun(string method, Task<object> work, HashSet<long> windowsBefore)
    {
        Method = method;
        Work = work;
        WindowsBefore = windowsBefore;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    public string Method { get; }
    public Task<object> Work { get; }
    public HashSet<long> WindowsBefore { get; }

    public object Describe(List<WindowInfo> windows) => new
    {
        status = windows.Count > 0 ? "waitingForUI" : "running",
        runId = Id,
        windows,
        hint = windows.Count > 0
            ? "The add-in opened a window and is waiting. Inspect it with capture_window/get_window_tree, interact with ui_action, then call get_run_result."
            : "Still running. Call get_run_result to wait for the result.",
    };
}
