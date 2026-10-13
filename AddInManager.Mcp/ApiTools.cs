using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RevitAddinManager.Mcp;

/// <summary>
/// Revit API knowledge that needs no running Revit: version compatibility and API lookup,
/// read from RevitAPI.dll/.xml of installed Revit or the Nice3point reference packages.
/// </summary>
[McpServerToolType]
public sealed class ApiTools(ApiCatalog catalog)
{

    [McpServerTool(Name = "check_api_compat", ReadOnly = true)]
    [Description(
        "Check a compiled add-in .dll against the Revit API of several Revit versions without opening Revit. " +
        "Reports every Revit API type/member the add-in uses that was removed, changed signature, or is obsolete in each version, " +
        "with the obsolete message (often naming the replacement) and where it is used (method, and file:line when a portable PDB is present). " +
        "Revit versions that are not installed are read from the Nice3point.Revit.Api reference packages, downloaded once from nuget.org.")]
    public async Task<string> CheckApiCompat(
        [Description("Absolute path to the add-in .dll.")] string assemblyPath,
        [Description("Revit versions to check, e.g. [2023, 2025, 2027]. Default: 2019-2027.")] int[]? versions = null,
        [Description("Allow downloading reference packages for versions that are not installed or cached.")] bool download = true)
    {
        if (!File.Exists(assemblyPath)) throw new McpException($"File not found: {assemblyPath}");
        var scan = CompatChecker.Scan(assemblyPath);
        var targets = (versions is { Length: > 0 } ? versions : Enumerable.Range(ApiCatalog.MinVersion, ApiCatalog.MaxVersion - ApiCatalog.MinVersion + 1))
            .Distinct().OrderBy(v => v).ToList();

        var indexes = await Task.WhenAll(targets.Select(async v =>
        {
            try
            {
                return (Version: v, Index: await catalog.GetAsync(v, download), Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (Version: v, Index: (ApiIndex?)null, Error: ex.Message);
            }
        }));

        var results = indexes.Select(x => x.Index != null
            ? CompatChecker.Check(scan, x.Index)
            : new { version = x.Version, status = "unavailable", error = x.Error ?? "RevitAPI.dll not found for this version." }).ToList();

        var summary = string.Join(" | ", results.Select(r =>
        {
            var json = JsonSerializer.SerializeToElement(r);
            var status = json.GetProperty("status").GetString();
            var errors = json.TryGetProperty("errors", out var e) ? e.GetArrayLength() : 0;
            var warnings = json.TryGetProperty("warnings", out var w) ? w.GetArrayLength() : 0;
            return $"{json.GetProperty("version")}: {status}" + (errors + warnings > 0 ? $" ({errors} errors, {warnings} warnings)" : "");
        }));

        return JsonSerializer.Serialize(new
        {
            assembly = scan.AssemblyPath,
            builtAgainst = scan.BuiltAgainst,
            revitApisUsed = scan.Usages.Count,
            pdb = scan.Pdb,
            summary,
            results,
        }, ToolResults.Indented);
    }

    [McpServerTool(Name = "revit_api_lookup", ReadOnly = true)]
    [Description(
        "Look up the real Revit API instead of guessing: a type (\"Wall\", \"Autodesk.Revit.DB.ElementId\") lists its members; " +
        "a member (\"Wall.Create\", \"ElementId.Value\", \"FilteredElementCollector.OfClass\") shows every overload with its exact signature, " +
        "obsolete message and documentation. Pass compareVersions to see in which Revit versions it exists or is obsolete.")]
    public async Task<string> RevitApiLookup(
        [Description("Type or Type.Member, short or fully qualified.")] string query,
        [Description("Revit version to read. Default: newest installed Revit, else 2027.")] int? version = null,
        [Description("Other versions to compare availability with, e.g. [2022, 2024, 2026].")] int[]? compareVersions = null)
    {
        var primaryVersion = version ?? ApiCatalog.InstalledVersions().DefaultIfEmpty(ApiCatalog.MaxVersion).Max();
        var index = await catalog.GetAsync(primaryVersion, true) ?? throw new McpException($"Revit API {primaryVersion} is not available.");
        var (type, memberName) = Resolve(index, query);

        object details;
        if (memberName == null)
        {
            index.Docs.TryGetValue($"T:{type.FullName}", out var typeDoc);
            details = new
            {
                type = type.FullName,
                baseType = type.BaseType,
                obsolete = type.Obsolete,
                doc = Trim(typeDoc, 1200),
                members = type.Members
                    .Where(m => m.Kind != "method" || !(m.Name.StartsWith("get_") || m.Name.StartsWith("set_") || m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")))
                    .GroupBy(m => m.Kind)
                    .ToDictionary(g => g.Key, g => g.Select(m => m.Display + (m.Obsolete != null ? "  [obsolete]" : "")).Distinct().Take(150)),
            };
        }
        else
        {
            var members = index.FindMembers(type.FullName, memberName).ToList();
            if (members.Count == 0) return await RemovedMemberHistory(type, memberName, primaryVersion);
            var docPrefixes = new[] { "M", "P", "F", "E" }.Select(k => $"{k}:{type.FullName.Replace('+', '.')}.{(memberName == ".ctor" ? "#ctor" : memberName)}").ToList();
            details = new
            {
                type = type.FullName,
                member = memberName,
                overloads = members.Select(m => new { signature = m.Display, kind = m.Kind, obsolete = m.Obsolete }),
                docs = index.Docs
                    .Where(d => docPrefixes.Any(p => d.Key == p || d.Key.StartsWith(p + "(")))
                    .Take(10)
                    .Select(d => new { id = d.Key, doc = Trim(d.Value, 1500) }),
            };
        }

        object? comparison = null;
        if (compareVersions is { Length: > 0 })
        {
            comparison = await Task.WhenAll(compareVersions.Distinct().OrderBy(v => v).Select(async v =>
            {
                var other = await catalog.GetAsync(v, true);
                if (other == null) return (object)new { version = v, status = "unavailable" };
                if (!other.Types.TryGetValue(type.FullName, out var otherType)) return new { version = v, status = "type missing" };
                if (memberName == null) return new { version = v, status = otherType.Obsolete != null ? $"obsolete: {otherType.Obsolete}" : "present" };
                var overloads = other.FindMembers(type.FullName, memberName).ToList();
                return new
                {
                    version = v,
                    status = overloads.Count == 0 ? "member missing" : overloads.All(o => o.Obsolete != null) ? "obsolete" : "present",
                    overloads = overloads.Select(o => o.Display + (o.Obsolete != null ? $"  [obsolete: {o.Obsolete}]" : "")).Distinct(),
                };
            }));
        }

        return JsonSerializer.Serialize(new { revitVersion = primaryVersion, source = index.Source.Origin, details, comparison }, ToolResults.Indented);
    }

    /// <summary>
    /// The member does not exist in this version: walk back through older versions to find when it was removed
    /// and what its obsolete message said to use instead.
    /// </summary>
    private async Task<string> RemovedMemberHistory(ApiType type, string memberName, int version)
    {
        for (var older = version - 1; older >= ApiCatalog.MinVersion; older--)
        {
            var index = await catalog.GetAsync(older, true);
            var overloads = index?.FindMembers(type.FullName, memberName).ToList();
            if (overloads is not { Count: > 0 }) continue;
            return JsonSerializer.Serialize(new
            {
                type = type.FullName,
                member = memberName,
                status = $"Not in Revit {version}. Last present in Revit {older}.",
                lastSeen = overloads.Select(o => new { signature = o.Display, obsolete = o.Obsolete }),
                hint = "The obsolete message usually names the replacement API; look it up for the current version.",
            }, ToolResults.Indented);
        }
        throw new McpException($"{TypeNames.Short(type.FullName)} has no member '{memberName}' in Revit {ApiCatalog.MinVersion}-{version}.");
    }

    private static (ApiType Type, string? Member) Resolve(ApiIndex index, string query)
    {
        query = query.Trim().Replace("()", string.Empty);
        if (FindType(index, query) is { } exact) return (exact, null);

        var dot = query.LastIndexOf('.');
        if (dot > 0 && FindType(index, query[..dot]) is { } owner)
        {
            var member = query[(dot + 1)..];
            return (owner, member is "ctor" or "new" or "#ctor" ? ".ctor" : member);
        }
        throw new McpException($"No Revit API type matches '{query}'.");
    }

    private static ApiType? FindType(ApiIndex index, string name)
    {
        if (index.Types.TryGetValue(name, out var type)) return type;
        var matches = index.Types.Values
            .Where(t => t.FullName.EndsWith("." + name) || t.FullName.EndsWith("+" + name))
            .OrderBy(t => t.FullName.StartsWith("Autodesk.Revit.DB.") ? 0 : 1)
            .ThenBy(t => t.FullName.Length)
            .ToList();
        return matches.FirstOrDefault();
    }

    private static string? Trim(string? text, int max) =>
        text == null || text.Length <= max ? text : text[..max] + "...";
}
