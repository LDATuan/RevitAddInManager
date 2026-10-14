using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace RevitAddinManager.Mcp;

/// <summary>
/// Decodes metadata signatures into full type-name strings, so a member reference in an add-in
/// can be compared with the member definition in any Revit version's RevitAPI.dll.
/// </summary>
public sealed class SignatureNameProvider : ISignatureTypeProvider<string, object?>, ICustomAttributeTypeProvider<string>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Void => "System.Void",
        _ => typeCode.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        TypeDefinitionName(reader, handle);

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        TypeReferenceName(reader, handle);

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(",", typeArguments) + ">";

    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

    // ICustomAttributeTypeProvider: only needed to decode [Obsolete(string, bool)].
    public string GetSystemType() => "System.Type";

    public bool IsSystemType(string type) => type == "System.Type";

    public string GetTypeFromSerializedName(string name) => name;

    public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

    public static string TypeDefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil) return TypeDefinitionName(reader, declaring) + "+" + name;
        var ns = reader.GetString(definition.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string TypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeReferenceName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "+" + name;
        var ns = reader.GetString(reference.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string? EntityName(MetadataReader reader, EntityHandle handle, SignatureNameProvider provider) => handle.Kind switch
    {
        HandleKind.TypeDefinition => TypeDefinitionName(reader, (TypeDefinitionHandle)handle),
        HandleKind.TypeReference => TypeReferenceName(reader, (TypeReferenceHandle)handle),
        HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(provider, null),
        _ => null,
    };
}
