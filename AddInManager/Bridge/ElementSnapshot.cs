using Autodesk.Revit.DB;

namespace RevitAddinManager.Bridge;

/// <summary>
/// Parameter values of one element at a point in time, used to diff before/after a run.
/// </summary>
public sealed class ElementSnapshot
{
    private ElementSnapshot(Element element)
    {
        Id = element.Id.ToString();
        Name = SafeName(element);
        Category = element.Category?.Name;
        foreach (Parameter parameter in element.Parameters)
        {
            var name = parameter.Definition?.Name;
            if (name == null) continue;
            // Parameter ids are unique per element; names are not.
            Values[parameter.Id.ToString()] = new KeyValuePair<string, string>(name, ReadValue(parameter));
        }
    }

    public string Id { get; }
    public string Name { get; }
    public string Category { get; }
    public Dictionary<string, KeyValuePair<string, string>> Values { get; } = new Dictionary<string, KeyValuePair<string, string>>();

    public static Dictionary<string, ElementSnapshot> Take(Document doc, IEnumerable<ElementId> ids)
    {
        var result = new Dictionary<string, ElementSnapshot>();
        if (doc == null || !doc.IsValidObject) return result;
        foreach (var id in ids)
        {
            var element = doc.GetElement(id);
            if (element == null || !element.IsValidObject) continue;
            try
            {
                result[id.ToString()] = new ElementSnapshot(element);
            }
            catch (Exception)
            {
                // Some internal elements throw on parameter access; they are not interesting to diff.
            }
        }
        return result;
    }

    public static object Describe(Element element) => new
    {
        id = element.Id.ToString(),
        name = SafeName(element),
        category = element.Category?.Name,
        className = element.GetType().Name,
    };

    /// <summary>
    /// Parameters whose value differs between two snapshots of the same element.
    /// </summary>
    public static List<object> Diff(ElementSnapshot before, ElementSnapshot after)
    {
        var changes = new List<object>();
        foreach (var pair in after.Values)
        {
            before.Values.TryGetValue(pair.Key, out var old);
            if (old.Value == pair.Value.Value) continue;
            changes.Add(new { name = pair.Value.Key, before = old.Value, after = pair.Value.Value });
        }
        foreach (var pair in before.Values.Where(x => !after.Values.ContainsKey(x.Key)))
        {
            changes.Add(new { name = pair.Value.Key, before = pair.Value.Value, after = (string)null });
        }
        return changes;
    }

    private static string ReadValue(Parameter parameter)
    {
        if (!parameter.HasValue) return null;
        var text = parameter.AsValueString();
        if (text != null) return text;
        switch (parameter.StorageType)
        {
            case StorageType.String:
                return parameter.AsString();
            case StorageType.Integer:
                return parameter.AsInteger().ToString();
            case StorageType.Double:
                return parameter.AsDouble().ToString("R");
            case StorageType.ElementId:
                return parameter.AsElementId().ToString();
            default:
                return null;
        }
    }

    private static string SafeName(Element element)
    {
        try
        {
            return element.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
