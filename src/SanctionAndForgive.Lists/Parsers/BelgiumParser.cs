namespace SanctionAndForgive.Lists.Parsers;

/// <summary>Belgium's national list of persons and entities whose funds and economic resources are frozen in the fight
/// against the financing of terrorism (royal decree of 28 December 2006; FPS Finance, Treasury): a workbook whose first
/// sheet has a title and notes, a header (<c>Naam</c>, <c>Voornaam(voornamen)</c>, <c>Rijkregisternummer</c>,
/// <c>Geboortedatum</c>, <c>Datum KB</c>, …, <c>Link naar de publicatie</c>), then a row per number, in the first column.
/// A number whose row has no name is a place kept for someone no longer listed, and is left out. Every party listed so
/// far is a person; dates are Excel serial numbers. The national register number isn't kept; the date of birth goes in
/// the remarks, and the link to the royal decree in the Belgian Official Journal is the official record.</summary>
public sealed class BelgiumParser : IListParser
{
    public const string Programme = "Belgian national list (royal decree of 28 December 2006, financing of terrorism)";

    public string SourceCode => "be-terrorism";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        var rows = Spreadsheets.XlsxRows(stream, sheetName: string.Empty, cancellationToken);
        var headerAt = rows.FindIndex(row => row.Any(cell => Is(cell, "Naam")) && row.Any(cell => Is(cell, "Voornaam(voornamen)")));
        if (headerAt < 0)
        {
            throw new ListFormatException("No header row (Naam, Voornaam(voornamen), …) in Belgium's national list.");
        }

        var header = rows[headerAt].ToList();
        int? Optional(string name) => header.FindIndex(cell => Is(cell, name)) is var index and >= 0 ? index : null;
        int surname = Optional("Naam")!.Value, given = Optional("Voornaam(voornamen)")!.Value;
        int? born = Optional("Geboortedatum"), decree = Optional("Datum KB"), publication = Optional("Link naar de publicatie");

        var subjects = new List<ParsedSubject>();
        foreach (var row in rows.Skip(headerAt + 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? Cell(int? column) => column is { } c && c < row.Count ? Text.Clean(row[c]) : null;
            string? last = Cell(surname), first = Cell(given);
            if (last is null && first is null)
            {
                continue;
            }

            var number = Cell(0) ?? throw new ListFormatException($"A row of Belgium's national list ({last ?? first}) has no number.");
            if (last is null)
            {
                throw new ListFormatException($"Row {number} of Belgium's national list has no surname.");
            }

            var birth = Spreadsheets.ExcelDate(Cell(born));
            var link = Cell(publication);
            var builder = new SubjectBuilder(number, SubjectType.Individual)
            {
                Programmes = Programme,
                ListedOn = Spreadsheets.ExcelDate(Cell(decree)),
                Remarks = birth is { } date ? $"born {date:yyyy-MM-dd}" : null,
                SourceUrl = IsLink(link) ? link : null,
            };
            builder.AddName(Text.Join(" ", first, last), isWeak: false, isPrimary: true);
            if (builder.Build() is { } subject)
            {
                subjects.Add(subject);
            }
        }

        return subjects;
    }

    /// <summary>The publication link as written, when it is one: http or https, no spaces. Kept as written, so every
    /// implementation gives the same text.</summary>
    private static bool IsLink(string? text) =>
        text is not null && (text.StartsWith("https://", StringComparison.Ordinal) || text.StartsWith("http://", StringComparison.Ordinal)) && !text.Any(char.IsWhiteSpace);

    private static bool Is(string cell, string name) => string.Equals(cell.Trim(), name, StringComparison.OrdinalIgnoreCase);
}
