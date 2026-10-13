using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace RevitAddinManager.Mcp;

public sealed record ApiUsage(string Kind, string TypeName, string? MemberName, IReadOnlyList<string> Parameters, string? ReturnType)
{
    public string Display => Kind switch
    {
        "type" => TypeNames.Short(TypeName),
        "field" => $"{TypeNames.Short(TypeName)}.{MemberName}",
        _ when MemberName == ".ctor" => $"new {TypeNames.Short(TypeName)}({string.Join(", ", Parameters.Select(TypeNames.Short))})",
        _ when MemberName!.StartsWith("get_") && Parameters.Count == 0 => $"{TypeNames.Short(TypeName)}.{MemberName[4..]} (get)",
        _ when MemberName!.StartsWith("set_") && Parameters.Count == 1 => $"{TypeNames.Short(TypeName)}.{MemberName[4..]} (set)",
        _ => $"{TypeNames.Short(TypeName)}.{MemberName}({string.Join(", ", Parameters.Select(TypeNames.Short))})",
    };
}

/// <summary>
/// Finds every Revit API type and member an add-in .dll uses (and where, via its PDB), then checks each one against
/// the RevitAPI surface of other Revit versions: removed, signature changed, or obsolete.
/// </summary>
public sealed class CompatChecker
{
    private const int MaxLocations = 5;
    private static readonly HashSet<string> RevitAssemblies = new(StringComparer.OrdinalIgnoreCase) { "RevitAPI", "RevitAPIUI" };
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields()
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    public sealed class ScanResult
    {
        public required string AssemblyPath { get; init; }
        public int? BuiltAgainst { get; init; }
        public required string Pdb { get; init; }
        public required Dictionary<ApiUsage, List<string>> Usages { get; init; }
    }

    public static ScanResult Scan(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var provider = new SignatureNameProvider();

        int? builtAgainst = null;
        var revitRefs = new HashSet<AssemblyReferenceHandle>();
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            if (!RevitAssemblies.Contains(reader.GetString(reference.Name))) continue;
            revitRefs.Add(handle);
            // RevitAPI 25.0.x.x -> Revit 2025
            if (reference.Version.Major is > 10 and < 100) builtAgainst = 2000 + reference.Version.Major;
        }

