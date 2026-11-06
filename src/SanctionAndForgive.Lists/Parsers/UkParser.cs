using System.Xml.Linq;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>UK Sanctions List (FCDO) XML: <c>Designations/Designation</c>. Not the closed OFSI consolidated list.</summary>
public sealed class UkParser : IListParser
{
    public string SourceCode => "uk";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        foreach (var record in Xml.Records(stream, "Designations", 1, cancellationToken))
        {
            if (record.Name.LocalName == "Designation" && Read(record) is { } subject)
            {
                yield return subject;
            }
        }
    }

    private static ParsedSubject? Read(XElement designation)
    {
        var type = designation.Val("IndividualEntityShip") switch
        {
            "Individual" => SubjectType.Individual,
            "Entity" => SubjectType.Entity,
            "Ship" => (SubjectType?)null,
            _ => SubjectType.Unknown,
        };
        if (type is null)
        {
            return null;
        }

        var id = designation.Val("UniqueID") ?? throw new ListFormatException("A UK Designation has no UniqueID.");
        var builder = new SubjectBuilder(id, type.Value)
        {
            Programmes = designation.Val("RegimeName"),
            ListedOn = Text.ParseDate(designation.Val("DateDesignated"), "dd/MM/yyyy"),
            Remarks = designation.Val("OtherInformation"),
            SourceUrl = "https://www.gov.uk/government/publications/the-uk-sanctions-list",
        };

        foreach (var name in designation.El("Names")?.Els("Name") ?? [])
        {
            var fullName = Text.Join(" ", name.Val("Name1"), name.Val("Name2"), name.Val("Name3"), name.Val("Name4"), name.Val("Name5"), name.Val("Name6"));
            var isWeak = name.Val("AliasStrength")?.StartsWith("Low", StringComparison.OrdinalIgnoreCase) == true;
            builder.AddName(fullName, isWeak, name.Val("NameType") == "Primary Name");
        }

        foreach (var name in designation.El("NonLatinNames")?.Els("NonLatinName") ?? [])
        {
            builder.AddName(name.Val("NameNonLatinScript"), isWeak: false, isPrimary: false);
        }

        foreach (var address in designation.El("Addresses")?.Els("Address") ?? [])
        {
            var raw = Text.Join(", ",
                address.Val("AddressLine1"), address.Val("AddressLine2"), address.Val("AddressLine3"), address.Val("AddressLine4"),
                address.Val("AddressLine5"), address.Val("AddressLine6"), address.Val("AddressPostalCode"), address.Val("AddressCountry"));
            builder.AddAddress(raw, countryCode: null);
        }

        return builder.Build();
    }
}
