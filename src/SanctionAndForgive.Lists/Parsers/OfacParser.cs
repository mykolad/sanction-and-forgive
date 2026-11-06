using System.Xml.Linq;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>OFAC enhanced XML (<c>sanctionsData/entities/entity</c>), for both the SDN list and the consolidated
/// non-SDN list: the two files share one format.</summary>
public sealed class OfacParser(string sourceCode) : IListParser
{
    public string SourceCode => sourceCode;

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        foreach (var record in Xml.Records(stream, "sanctionsData", 2, cancellationToken))
        {
            if (record.Name.LocalName == "entity" && Read(record) is { } subject)
            {
                yield return subject;
            }
        }
    }

    private static ParsedSubject? Read(XElement entity)
    {
        var type = entity.El("generalInfo")?.Val("entityType") switch
        {
            "Individual" => SubjectType.Individual,
            "Entity" => SubjectType.Entity,
            // Vessels and aircraft aren't screened in the MVP.
            "Vessel" or "Aircraft" => (SubjectType?)null,
            _ => SubjectType.Unknown,
        };
        if (type is null)
        {
            return null;
        }

        var id = entity.Attr("id") ?? throw new ListFormatException("An OFAC entity has no id.");
        var builder = new SubjectBuilder(id, type.Value)
        {
            Programmes = Text.Join(", ", entity.El("sanctionsPrograms")?.Els("sanctionsProgram").Select(program => program.Value).Distinct() ?? []),
            ListedOn = entity.El("sanctionsLists")?.Els("sanctionsList")
                .Select(list => Text.ParseDate(list.Attr("datePublished"), "yyyy-MM-dd"))
                .Where(date => date is not null)
                .Min(),
            SourceUrl = $"https://sanctionssearch.ofac.treas.gov/Details.aspx?id={id}",
        };

        foreach (var name in entity.El("names")?.Els("name") ?? [])
        {
            var isPrimary = name.Val("isPrimary") == "true";
            var isWeak = name.Val("isLowQuality") == "true";
            foreach (var translation in name.El("translations")?.Els("translation") ?? [])
            {
                var fullName = Text.Join(" ", translation.Val("formattedFirstName"), translation.Val("formattedLastName"))
                    ?? translation.Val("formattedFullName");
                builder.AddName(fullName, isWeak, isPrimary && translation.Val("isPrimary") == "true");
            }
        }

        foreach (var address in entity.El("addresses")?.Els("address") ?? [])
        {
            var country = address.Val("country");
            var translations = address.El("translations")?.Els("translation").ToList() ?? [];
            if (translations.Count == 0)
            {
                builder.AddAddress(country, countryCode: null);
            }

            foreach (var translation in translations)
            {
                var parts = translation.El("addressParts")?.Els("addressPart").Select(part => part.Val("value")) ?? [];
                builder.AddAddress(Text.Join(", ", [.. parts, country]), countryCode: null);
            }
        }

        return builder.Build();
    }
}
