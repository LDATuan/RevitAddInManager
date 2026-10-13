using System.Collections;
using System.Reflection;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitAddinManager.Bridge;

/// <summary>
/// Turns whatever a script returns into JSON without walking into Revit's huge, cyclic object graphs.
/// </summary>
public static class ResultFormatter
{
    private const int MaxItems = 200;
    private const int MaxDepth = 4;

    public static JToken ToJson(object value, int depth = 0)
    {
        switch (value)
        {
            case null:
                return JValue.CreateNull();
            case bool flag:
                return new JValue(flag);
            case string or char or Enum:
                return new JValue(value.ToString());
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                return new JValue(Convert.ToInt64(value));
            case double or float or decimal:
                return new JValue(Convert.ToDouble(value));
            case ElementId id:
                return new JValue(id.ToString());
            case Element element:
                return JObject.FromObject(ElementSnapshot.Describe(element));
            case XYZ xyz:
                return new JArray(xyz.X, xyz.Y, xyz.Z);
            case Parameter parameter:
                return new JObject
                {
                    ["name"] = parameter.Definition?.Name,
                    ["value"] = parameter.AsValueString() ?? parameter.AsString(),
                };
        }

        if (depth >= MaxDepth) return new JValue(value.ToString());

        if (value is IDictionary dictionary)
        {
            var obj = new JObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (obj.Count >= MaxItems) break;
                obj[entry.Key?.ToString() ?? "null"] = ToJson(entry.Value, depth + 1);
            }
            return obj;
        }

        if (value is IEnumerable enumerable)
        {
            var array = new JArray();
            var total = 0;
            foreach (var item in enumerable)
            {
                if (total++ < MaxItems) array.Add(ToJson(item, depth + 1));
            }
            if (total > MaxItems) array.Add($"... {total - MaxItems} more");
            return array;
        }

        var type = value.GetType();
        // Revit API objects: show their type and string form rather than every property.
        if (type.Namespace?.StartsWith("Autodesk.") == true) return new JValue($"{type.Name}: {value}");

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .ToList();
        if (properties.Count == 0) return new JValue(value.ToString());

        var result = new JObject();
        foreach (var property in properties)
        {
            try
            {
                result[property.Name] = ToJson(property.GetValue(value), depth + 1);
            }
            catch (Exception ex)
            {
                result[property.Name] = $"<{ex.GetBaseException().Message}>";
            }
        }
        return result;
    }
}
