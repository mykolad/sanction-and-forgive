using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>Canada's consolidated autonomous sanctions list (SEMA): <c>data-set/record</c>, bilingual element
/// names. Ships (records with an IMO number) are left out.</summary>
public sealed partial class CanadaSemaParser : IListParser
{
    public string SourceCode => "ca-sema";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        foreach (var record in Xml.Records(stream, "data-set", 1, cancellationToken))
        {
            if (record.Name.LocalName == "record" && Read(record) is { } subject)
            {
                yield return subject;
            }
        }
    }

    private static ParsedSubject? Read(XElement record)
    {
        if (record.Val("ShipIMONumber-NumeroOMIDuNavire") is not null)
        {
            return null;
        }

        var entityName = record.Val("EntityOrShip-EntiteOuNavire");
        var personName = Text.Join(" ", record.Val("GivenName-Prenom"), record.Val("LastName-NomDeFamille"));
        var type = entityName is not null ? SubjectType.Entity : SubjectType.Individual;
        var country = record.Val("Country-Pays");

        // The list has no id of its own: its country, schedule and item number identify a record.
        var id = Text.Join("/", country, record.Val("Schedule-Annexe"), record.Val("Item-NumeroDarticle"))
            ?? throw new ListFormatException("A Canada SEMA record has no country, schedule or item.");
        var builder = new SubjectBuilder(id, type)
        {
            Programmes = country,
            ListedOn = Text.ParseDate(record.Val("DateOfListing-DateDinscription"), "yyyy-MM-dd"),
            SourceUrl = "https://www.international.gc.ca/world-monde/international_relations-relations_internationales/sanctions/consolidated-consolide.aspx?lang=eng",
        };

        builder.AddName(entityName ?? personName, isWeak: false, isPrimary: true);

        // "A, B, Belarusian: В, Russian: Г": comma-separated, some with a language label.
        foreach (var alias in record.Val("Aliases-Alias")?.Split(',') ?? [])
        {
            builder.AddName(LanguageLabel().Replace(alias, string.Empty), isWeak: false, isPrimary: false);
        }

        return builder.Build();
    }

    [GeneratedRegex(@"^\s*\p{L}+:\s*")]
    private static partial Regex LanguageLabel();
}
