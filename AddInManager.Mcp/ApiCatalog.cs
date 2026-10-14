using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace RevitAddinManager.Mcp;

public sealed record ApiSource(int Version, string ApiDll, string? UiDll, string? ApiXml, string? UiXml, string Origin);

public sealed class ApiMember
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required IReadOnlyList<string> Parameters { get; init; }
    public required string Type { get; init; }
    public string? Obsolete { get; init; }
    public bool ObsoleteIsError { get; init; }

    /// <summary>Key used to match member references: name(params):return.</summary>
    public string Key => $"{Name}({string.Join(",", Parameters)}):{Type}";

    public string Display => Kind switch
    {
        "ctor" => $"new({string.Join(", ", Parameters.Select(TypeNames.Short))})",
        "method" => $"{TypeNames.Short(Type)} {Name}({string.Join(", ", Parameters.Select(TypeNames.Short))})",
        _ => $"{Kind} {TypeNames.Short(Type)} {Name}",
    };
}

public sealed class ApiType
{
    public required string FullName { get; init; }
    public string? BaseType { get; init; }
    public string? Obsolete { get; init; }
    public bool ObsoleteIsError { get; init; }
    public List<ApiMember> Members { get; } = new();
}

/// <summary>
/// Public surface of RevitAPI/RevitAPIUI for one Revit version, read straight from metadata (no Revit needed).
/// </summary>
public sealed class ApiIndex
{
    private Dictionary<string, string>? docs;

    private ApiIndex(ApiSource source)
    {
        Source = source;
    }

    public ApiSource Source { get; }
    public Dictionary<string, ApiType> Types { get; } = new(StringComparer.Ordinal);

    public static ApiIndex Load(ApiSource source)
    {
        var index = new ApiIndex(source);
        index.Read(source.ApiDll);
        if (source.UiDll != null) index.Read(source.UiDll);
        return index;
    }

    /// <summary>Members with this name on the type or any of its base types.</summary>
    public IEnumerable<ApiMember> FindMembers(string typeName, string memberName)
    {
        var name = typeName;
        var guard = 0;
        while (name != null && Types.TryGetValue(name, out var type) && guard++ < 50)
        {
            foreach (var member in type.Members.Where(m => m.Name == memberName)) yield return member;
            name = type.BaseType;
        }
    }

    public IReadOnlyDictionary<string, string> Docs => docs ??= LoadDocs();

    private void Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var provider = new SignatureNameProvider();

        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
            if (visibility != TypeAttributes.Public && visibility != TypeAttributes.NestedPublic) continue;

            var (obsolete, obsoleteIsError) = ReadObsolete(reader, definition.GetCustomAttributes(), provider);
            var type = new ApiType
            {
                FullName = SignatureNameProvider.TypeDefinitionName(reader, handle),
                BaseType = definition.BaseType.IsNil ? null : SignatureNameProvider.EntityName(reader, definition.BaseType, provider),
                Obsolete = obsolete,
                ObsoleteIsError = obsoleteIsError,
            };

            // [Obsolete] usually sits on the property, while add-ins call its get_/set_ accessor.
            var accessorObsolete = new Dictionary<MethodDefinitionHandle, (string? Message, bool IsError)>();
            foreach (var propertyHandle in definition.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                var obsoleteInfo = ReadObsolete(reader, property.GetCustomAttributes(), provider);
                if (obsoleteInfo.Message == null) continue;
                var accessors = property.GetAccessors();
                if (!accessors.Getter.IsNil) accessorObsolete[accessors.Getter] = obsoleteInfo;
                if (!accessors.Setter.IsNil) accessorObsolete[accessors.Setter] = obsoleteInfo;
            }

            foreach (var methodHandle in definition.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (!IsVisible(method.Attributes & MethodAttributes.MemberAccessMask)) continue;
                var signature = method.DecodeSignature(provider, null);
                var name = reader.GetString(method.Name);
                var (mObsolete, mError) = ReadObsolete(reader, method.GetCustomAttributes(), provider);
                if (mObsolete == null && accessorObsolete.TryGetValue(methodHandle, out var inherited)) (mObsolete, mError) = inherited;
                type.Members.Add(new ApiMember
                {
                    Name = name,
                    Kind = name is ".ctor" or ".cctor" ? "ctor" : "method",
                    Parameters = signature.ParameterTypes,
                    Type = signature.ReturnType,
                    Obsolete = mObsolete,
                    ObsoleteIsError = mError,
                });
            }

