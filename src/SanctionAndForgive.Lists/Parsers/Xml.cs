using System.Xml;
using System.Xml.Linq;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>Streaming XML: one record element at a time becomes an <see cref="XElement"/>, the rest of the file
/// is never held. Names are matched by local name, so the feeds' namespaces don't matter.</summary>
internal static class Xml
{
    /// <summary>Every element at <paramref name="depth"/> (1 = the root's children). Throws
    /// <see cref="ListFormatException"/> if the root isn't <paramref name="expectedRoot"/>.</summary>
    public static IEnumerable<XElement> Records(Stream stream, string expectedRoot, int depth, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        using var reader = XmlReader.Create(stream, settings);
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != expectedRoot)
        {
            throw new ListFormatException($"Expected root element <{expectedRoot}>, found <{reader.LocalName}>.");
        }

        while (!reader.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth)
            {
                // ReadFrom leaves the reader on the next node, so don't Read() again here.
                yield return (XElement)XNode.ReadFrom(reader);
            }
            else
            {
                reader.Read();
            }
        }
    }

    public static XElement? El(this XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName);

    public static IEnumerable<XElement> Els(this XElement element, string localName) =>
        element.Elements().Where(child => child.Name.LocalName == localName);

    /// <summary>All descendants with this local name, in document order.</summary>
    public static IEnumerable<XElement> All(this XElement element, string localName) =>
        element.Descendants().Where(child => child.Name.LocalName == localName);

    /// <summary>The cleaned text of the first child with this local name, or null.</summary>
    public static string? Val(this XElement element, string localName) => Text.Clean(element.El(localName)?.Value);

    /// <summary>The cleaned value of an attribute, or null.</summary>
    public static string? Attr(this XElement element, string name) => Text.Clean(element.Attribute(name)?.Value);
}
