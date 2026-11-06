using System.Xml.Linq;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>EU consolidated financial sanctions list, FSF XML 1.1: <c>export/sanctionEntity</c>.</summary>
public sealed class EuParser : IListParser
{
    public string SourceCode => "eu";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        foreach (var record in Xml.Records(stream, "export", 1, cancellationToken))
        {
            if (record.Name.LocalName == "sanctionEntity" && Read(record) is { } subject)
            {
                yield return subject;
            }
        }
    }

    private static ParsedSubject? Read(XElement entity)
    {
        var type = entity.El("subjectType")?.Attr("code") switch
        {
            "person" => SubjectType.Individual,
            "enterprise" => SubjectType.Entity,
            _ => SubjectType.Unknown,
        };
        var id = entity.Attr("logicalId") ?? throw new ListFormatException("An EU sanctionEntity has no logicalId.");
        var regulations = entity.Els("regulation").ToList();
        var builder = new SubjectBuilder(id, type)
        {
            Programmes = Text.Join(", ", regulations.Select(regulation => regulation.Attr("programme")).Distinct()),
            ListedOn = Text.ParseDate(entity.Attr("designationDate"), "yyyy-MM-dd"),
            Remarks = Text.Join(" ", entity.Els("remark").Select(remark => remark.Value)),
            SourceUrl = regulations.Select(regulation => regulation.Val("publicationUrl")).FirstOrDefault(url => url is not null),
        };

        foreach (var alias in entity.Els("nameAlias"))
        {
            var name = alias.Attr("wholeName")
                ?? Text.Join(" ", alias.Attr("firstName"), alias.Attr("middleName"), alias.Attr("lastName"));
            builder.AddName(name, alias.Attr("strong") == "false", isPrimary: false);
        }

        foreach (var address in entity.Els("address"))
        {
            var raw = Text.Join(", ",
                address.Attr("street"), address.Attr("poBox"), address.Attr("zipCode"), address.Attr("city"),
                address.Attr("place"), address.Attr("region"), address.Attr("countryDescription"));
            builder.AddAddress(raw, address.Attr("countryIso2Code"));
        }

        return builder.Build();
    }
}