            foreach (var fieldHandle in definition.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if (!IsVisible((MethodAttributes)(field.Attributes & FieldAttributes.FieldAccessMask))) continue;
                var (fObsolete, fError) = ReadObsolete(reader, field.GetCustomAttributes(), provider);
                type.Members.Add(new ApiMember
                {
                    Name = reader.GetString(field.Name),
                    Kind = "field",
                    Parameters = ImmutableArray<string>.Empty,
                    Type = field.DecodeSignature(provider, null),
                    Obsolete = fObsolete,
                    ObsoleteIsError = fError,
                });
            }

            foreach (var propertyHandle in definition.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                var accessors = property.GetAccessors();
                var getter = accessors.Getter.IsNil ? accessors.Setter : accessors.Getter;
                if (getter.IsNil || !IsVisible(reader.GetMethodDefinition(getter).Attributes & MethodAttributes.MemberAccessMask)) continue;
                var (pObsolete, pError) = ReadObsolete(reader, property.GetCustomAttributes(), provider);
                type.Members.Add(new ApiMember
                {
                    Name = reader.GetString(property.Name),
                    Kind = "property",
                    Parameters = ImmutableArray<string>.Empty,
                    Type = property.DecodeSignature(provider, null).ReturnType,
                    Obsolete = pObsolete,
                    ObsoleteIsError = pError,
                });
            }

            Types[type.FullName] = type;
        }
    }

    private static bool IsVisible(MethodAttributes access) =>
        access is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;

    private static (string? Message, bool IsError) ReadObsolete(MetadataReader reader, CustomAttributeHandleCollection attributes,
        SignatureNameProvider provider)
    {
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            var attributeType = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => SignatureNameProvider.EntityName(reader, reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent, provider),
                HandleKind.MethodDefinition => SignatureNameProvider.TypeDefinitionName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
                _ => null,
            };
            if (attributeType != "System.ObsoleteAttribute") continue;

            var value = attribute.DecodeValue(provider);
            var message = value.FixedArguments.Length > 0 ? value.FixedArguments[0].Value as string : null;
            var isError = value.FixedArguments.Length > 1 && value.FixedArguments[1].Value is true;
            return (message ?? "Obsolete", isError);
        }
        return (null, false);
    }

    private Dictionary<string, string> LoadDocs()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in new[] { Source.ApiXml, Source.UiXml })
        {
            if (path == null || !File.Exists(path)) continue;
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.ReadToFollowing("member"))
            {
                var name = reader.GetAttribute("name");
                if (name == null) continue;
                var inner = reader.ReadInnerXml();
                result[name] = CleanDoc(inner);
            }
        }
        return result;
    }

    private static string CleanDoc(string xml)
    {
        // <see cref="T:Autodesk.Revit.DB.Wall"/> -> Wall
        var text = Regex.Replace(xml, "<see cref=\"[A-Z]:([^\"]+)\"\\s*/>", m => TypeNames.Short(m.Groups[1].Value));
        text = Regex.Replace(text, "<(summary|remarks|returns)>", m => $"\n[{m.Groups[1].Value}] ");
        text = Regex.Replace(text, "<param name=\"([^\"]+)\">", m => $"\n[param {m.Groups[1].Value}] ");
        text = Regex.Replace(text, "<exception cref=\"[A-Z]:([^\"]+)\">", m => $"\n[throws {TypeNames.Short(m.Groups[1].Value)}] ");
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "[ \\t\\r]+", " ");
        text = Regex.Replace(text, "\\n\\s*\\n+", "\n");
        return text.Trim();
    }
}

/// <summary>
/// Finds RevitAPI.dll for a Revit version: installed Revit, the local NuGet cache, or a download of the
/// Nice3point.Revit.Api reference packages from nuget.org (cached under %LOCALAPPDATA%).
/// </summary>
public sealed class ApiCatalog
{
    public const int MinVersion = 2019;
    public const int MaxVersion = 2027;
    private const string ApiPackage = "nice3point.revit.api.revitapi";
    private const string UiPackage = "nice3point.revit.api.revitapiui";
    private const string NuGetFeed = "https://api.nuget.org/v3-flatcontainer";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly ConcurrentDictionary<int, Lazy<Task<ApiIndex?>>> indexes = new();

    public static string DownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAddinManager", "api-cache");

