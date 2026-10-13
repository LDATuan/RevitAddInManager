using System.Collections.Concurrent;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace RevitAddinManager.Mcp;

public sealed record CompiledScript(byte[] Assembly, byte[] Pdb, IReadOnlyList<string> Warnings);

/// <summary>
/// Compiles C# snippets for execute_csharp against the exact assemblies the target Revit process has loaded,
/// so the snippet sees the same Revit API version and runtime. Compilation errors never reach Revit.
/// </summary>
public sealed class ScriptCompiler
{
    public const string ScriptFile = "script.csx";

    private static readonly string[] DefaultUsings =
    {
        "System", "System.Collections.Generic", "System.Linq", "System.Text", "System.Diagnostics",
        "Autodesk.Revit.DB", "Autodesk.Revit.UI", "Autodesk.Revit.DB.Architecture", "Autodesk.Revit.DB.Structure",
    };

    // Unreachable code after the user's own return statement is expected.
    private static readonly HashSet<string> IgnoredWarnings = new() { "CS0162" };

    private readonly ConcurrentDictionary<int, IReadOnlyList<MetadataReference>> referenceCache = new();

    /// <summary>
    /// References are fetched from the Revit process once and cached per pid.
    /// </summary>
    public async Task<IReadOnlyList<MetadataReference>> GetReferencesAsync(int pid, Func<Task<JsonNode?>> fetch)
    {
        if (referenceCache.TryGetValue(pid, out var cached)) return cached;
        var info = await fetch() ?? throw new InvalidOperationException("Revit returned no compile references.");
        return referenceCache[pid] = LoadReferences(info);
    }

    public CompiledScript Compile(string code, IEnumerable<string>? extraUsings, IReadOnlyList<MetadataReference> references,
        string revitVersion)
    {
        var usings = DefaultUsings.Concat(extraUsings ?? Enumerable.Empty<string>())
            .Select(u => u.Trim().TrimEnd(';'))
            .Select(u => u.StartsWith("using ") ? u + ";" : $"using {u};")
            .Distinct();

        var source = $$"""
            {{string.Join("\n", usings)}}

            public static class AiScript
            {
                public static object Run(UIApplication uiapp, UIDocument uidoc, Document doc)
                {
            #line 1 "{{ScriptFile}}"
            {{code}}
            #line default
                    return null;
                }
            }
            """;

        var symbols = new[] { "DEBUG", "TRACE", $"REVIT{revitVersion}", $"R{revitVersion[^2..]}" };
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8),
            new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: symbols), path: "AiScript.cs");

        var compilation = CSharpCompilation.Create(
            $"AiScript_{Guid.NewGuid():N}",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                platform: Platform.AnyCpu,
                nullableContextOptions: NullableContextOptions.Disable));

        using var dll = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

        var diagnostics = result.Diagnostics.Where(d => !IgnoredWarnings.Contains(d.Id)).ToList();
        if (!result.Success)
        {
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20).Select(Format);
            throw new ScriptCompilationException(string.Join("\n", errors));
        }

        var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).Take(10).Select(Format).ToList();
        return new CompiledScript(dll.ToArray(), pdb.ToArray(), warnings);
    }

    private static string Format(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetMappedLineSpan();
        var where = span.Path == ScriptFile
            ? $"line {span.StartLinePosition.Line + 1}, col {span.StartLinePosition.Character + 1}"
            : "usings";
        return $"{where}: {diagnostic.Id} {diagnostic.GetMessage()}";
    }

    private static IReadOnlyList<MetadataReference> LoadReferences(JsonNode info)
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in info["references"]!.AsArray().Select(x => x!.GetValue<string>()))
        {
            // First one wins: the runtime folder comes first, so framework assemblies are never shadowed.
            if (!File.Exists(path) || !seen.Add(Path.GetFileNameWithoutExtension(path)) || !HasMetadata(path)) continue;
            references.Add(MetadataReference.CreateFromFile(path));
        }
        return references;
    }

    private static bool HasMetadata(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}

public sealed class ScriptCompilationException(string message) : Exception(message);
