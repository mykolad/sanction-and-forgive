using System.Xml.Linq;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>Ukraine's State Financial Monitoring Service list of persons linked to terrorism:
/// <c>list-terror/acount-list</c>. <c>type-entry</c> 1 is an entity, 2 a person; <c>type-aka</c> N is the main
/// name; <c>quality-aka</c> 2 is a low-quality alias.</summary>
public sealed class SfmsParser : IListParser
{
    public string SourceCode => "ua-sfms";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        foreach (var record in Xml.Records(stream, "list-terror", 1, cancellationToken))
        {
            if (record.Name.LocalName == "acount-list" && Read(record) is { } subject)
            {
                yield return subject;
            }
        }
    }

    private static ParsedSubject? Read(XElement record)
    {
        var type = record.Val("type-entry") switch
        {
            "1" => SubjectType.Entity,
            "2" => SubjectType.Individual,
            _ => SubjectType.Unknown,
        };
        var id = record.Val("number-entry") ?? throw new ListFormatException("An SFMS record has no number-entry.");
        var builder = new SubjectBuilder(id, type)
        {
            Programmes = record.Val("program-entry"),
            ListedOn = Text.ParseDate(record.Val("date-entry"), "yyyyMMdd"),
            Remarks = record.Val("comments"),
            SourceUrl = "https://fiu.gov.ua/",
        };

        foreach (var aka in record.Els("aka-list"))
        {
            var name = Text.Join(" ", aka.Val("aka-name1"), aka.Val("aka-name2"), aka.Val("aka-name3"), aka.Val("aka-name4"));
            builder.AddName(name, aka.Val("quality-aka") == "2", aka.Val("type-aka") == "N");
        }

        foreach (var address in record.Els("address-list"))
        {
            // The numeric country code isn't kept: the address text names the country, resolved at index build.
            builder.AddAddress(address.Val("address"), countryCode: null);
        }

        return builder.Build();
    }
}
