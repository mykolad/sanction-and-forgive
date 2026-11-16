namespace SanctionAndForgive.Lists.Parsers;

/// <summary>New Zealand's Russia Sanctions Register (Ministry of Foreign Affairs and Trade): a workbook whose sheet
/// <c>Russia Sanctions Register</c> has a legend, then a header (<c>Type</c>, <c>Unique Identifier</c>, <c>First
/// name</c>, <c>Middle name(s)</c>, <c>Last name</c>, …, <c>Alias/Alternate Spellings</c>, <c>Address</c>, …,
/// <c>Sanction Status</c>, <c>Date of Sanction</c>, …) and a row per designation. Columns are found by their header.
/// <c>Individual</c> rows are people; <c>Entity</c> and <c>Bank</c> rows are entities, named in the <c>First name</c>
/// column; <c>Asset</c> rows (categories of ships or aircraft) and the closing <c>Total</c> row are left out, as is a
/// row whose status says it was revoked, delisted or removed. Aliases are separated by semicolons; dates are Excel
/// serial numbers. The other sheets (ships, trade measures) aren't read.</summary>
public sealed class NewZealandParser : IListParser
{
    public const string Sheet = "Russia Sanctions Register";

    public const string Programme = "Russia Sanctions Act 2022";

    private static readonly string[] Gone = ["revoked", "delisted", "removed"];

    public string SourceCode => "nz-mfat";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        var rows = Spreadsheets.XlsxRows(stream, Sheet, cancellationToken);
        var headerAt = rows.FindIndex(row => row.Count > 1 && Is(row[0], "Type") && Is(row[1], "Unique Identifier"));
        if (headerAt < 0)
        {
            throw new ListFormatException("No header row (Type, Unique Identifier, …) in New Zealand's register.");
        }

        var header = rows[headerAt].ToList();
        int Column(string name) => header.FindIndex(cell => Is(cell, name)) is var index and >= 0
            ? index
            : throw new ListFormatException($"New Zealand's register has no \"{name}\" column.");
        int? Optional(string name) => header.FindIndex(cell => Is(cell, name)) is var index and >= 0 ? index : null;
        int kind = Column("Type"), identifier = Column("Unique Identifier"), first = Column("First name"), last = Column("Last name"),
            alias = Column("Alias/Alternate Spellings"), address = Column("Address"), status = Column("Sanction Status"), sanctioned = Column("Date of Sanction");
        int? middle = Optional("Middle name(s)"), title = Optional("Title"), rationale = Optional("General Rationale for Sanction");

        var subjects = new List<ParsedSubject>();
        foreach (var row in rows.Skip(headerAt + 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? Cell(int? column) => column is { } c && c < row.Count ? Text.Clean(row[c]) : null;
            SubjectType? type = Cell(kind)?.ToUpperInvariant() switch
            {
                "INDIVIDUAL" => SubjectType.Individual,
                "ENTITY" or "BANK" => SubjectType.Entity,
                "ASSET" or "TOTAL" or "SHIP" or "AIRCRAFT" => null,
                null => Cell(identifier) is null ? null : SubjectType.Unknown,
                _ => SubjectType.Unknown,
            };
            if (type is null || Cell(status) is { } state && Gone.Any(word => state.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var id = Cell(identifier) ?? throw new ListFormatException($"A {Cell(kind)} row in New Zealand's register has no Unique Identifier.");
            var builder = new SubjectBuilder(id, type.Value)
            {
                Programmes = Programme,
                ListedOn = Spreadsheets.ExcelDate(Cell(sanctioned)) ?? Text.ParseDate(Cell(sanctioned), "d/M/yyyy", "dd/MM/yyyy"),
                Remarks = Text.Join("; ", Cell(title), Cell(rationale)),
            };
            builder.AddName(type == SubjectType.Individual ? Text.Join(" ", Cell(first), Cell(middle), Cell(last)) : Cell(first) ?? Cell(last), isWeak: false, isPrimary: true);
            foreach (var name in (Cell(alias) ?? string.Empty).Split(';'))
            {
                builder.AddName(name, isWeak: false, isPrimary: false);
            }

            builder.AddAddress(Cell(address), countryCode: null);
            if (builder.Build() is { } subject)
            {
                subjects.Add(subject);
            }
        }

        return subjects;
    }

    private static bool Is(string cell, string name) => string.Equals(cell.Trim(), name, StringComparison.OrdinalIgnoreCase);
}
