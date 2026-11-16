using System.Text;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>Japan's asset-freeze list (Ministry of Finance, <c>資産凍結等対象者一覧</c>): CSV in UTF-8 with a byte
/// order mark, published under a dated name (<c>shisantouketsuYYYYMMDD.csv</c>) linked from the list page. A header
/// row of 32 Japanese column names, then a row per party, every text in Japanese and in English. Columns are found by
/// their header. <c>個人</c> (person) rows are people, <c>団体</c> (group) rows entities. The English name is the
/// primary name (one that holds several, separated by semicolons, gives the rest as aliases); the Japanese name,
/// aliases (<c>別名・別称</c>) and former names (<c>旧称</c>) are aliases, and the aliases the list calls not enough to
/// identify (<c>確定に十分でない別名</c>) are weak. Lists inside a cell are separated by semicolons, half or full
/// width. Addresses are a city or other detail and a country, each a list: paired when they have as many parts, else
/// each part on its own; in English when given, otherwise in Japanese. Listed on is the date of the notice
/// (<c>告示日付</c>, <c>2002.3.1</c>, later amendments after it). The programme is the list's category number
/// (<c>区分</c>): the list page names each, but no file maps the numbers. No per-record page.</summary>
public sealed class JapanParser : IListParser
{
    public const string Programme = "Foreign Exchange and Foreign Trade Act";

    /// <summary>"At the time of listing", written after some countries.</summary>
    private static readonly string[] Annotations = ["(リスト掲載時点)", "（リスト掲載時点）"];

    public string SourceCode => "jp-mof";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var rows = ManualCsvReader.Rows(reader).GetEnumerator();
        if (!rows.MoveNext() || !rows.Current.Any(cell => cell.Trim() == "番号") || !rows.Current.Any(cell => cell.Trim() == "氏名（英語）"))
        {
            throw new ListFormatException("No header row (区分, 番号, …, 氏名（英語）, …) in Japan's asset-freeze list.");
        }

        var header = rows.Current.Select(cell => cell.Trim()).ToList();
        int Column(string name) => header.IndexOf(name) is var index and >= 0
            ? index
            : throw new ListFormatException($"Japan's asset-freeze list has no \"{name}\" column.");
        int category = Column("区分"), number = Column("番号"), noticeDate = Column("告示日付"), kind = Column("個人・団体"),
            nameJa = Column("氏名（日本語）"), nameEn = Column("氏名（英語）"), aliasJa = Column("別名・別称（日本語）"), aliasEn = Column("別名・別称（英語）"),
            formerJa = Column("旧称（日本語）"), formerEn = Column("旧称（英語）"), weakJa = Column("確定に十分でない別名（日本語）"), weakEn = Column("確定に十分でない別名（英語）"),
            titleEn = Column("称号（英語）"), positionEn = Column("役職（英語）"), born = Column("生年月日"), birthplaceEn = Column("出生地（英語）"),
            nationalityEn = Column("国籍（英語）"), countryJa = Column("住所・所在地（国）（日本語）"), cityJa = Column("住所・所在地（都市その他の情報）（日本語）"),
            countryEn = Column("住所・所在地（国）（英語）"), cityEn = Column("住所・所在地（都市その他の情報）（英語）"), other = Column("その他の情報");

        while (rows.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows.Current;
            string? Cell(int column) => column < row.Count ? Text.Clean(row[column]) : null;
            if (row.All(cell => Text.Clean(cell) is null))
            {
                continue;
            }

            var id = Cell(number) ?? throw new ListFormatException("A row in Japan's asset-freeze list has no 番号 (number).");
            var builder = new SubjectBuilder(id, Cell(kind) switch
            {
                "個人" => SubjectType.Individual,
                "団体" => SubjectType.Entity,
                _ => SubjectType.Unknown,
            })
            {
                Programmes = Cell(category) is { } c ? $"{Programme}, category {c}" : Programme,
                ListedOn = Text.ParseDate(Cell(noticeDate)?.Split(' ')[0], "yyyy.M.d"),
                Remarks = Text.Join("; ", Cell(titleEn), Cell(positionEn), Cell(born) is { } b ? "born " + b : null, Cell(birthplaceEn),
                    Cell(nationalityEn) is { } n ? "nationality " + n : null, Cell(other)),
            };
            var primary = true;
            foreach (var name in Split(Cell(nameEn)))
            {
                builder.AddName(name, isWeak: false, isPrimary: primary);
                primary = false;
            }

            foreach (var column in (int[])[nameJa, aliasEn, aliasJa, formerEn, formerJa])
            {
                foreach (var name in Split(Cell(column)))
                {
                    builder.AddName(name, isWeak: false, isPrimary: false);
                }
            }

            foreach (var column in (int[])[weakEn, weakJa])
            {
                foreach (var name in Split(Cell(column)))
                {
                    builder.AddName(name, isWeak: true, isPrimary: false);
                }
            }

            var english = Cell(cityEn) is not null || Cell(countryEn) is not null;
            var cities = Split(Cell(english ? cityEn : cityJa));
            var countries = Split(Cell(english ? countryEn : countryJa));
            if (cities.Count == countries.Count)
            {
                for (var i = 0; i < cities.Count; i++)
                {
                    builder.AddAddress(Text.Join(", ", cities[i], countries[i]), countryCode: null);
                }
            }
            else
            {
                foreach (var part in cities.Concat(countries))
                {
                    builder.AddAddress(part, countryCode: null);
                }
            }

            if (builder.Build() is { } subject)
            {
                yield return subject;
            }
        }
    }

    /// <summary>A cell's list: split at semicolons (half or full width), each part without the "at the time of
    /// listing" note and the commas left at its end.</summary>
    private static List<string> Split(string? cell)
    {
        var parts = new List<string>();
        foreach (var raw in (cell ?? string.Empty).Split([';', '；']))
        {
            var part = raw;
            foreach (var annotation in Annotations)
            {
                part = part.Replace(annotation, " ", StringComparison.Ordinal);
            }

            if (Text.Clean(part)?.TrimEnd(',', '、', ' ') is { Length: > 0 } cleaned)
            {
                parts.Add(cleaned);
            }
        }

        return parts;
    }
}
