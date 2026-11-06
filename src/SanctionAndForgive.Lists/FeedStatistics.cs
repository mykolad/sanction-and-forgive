using System.Xml;

namespace SanctionAndForgive.Lists;

/// <summary>What a parsed feed holds, for the probe and for sizing the index and the database.</summary>
public sealed record FeedStatistics(
    int Individuals,
    int Entities,
    int Unknown,
    int Names,
    int WeakNames,
    int CyrillicNames,
    int OtherScriptNames,
    int Addresses,
    long NameCharacters,
    long AddressCharacters,
    long OtherCharacters)
{
    public int Subjects => Individuals + Entities + Unknown;

    public static FeedStatistics Of(IEnumerable<ParsedSubject> subjects)
    {
        int individuals = 0, entities = 0, unknown = 0, names = 0, weak = 0, cyrillic = 0, other = 0, addresses = 0;
        long nameCharacters = 0, addressCharacters = 0, otherCharacters = 0;
        foreach (var subject in subjects)
        {
            switch (subject.Type)
            {
                case SubjectType.Individual: individuals++; break;
                case SubjectType.Entity: entities++; break;
                default: unknown++; break;
            }

            foreach (var name in subject.Names)
            {
                names++;
                nameCharacters += name.FullName.Length;
                weak += name.IsWeak ? 1 : 0;
                cyrillic += name.Script == NameScript.Cyrillic ? 1 : 0;
                other += name.Script == NameScript.Other ? 1 : 0;
            }

            addresses += subject.Addresses.Count;
            addressCharacters += subject.Addresses.Sum(address => (long)address.Raw.Length);
            otherCharacters += subject.ExternalId.Length + subject.PrimaryName.Length + (subject.Programmes?.Length ?? 0)
                + (subject.Remarks?.Length ?? 0) + (subject.SourceUrl?.Length ?? 0);
        }

        return new FeedStatistics(individuals, entities, unknown, names, weak, cyrillic, other, addresses, nameCharacters, addressCharacters, otherCharacters);
    }

    /// <summary>The root element's name of an XML file, <c>{json}</c> for JSON, or null if it's neither.</summary>
    public static string? RootElement(string path)
    {
        using var stream = File.OpenRead(path);
        var first = stream.ReadByte();
        while (first is ' ' or '\r' or '\n' or '\t' or 0xEF or 0xBB or 0xBF)
        {
            first = stream.ReadByte();
        }

        if (first is '{' or '[')
        {
            return "{json}";
        }

        stream.Position = 0;
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return reader.MoveToContent() == XmlNodeType.Element ? reader.LocalName : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