        var usagesByToken = new Dictionary<EntityHandle, ApiUsage>();
        foreach (var handle in reader.TypeReferences)
        {
            if (IsRevit(reader, handle, revitRefs))
                usagesByToken[handle] = new ApiUsage("type", SignatureNameProvider.TypeReferenceName(reader, handle), null, Array.Empty<string>(), null);
        }

        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference || !IsRevit(reader, (TypeReferenceHandle)member.Parent, revitRefs)) continue;
            var typeName = SignatureNameProvider.TypeReferenceName(reader, (TypeReferenceHandle)member.Parent);
            var name = reader.GetString(member.Name);
            if (member.GetKind() == MemberReferenceKind.Method)
            {
                var signature = member.DecodeMethodSignature(provider, null);
                usagesByToken[handle] = new ApiUsage("method", typeName, name, signature.ParameterTypes, signature.ReturnType);
            }
            else
            {
                usagesByToken[handle] = new ApiUsage("field", typeName, name, Array.Empty<string>(), member.DecodeFieldSignature(provider, null));
            }
        }

        var usages = usagesByToken.Values.Distinct().ToDictionary(u => u, _ => new List<string>());
        using var pdb = OpenPdb(pe, assemblyPath, out var pdbKind);
        var pdbReader = pdb?.GetMetadataReader();

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue;
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            foreach (var (token, offset) in ReadTokens(body.GetILReader()))
            {
                var handle = ResolveToken(reader, token);
                if (handle == null || !usagesByToken.TryGetValue(handle.Value, out var usage)) continue;
                var locations = usages[usage];
                if (locations.Count >= MaxLocations) continue;
                var location = Describe(reader, pdbReader, methodHandle, offset);
                if (!locations.Contains(location)) locations.Add(location);
            }
        }

        // Enum values compile to constants, so a type like DisplayUnitType has no IL reference of its own:
        // borrow the locations of the members whose signature mentions it.
        foreach (var (usage, locations) in usages.Where(x => x.Key.Kind == "type" && x.Value.Count == 0))
        {
            locations.AddRange(usages
                .Where(x => x.Key.Kind != "type" && x.Key.Parameters.Append(x.Key.ReturnType ?? "").Any(t => t.Contains(usage.TypeName)))
                .SelectMany(x => x.Value)
                .Distinct()
                .Take(MaxLocations));
        }

        return new ScanResult { AssemblyPath = assemblyPath, BuiltAgainst = builtAgainst, Pdb = pdbKind, Usages = usages };
    }

    public static object Check(ScanResult scan, ApiIndex index)
    {
        var errors = new List<object>();
        var warnings = new List<object>();
        var missingTypes = new HashSet<string>();

        foreach (var (usage, locations) in scan.Usages.OrderBy(x => x.Key.TypeName).ThenBy(x => x.Key.MemberName))
        {
            if (!index.Types.TryGetValue(usage.TypeName, out var type))
            {
                if (missingTypes.Add(usage.TypeName))
                    errors.Add(new { api = TypeNames.Short(usage.TypeName), problem = "type removed", fullName = usage.TypeName, usedAt = locations });
                continue;
            }

            if (usage.Kind == "type")
            {
                if (type.Obsolete != null)
                    warnings.Add(new { api = usage.Display, problem = type.ObsoleteIsError ? "obsolete (error)" : "obsolete", message = type.Obsolete, usedAt = locations });
                continue;
            }

            var candidates = index.FindMembers(usage.TypeName, usage.MemberName!)
                .Where(m => usage.Kind == "field" ? m.Kind == "field" : m.Kind is "method" or "ctor")
                .ToList();
            var key = $"{usage.MemberName}({string.Join(",", usage.Parameters)}):{usage.ReturnType}";
            var match = candidates.FirstOrDefault(m => m.Key == key);

            if (candidates.Count == 0)
            {
                errors.Add(new { api = usage.Display, problem = "member removed", usedAt = locations });
            }
            else if (match == null)
            {
                errors.Add(new
                {
                    api = usage.Display,
                    problem = "signature changed",
                    available = candidates.Select(c => c.Display).Distinct().Take(8),
                    usedAt = locations,
                });
            }
            else if (match.Obsolete != null)
            {
                var item = new { api = usage.Display, problem = match.ObsoleteIsError ? "obsolete (error)" : "obsolete", message = match.Obsolete, usedAt = locations };
                if (match.ObsoleteIsError) errors.Add(item);
                else warnings.Add(item);
            }
        }

        return new
        {
            version = index.Source.Version,
            source = index.Source.Origin,
            status = errors.Count == 0 ? (warnings.Count == 0 ? "compatible" : "compatible with warnings") : "breaks",
            errors,
            warnings,
        };
    }

    private static bool IsRevit(MetadataReader reader, TypeReferenceHandle handle, HashSet<AssemblyReferenceHandle> revitRefs)
    {
        var scope = reader.GetTypeReference(handle).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference) scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        return scope.Kind == HandleKind.AssemblyReference && revitRefs.Contains((AssemblyReferenceHandle)scope);
    }

    private static EntityHandle? ResolveToken(MetadataReader reader, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
            case HandleKind.TypeReference:
                return handle;
            case HandleKind.MethodSpecification:
                // Generic method instantiation: look at the method it instantiates.
                var method = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
                return method.Kind == HandleKind.MemberReference ? method : null;
            default:
                return null;
        }
    }

    /// <summary>Yields metadata tokens referenced by the IL together with their IL offset.</summary>
    private static IEnumerable<(int Token, int Offset)> ReadTokens(BlobReader il)
    {
        var results = new List<(int, int)>();
        while (il.RemainingBytes > 0)
        {
            var offset = il.Offset;
            short value = il.ReadByte();
            if (value == 0xFE) value = (short)(0xFE00 | il.ReadByte());
            if (!OpCodesByValue.TryGetValue(value, out var opCode)) break;

            switch (opCode.OperandType)
            {
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    results.Add((il.ReadInt32(), offset));
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    il.Offset += 1;
                    break;
                case OperandType.InlineVar:
                    il.Offset += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    il.Offset += 8;
                    break;
                case OperandType.InlineSwitch:
                    var count = il.ReadInt32();
                    il.Offset += count * 4;
                    break;
                default:
                    il.Offset += 4;
                    break;
            }
        }
        return results;
    }

    private static string Describe(MetadataReader reader, MetadataReader? pdb, MethodDefinitionHandle handle, int offset)
    {
        var method = reader.GetMethodDefinition(handle);
        var name = $"{TypeNames.Short(SignatureNameProvider.TypeDefinitionName(reader, method.GetDeclaringType()))}.{reader.GetString(method.Name)}";
        if (pdb == null) return name;

        var points = pdb.GetMethodDebugInformation(handle.ToDebugInformationHandle()).GetSequencePoints()
            .Where(p => !p.IsHidden && p.Offset <= offset)
            .ToList();
        if (points.Count == 0) return name;
        var point = points.MaxBy(p => p.Offset);
        var document = pdb.GetString(pdb.GetDocument(point.Document).Name);
        return $"{name} ({Path.GetFileName(document)}:{point.StartLine})";
    }

    private static MetadataReaderProvider? OpenPdb(PEReader pe, string assemblyPath, out string kind)
    {
        var embedded = pe.ReadDebugDirectory().FirstOrDefault(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        if (embedded.DataSize > 0)
        {
            kind = "embedded portable";
            return pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded);
        }

        var path = Path.ChangeExtension(assemblyPath, ".pdb");
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            // Portable PDBs are metadata blobs starting with "BSJB"; Windows PDBs cannot be read here.
            if (bytes.Length > 4 && bytes[0] == 'B' && bytes[1] == 'S' && bytes[2] == 'J' && bytes[3] == 'B')
            {
                kind = "portable";
                return MetadataReaderProvider.FromPortablePdbImage(System.Collections.Immutable.ImmutableArray.Create(bytes));
            }
            kind = "windows pdb (no line numbers; set <DebugType>portable</DebugType>)";
            return null;
        }

        kind = "none";
        return null;
    }
}
