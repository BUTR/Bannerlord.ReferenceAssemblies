using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace Bannerlord.ReferenceAssemblies;

internal sealed record PrefabIndex(int FormatVersion, List<PrefabEntry> Prefabs);

/// <summary>One prefab of prefabs.json. File is the tree's path relative to gui/.</summary>
internal sealed record PrefabEntry(string Name, string Module, string File, string? RootTag, List<string> Tags, List<PrefabParameter> Parameters);

internal sealed record PrefabParameter(string? Name, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultValue);

/// <summary>
/// A prefab as JSON: the document WidgetPrefab.LoadFrom loads, which is the one UIExtenderEx patches, so an
/// XPath gives the same answer on the tree rebuilt as XML. A node is { n, a, c }: the element name, its
/// attributes and its children, each in document order, a and c left out when empty. A child is a node, or a
/// string for text or CDATA that is not only whitespace. Comments are gone already, as the game drops them;
/// processing instructions and the declaration are left out.
/// </summary>
internal static class PrefabTrees
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The document exactly as WidgetPrefab.LoadFrom loads it: comments dropped, whitespace-only text too.</summary>
    public static XmlDocument Load(string path)
    {
        var document = new XmlDocument();
        // The game passes the StreamReader in and never closes it; the file is read the same way here, and closed.
        using var text = new StreamReader(path);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { IgnoreComments = true });
        document.Load(reader);
        return document;
    }

    /// <summary>The tree file of a prefab, compact: it is data for a program.</summary>
    public static byte[] Write(XmlDocument document, string name, string module)
    {
        var root = document.DocumentElement ?? throw new InvalidDataException("The prefab has no root element.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", GuiPackager.FormatVersion);
            writer.WriteString("name", name);
            writer.WriteString("module", module);
            writer.WritePropertyName("root");
            WriteNode(writer, root);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteNode(Utf8JsonWriter writer, XmlElement element)
    {
        // None of the game's prefabs use namespaces; a mapping invented for one would be a guess.
        if (element.Prefix.Length > 0 || element.NamespaceURI.Length > 0)
            throw new InvalidDataException($"Element {element.Name} is in a namespace, which the prefab trees do not carry.");

        writer.WriteStartObject();
        writer.WriteString("n", element.Name);
        if (element.Attributes.Count > 0)
        {
            writer.WriteStartObject("a");
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (attribute.Prefix.Length > 0 || attribute.NamespaceURI.Length > 0 || attribute.Name == "xmlns")
                    throw new InvalidDataException($"Attribute {attribute.Name} of {element.Name} is in a namespace, which the prefab trees do not carry.");
                writer.WriteString(attribute.Name, attribute.Value);
            }
            writer.WriteEndObject();
        }

        var children = element.ChildNodes.Cast<XmlNode>().Where(x => x is XmlElement || IsText(x) && !IsWhitespace(x.Value!)).ToList();
        if (children.Count > 0)
        {
            writer.WriteStartArray("c");
            foreach (var child in children)
            {
                if (child is XmlElement childElement)
                    WriteNode(writer, childElement);
                else
                    writer.WriteStringValue(child.Value);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// The JSON depth a reader of the trees needs: each element is an object and its children an array, so
    /// the game's deepest prefabs go past the 64 levels System.Text.Json reads by default.
    /// </summary>
    public const int MaxDepth = 512;

    /// <summary>The prefab a tree file stands for, rebuilt as an XmlDocument.</summary>
    public static XmlDocument Rebuild(byte[] tree)
    {
        using var json = JsonDocument.Parse(tree, new JsonDocumentOptions { MaxDepth = MaxDepth });
        var document = new XmlDocument();
        document.AppendChild(RebuildNode(document, json.RootElement.GetProperty("root")));
        return document;
    }

    private static XmlElement RebuildNode(XmlDocument document, JsonElement node)
    {
        var element = document.CreateElement(node.GetProperty("n").GetString()!);
        if (node.TryGetProperty("a", out var attributes))
            foreach (var attribute in attributes.EnumerateObject())
                element.SetAttribute(attribute.Name, attribute.Value.GetString());
        if (node.TryGetProperty("c", out var children))
            foreach (var child in children.EnumerateArray())
                element.AppendChild(child.ValueKind == JsonValueKind.String ? document.CreateTextNode(child.GetString()) : RebuildNode(document, child));
        return element;
    }

    /// <summary>
    /// Where a rebuilt prefab differs from the one the game loads, or null when it does not: element names,
    /// attributes in order, and children in order, text and CDATA compared by their text. Processing
    /// instructions are left out on purpose; anything else the tree lost, such as whitespace kept by
    /// xml:space, is a difference.
    /// </summary>
    public static string? Difference(XmlDocument loaded, XmlDocument rebuilt) =>
        Difference(loaded.DocumentElement!, rebuilt.DocumentElement!, "");

    private static string? Difference(XmlElement expected, XmlElement actual, string path)
    {
        path = $"{path}/{expected.Name}";
        if (expected.Name != actual.Name)
            return $"{path}: rebuilt as {actual.Name}";

        var expectedAttributes = expected.Attributes.Cast<XmlAttribute>().Select(x => (x.Name, x.Value)).ToList();
        var actualAttributes = actual.Attributes.Cast<XmlAttribute>().Select(x => (x.Name, x.Value)).ToList();
        if (!expectedAttributes.SequenceEqual(actualAttributes))
            return $"{path}: attributes {string.Join(" ", expectedAttributes.Select(x => $"{x.Name}=\"{x.Value}\""))} rebuilt as {string.Join(" ", actualAttributes.Select(x => $"{x.Name}=\"{x.Value}\""))}";

        var expectedChildren = Children(expected);
        var actualChildren = Children(actual);
        if (expectedChildren.Count != actualChildren.Count)
            return $"{path}: {expectedChildren.Count} child node(s) rebuilt as {actualChildren.Count}";
        for (var i = 0; i < expectedChildren.Count; i++)
        {
            var (expectedChild, actualChild) = (expectedChildren[i], actualChildren[i]);
            if (expectedChild is XmlElement expectedElement && actualChild is XmlElement actualElement)
            {
                if (Difference(expectedElement, actualElement, path) is { } difference)
                    return difference;
            }
            else if (expectedChild is string expectedText && actualChild is string actualText)
            {
                if (expectedText != actualText)
                    return $"{path}: text \"{expectedText}\" rebuilt as \"{actualText}\"";
            }
            else
            {
                return $"{path}: child {i + 1} is {Describe(expectedChild)}, rebuilt as {Describe(actualChild)}";
            }
        }
        return null;

        static string Describe(object child) => child switch
        {
            XmlElement element => $"element {element.Name}",
            string => "text",
            XmlNode node => node.NodeType.ToString(),
            _ => "?",
        };
    }

    /// <summary>The children to compare: adjacent text and CDATA merged into one string, processing instructions left out.</summary>
    private static List<object> Children(XmlElement element)
    {
        var result = new List<object>();
        foreach (XmlNode child in element.ChildNodes)
        {
            if (child is XmlProcessingInstruction)
                continue;
            if (IsText(child))
            {
                if (result.Count > 0 && result[^1] is string previous)
                    result[^1] = previous + child.Value;
                else
                    result.Add(child.Value!);
                continue;
            }
            result.Add(child);
        }
        return result;
    }

    /// <summary>The prefab's entry in prefabs.json, read as WidgetPrefab.LoadFrom reads the root widget and the parameters.</summary>
    public static PrefabEntry Entry(XmlDocument document, string name, string module, string file)
    {
        var prefab = document.SelectSingleNode("Prefab");
        var window = prefab is not null ? prefab.SelectSingleNode("Window") : document.SelectSingleNode("Window");
        var parameters = prefab?.SelectSingleNode("Parameters")?.ChildNodes.OfType<XmlElement>()
            .Select(x => new PrefabParameter(x.GetAttributeNode("Name")?.Value, x.GetAttributeNode("DefaultValue")?.Value))
            .ToList() ?? [];
        var tags = document.GetElementsByTagName("*").Cast<XmlElement>().Select(x => x.Name).Distinct().Order(StringComparer.Ordinal).ToList();
        return new PrefabEntry(name, module, file, (window?.FirstChild as XmlElement)?.Name, tags, parameters);
    }

    private static bool IsText(XmlNode node) => node is XmlText or XmlCDataSection;

    /// <summary>Whitespace as XML has it: a no-break space is text.</summary>
    private static bool IsWhitespace(string text) => text.All(x => x is ' ' or '\t' or '\r' or '\n');
}
