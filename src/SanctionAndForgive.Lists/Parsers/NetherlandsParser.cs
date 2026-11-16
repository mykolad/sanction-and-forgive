namespace SanctionAndForgive.Lists.Parsers;

/// <summary>The Netherlands' national terrorism list (Ministry of Foreign Affairs, <c>Nationale sanctielijst
/// terrorisme</c>): an OpenDocument spreadsheet, a title row, then a header (<c>Surname</c>, <c>First name(s)</c>,
/// <c>Alias</c>, <c>Date of Birth</c>, <c>Place of Birth</c>, <c>Date of ministerial decision</c>, <c>Link official
/// notification</c>) and a row per listing. Columns are found by their header. An organisation has no first name and
/// its place reads <c>n/a: established in …</c>. There is no id column: the notification in the Government Gazette
/// (<c>Stcrt 2015, 26627</c>), one per listing, is the id. A surname written <c>Dusseldorp, van</c> is
/// <c>van Dusseldorp</c>.</summary>
public sealed class NetherlandsParser : IListParser
{
    public const string Programme = "Sanctieregeling terrorisme 2007-II";

    public string SourceCode => "nl-terrorism";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        var rows = Spreadsheets.OdsRows(stream, cancellationToken);
        var headerAt = rows.FindIndex(row => row.Count > 1 && Is(row[0], "surname") && Is(row[1], "first name"));
        if (headerAt < 0)
        {
            throw new ListFormatException("No header row (Surname, First name(s), …) in the Dutch terrorism list.");
        }

        var header = rows[headerAt];
        int Column(string start) => header.ToList().FindIndex(cell => Is(cell, start)) is var index and >= 0
            ? index
            : throw new ListFormatException($"The Dutch terrorism list has no \"{start}\" column.");
        int surname = Column("surname"), given = Column("first name"), alias = Column("alias"), born = Column("date of birth"),
            place = Column("place of birth"), decided = Column("date of ministerial decision"), notice = Column("link official notification");

        var subjects = new List<ParsedSubject>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Skip(headerAt + 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? Cell(int column) => column < row.Count ? Text.Clean(row[column]) : null;
            var family = Cell(surname);
            if (family is null)
            {
                continue;
            }

            var first = Cell(given);
            var where = Cell(place);
            var type = first is not null ? SubjectType.Individual
                : where?.StartsWith("n/a", StringComparison.OrdinalIgnoreCase) == true ? SubjectType.Entity
                : SubjectType.Unknown;

            // One listing per notification; a repeated one (never seen) still gets an id of its own.
            var id = Cell(notice) ?? Text.Join(" ", family, first, Cell(born)) ?? family;
            for (var n = 2; !ids.Add(id); n++)
            {
                id = $"{Cell(notice) ?? family} #{n}";
            }

            var builder = new SubjectBuilder(id, type)
            {
                Programmes = Programme,
                ListedOn = Text.ParseDate(Cell(decided), "d-M-yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd/MM/yyyy"),
                Remarks = Text.Join("; ", Cell(born) is { } date ? (type == SubjectType.Entity ? "established " : "born ") + date : null, where),
            };
            builder.AddName(type == SubjectType.Individual ? Text.Join(" ", first, Surname(family)) : family, isWeak: false, isPrimary: true);
            builder.AddName(Cell(alias), isWeak: false, isPrimary: false);
            if (builder.Build() is { } subject)
            {
                subjects.Add(subject);
            }
        }

        return subjects;
    }

    /// <summary>"Dusseldorp, van" is "van Dusseldorp": a particle written after a comma goes first.</summary>
    internal static string Surname(string family)
    {
        var comma = family.LastIndexOf(',');
        return comma > 0 && comma < family.Length - 1 && !family[(comma + 1)..].Trim().Contains(' ', StringComparison.Ordinal)
            ? $"{family[(comma + 1)..].Trim()} {family[..comma].Trim()}"
            : family;
    }

    private static bool Is(string cell, string start) =>
        cell.Trim().StartsWith(start, StringComparison.OrdinalIgnoreCase);
}
