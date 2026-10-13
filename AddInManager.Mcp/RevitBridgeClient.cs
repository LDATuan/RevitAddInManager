using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace RevitAddinManager.Mcp;

public sealed record RevitInstance(int Pid, string Pipe, string Token, string RevitVersion, DateTime StartedUtc);

/// <summary>
/// Talks to the BridgeServer running inside each Revit process (see AddInManager/Bridge).
/// Revit instances announce themselves through JSON files in %LOCALAPPDATA%\RevitAddinManager\bridge.
/// </summary>
public sealed class RevitBridgeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string DiscoveryFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAddinManager", "bridge");

    public List<RevitInstance> ListInstances()
    {
        var instances = new List<RevitInstance>();
        if (!Directory.Exists(DiscoveryFolder)) return instances;

        foreach (var file in Directory.GetFiles(DiscoveryFolder, "*.json"))
        {
            RevitInstance? instance;
            try
            {
                instance = JsonSerializer.Deserialize<RevitInstance>(File.ReadAllText(file), JsonOptions);
            }
            catch (Exception)
            {
                continue;
            }
            if (instance == null) continue;

            if (IsAlive(instance.Pid))
                instances.Add(instance);
            else
                TryDelete(file);
        }
        return instances.OrderByDescending(x => x.StartedUtc).ToList();
    }

    public async Task<string> CallAsync(string method, JsonObject? parameters, int? pid, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await CallNodeAsync(method, parameters, Resolve(pid), timeout, cancellationToken);
        return result?.ToJsonString(ToolResults.Indented) ?? "null";
    }

    public async Task<JsonNode?> CallNodeAsync(string method, JsonObject? parameters, RevitInstance instance, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["token"] = instance.Token,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        await using var pipe = new NamedPipeClientStream(".", instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, timeoutCts.Token);
        }
        catch (TimeoutException)
        {
            throw new McpException($"Could not connect to Revit {instance.RevitVersion} (pid {instance.Pid}).");
        }

        var writer = new StreamWriter(pipe, Utf8, leaveOpen: true);
        await writer.WriteLineAsync(request.ToJsonString());
        await writer.FlushAsync(timeoutCts.Token);

        using var reader = new StreamReader(pipe, Utf8);
        string? line;
        try
        {
            line = await reader.ReadLineAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException($"Timed out after {timeout.TotalSeconds:0}s waiting for Revit.");
        }
        if (line == null) throw new McpException("Revit closed the connection without a response.");

        var response = JsonNode.Parse(line)!.AsObject();
        if (response["ok"]?.GetValue<bool>() != true)
            throw new McpException(response["error"]?.GetValue<string>() ?? "Unknown error from Revit.");

        return response["result"];
    }

    public RevitInstance Resolve(int? pid)
    {
        var instances = ListInstances();
        if (instances.Count == 0)
            throw new McpException(
                "No running Revit with Add-in Manager found. Start Revit with RevitAddinManager loaded.");
        if (pid == null) return instances[0];
        return instances.FirstOrDefault(x => x.Pid == pid)
               ?? throw new McpException(
                   $"No Revit with pid {pid}. Running: {string.Join(", ", instances.Select(x => $"{x.Pid} (Revit {x.RevitVersion})"))}");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
