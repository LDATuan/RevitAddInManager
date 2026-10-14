using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace RevitAddinManager.Mcp;

/// <summary>
/// Turns a bridge result into MCP content: the JSON as text, plus every PNG the bridge wrote attached as an image.
/// </summary>
public static class ToolResults
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static CallToolResult From(JsonNode? result, params (string Label, string Path)[] extraImages)
    {
        var images = new List<(string Label, string Path)>();
        if (result is JsonObject obj)
        {
            if (obj["path"] is JsonValue pathValue && pathValue.TryGetValue<string>(out var path) && path.EndsWith(".png"))
            {
                images.Add(("capture", path));
                obj["path"] = "attached below";
            }
            if (obj["screenshots"] is JsonArray screenshots)
            {
                foreach (var shot in screenshots.OfType<JsonObject>())
                {
                    var shotPath = shot["path"]?.GetValue<string>();
                    if (shotPath == null) continue;
                    images.Add(($"{shot["tag"]} ({shot["view"]})", shotPath));
                    shot["path"] = "attached below";
                }
            }
        }
        images.AddRange(extraImages);

        var content = new List<ContentBlock> { new TextContentBlock { Text = result?.ToJsonString(Indented) ?? "null" } };
        foreach (var (label, path) in images)
        {
            if (!File.Exists(path)) continue;
            content.Add(new TextContentBlock { Text = $"Image: {label}" });
            content.Add(ImageContentBlock.FromBytes(File.ReadAllBytes(path), "image/png"));
            TryDelete(path);
        }
        return new CallToolResult { Content = content };
    }

    public static CallToolResult Error(string message) =>
        new() { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = message } } };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            var folder = Path.GetDirectoryName(path);
            if (folder != null && !Directory.EnumerateFileSystemEntries(folder).Any() && Path.GetFileName(folder).Length == 32)
                Directory.Delete(folder);
        }
        catch (IOException)
        {
        }
    }
}