    public static IEnumerable<int> InstalledVersions() =>
        Enumerable.Range(MinVersion, MaxVersion - MinVersion + 1).Where(v => File.Exists(Path.Combine(RevitFolder(v), "RevitAPI.dll")));

    public Task<ApiIndex?> GetAsync(int version, bool allowDownload) =>
        indexes.GetOrAdd(version, v => new Lazy<Task<ApiIndex?>>(async () =>
        {
            var source = await FindSourceAsync(v, allowDownload);
            return source == null ? null : ApiIndex.Load(source);
        })).Value;

    private static async Task<ApiSource?> FindSourceAsync(int version, bool allowDownload)
    {
        var folder = RevitFolder(version);
        var installedApi = Path.Combine(folder, "RevitAPI.dll");
        if (File.Exists(installedApi))
        {
            return new ApiSource(version, installedApi, ExistingOrNull(Path.Combine(folder, "RevitAPIUI.dll")),
                ExistingOrNull(Path.Combine(folder, "RevitAPI.xml")), ExistingOrNull(Path.Combine(folder, "RevitAPIUI.xml")),
                $"Revit {version} installation");
        }

        foreach (var root in new[] { NuGetRoot(), DownloadFolder })
        {
            var api = FindPackageFile(root, ApiPackage, version, "RevitAPI.dll");
            if (api == null) continue;
            var ui = FindPackageFile(root, UiPackage, version, "RevitAPIUI.dll");
            return new ApiSource(version, api.Value.Path, ui?.Path, ExistingOrNull(Path.ChangeExtension(api.Value.Path, ".xml")),
                ui == null ? null : ExistingOrNull(Path.ChangeExtension(ui.Value.Path, ".xml")),
                $"Nice3point.Revit.Api {api.Value.PackageVersion} ({(root == DownloadFolder ? "downloaded" : "NuGet cache")})");
        }

        if (!allowDownload) return null;
        var apiVersion = await DownloadAsync(ApiPackage, version);
        if (apiVersion == null) return null;
        await DownloadAsync(UiPackage, version);
        return await FindSourceAsync(version, false);
    }

    private static async Task<string?> DownloadAsync(string id, int version)
    {
        var index = await Http.GetStringAsync($"{NuGetFeed}/{id}/index.json");
        var packageVersion = JsonDocument.Parse(index).RootElement.GetProperty("versions").EnumerateArray()
            .Select(x => x.GetString()!)
            .Where(x => x.StartsWith($"{version}.") && !x.Contains('-'))
            .OrderBy(x => Version.TryParse(x, out var v) ? v : new Version())
            .LastOrDefault();
        if (packageVersion == null) return null;

        var target = Path.Combine(DownloadFolder, id, packageVersion);
        await using var package = await Http.GetStreamAsync($"{NuGetFeed}/{id}/{packageVersion}/{id}.{packageVersion}.nupkg");
        using var zip = new ZipArchive(package);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("ref/") || e.FullName.StartsWith("lib/")))
        {
            if (!entry.Name.EndsWith(".dll") && !entry.Name.EndsWith(".xml")) continue;
            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!destination.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
        return packageVersion;
    }

    private static (string Path, string PackageVersion)? FindPackageFile(string root, string id, int version, string fileName)
    {
        var packageRoot = Path.Combine(root, id);
        if (!Directory.Exists(packageRoot)) return null;
        var best = Directory.GetDirectories(packageRoot, $"{version}.*")
            .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(x => x.Version != null)
            .OrderByDescending(x => x.Version)
            .Select(x => (x.Dir, File: Directory.GetFiles(x.Dir, fileName, SearchOption.AllDirectories).FirstOrDefault()))
            .FirstOrDefault(x => x.File != null);
        return best.File == null ? null : (best.File, Path.GetFileName(best.Dir));
    }

    private static string NuGetRoot() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    private static string RevitFolder(int version) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", $"Revit {version}");

    private static string? ExistingOrNull(string path) => File.Exists(path) ? path : null;
}

public static class TypeNames
{
    /// <summary>Autodesk.Revit.DB.ElementId -> ElementId, System.Collections.Generic.IList`1&lt;X&gt; -> IList&lt;X&gt;</summary>
    public static string Short(string name) =>
        Regex.Replace(name, @"(?:[A-Za-z_][\w]*\.)+(?=[A-Za-z_][\w]*)", string.Empty).Replace("`1", "").Replace("`2", "").Replace("`3", "");
}
