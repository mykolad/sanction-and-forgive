using System.Xml.Linq;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>UN Security Council consolidated list: <c>CONSOLIDATED_LIST/INDIVIDUALS/INDIVIDUAL</c> and
/// <c>CONSOLIDATED_LIST/ENTITIES/ENTITY</c>.</summary>
public sealed class UnParser : IListParser
{
    public string SourceCode => "un";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        foreach (var record in Xml.Records(stream, "CONSOLIDATED_LIST", 2, cancellationToken))
        {
            var type = record.Name.LocalName switch
            {
                "INDIVIDUAL" => SubjectType.Individual,
                "ENTITY" => SubjectType.Entity,
                _ => (SubjectType?)null,
            };
            if (type is not null && Read(record, type.Value) is { } subject)
            {
                yield return subject;
            }
        }
    }

    private static ParsedSubject? Read(XElement record, SubjectType type)
    {
        var id = record.Val("REFERENCE_NUMBER") ?? record.Val("DATAID")
            ?? throw new ListFormatException("A UN record has neither REFERENCE_NUMBER nor DATAID.");
        var builder = new SubjectBuilder(id, type)
        {
            Programmes = record.Val("UN_LIST_TYPE"),
            ListedOn = Text.ParseDate(record.Val("LISTED_ON"), "yyyy-MM-dd"),
            Remarks = record.Val("COMMENTS1"),
            SourceUrl = "https://main.un.org/securitycouncil/en/content/un-sc-consolidated-list",
        };

        builder.AddName(
            Text.Join(" ", record.Val("FIRST_NAME"), record.Val("SECOND_NAME"), record.Val("THIRD_NAME"), record.Val("FOURTH_NAME")),
            isWeak: false,
            isPrimary: true);
        builder.AddName(record.Val("NAME_ORIGINAL_SCRIPT"), isWeak: false, isPrimary: false);

        var aliasElement = type == SubjectType.Individual ? "INDIVIDUAL_ALIAS" : "ENTITY_ALIAS";
        foreach (var alias in record.Els(aliasElement))
        {
            builder.AddName(alias.Val("ALIAS_NAME"), alias.Val("QUALITY") is "Low" or "Very Low", isPrimary: false);
        }

        var addressElement = type == SubjectType.Individual ? "INDIVIDUAL_ADDRESS" : "ENTITY_ADDRESS";
        foreach (var address in record.Els(addressElement))
        {
            var raw = Text.Join(", ",
                address.Val("STREET"), address.Val("ZIP_CODE"), address.Val("CITY"), address.Val("STATE_PROVINCE"), address.Val("COUNTRY"));
            builder.AddAddress(raw, countryCode: null);
        }

        return builder.Build();
    }
}
