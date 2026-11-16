using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>The US UFLPA Entity List (Department of Homeland Security): the list page itself, HTML, with one table per
/// section of the Uyghur Forced Labor Prevention Act (<c>Section 2(d)(2)(B)(i)</c> and so on, written before the
/// table), each a header (<c>Name of Entity</c> or <c>Entity Name</c>, <c>Effective Date</c>) and a row per entity.
/// Aliases are written in the name, in parentheses: <c>(also known as A; B; and C)</c>, <c>(formerly known as …)</c>,
/// <c>(including three aliases: …)</c>, <c>(and two aliases: …)</c>, or an acronym alone (<c>(XPCC)</c>); other
/// parentheses are part of the name (<c>(Shanshan)</c>). A trailing "and subsidiaries" or "and its subordinate and
/// affiliated entities" goes to the remarks. A page cut short (a table or the document not closed), a table without its
/// header, or a row that isn't a name and a date is a format error. An entity listed under several sections is one record: its sections
/// joined, the earliest date, every spelling of its name kept (two that differ only by a final full stop are one
/// entity). The id is the first spelling of the name without its aliases. No per-record page.</summary>
public sealed partial class UflpaParser : IListParser
{
    public const string Programme = "Uyghur Forced Labor Prevention Act";

    public string SourceCode => "us-uflpa";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        string html;
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            html = reader.ReadToEnd();
        }

        var tables = HtmlTables.Read(html);
        if (tables.Count == 0)
        {
            throw new ListFormatException("The UFLPA page has no entity tables.");
        }

        // A page cut short loses its last rows or tables without a trace: the document must end, and every table close.
        if (!HtmlTables.IsComplete(html, tables.Count))
        {
            throw new ListFormatException("The UFLPA page is cut short: a table or the document doesn't close.");
        }

        var entities = new List<Entity>();
        var byId = new Dictionary<string, Entity>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (table.Rows.Count == 0 || table.Rows[0] is not [var nameHeader, var dateHeader]
                || !nameHeader.Contains("Entity", StringComparison.OrdinalIgnoreCase) || !dateHeader.Contains("Effective Date", StringComparison.OrdinalIgnoreCase))
            {
                throw new ListFormatException("A UFLPA table has no header (Name of Entity, Effective Date).");
            }

            var section = Section().Matches(table.Before) is { Count: > 0 } sections ? sections[^1].Value : null;
            foreach (var row in table.Rows.Skip(1))
            {
                if (row is not [var cell, var dateCell] || Text.Clean(cell) is not { } written || Text.ParseDate(dateCell, "MMMM d, yyyy") is not { } date)
                {
                    throw new ListFormatException("A UFLPA row isn't a name and an effective date.");
                }

                var (primary, aliases, remark) = SplitName(written);

                // "Co., Ltd" and "Co., Ltd." in two tables are one entity: the first spelling is its id.
                var key = primary.TrimEnd('.', ' ');
                if (!byId.TryGetValue(key, out var entity))
                {
                    entity = new Entity(primary);
                    byId[key] = entity;
                    entities.Add(entity);
                }

                aliases.Add(primary);

                entity.Add(aliases, remark, section, date);
            }
        }

        foreach (var entity in entities)
        {
            if (entity.Build() is { } subject)
            {
                yield return subject;
            }
        }
    }

    /// <summary>A name as written: the name without its alias parentheses or trailing remark, the aliases, and the
    /// remark (null if none).</summary>
    internal static (string Primary, List<string> Aliases, string? Remark) SplitName(string written)
    {
        var aliases = new List<string>();
        var name = new StringBuilder();
        var i = 0;
        while (i < written.Length)
        {
            if (written[i] != '(' || Close(written, i) is not { } close)
            {
                name.Append(written[i]);
                i++;
                continue;
            }

            var inside = written[(i + 1)..close].Trim();
            if (AliasLead().Match(inside) is { Success: true } lead)
            {
                foreach (var part in inside[lead.Length..].Split(';'))
                {
                    var alias = Text.Clean(part);
                    alias = alias is not null && alias.StartsWith("and ", StringComparison.OrdinalIgnoreCase) ? Text.Clean(alias[4..]) : alias;
                    if (alias is not null)
                    {
                        aliases.Add(alias);
                    }
                }
            }
            else if (Acronym().IsMatch(inside))
            {
                aliases.Add(inside);
            }
            else
            {
                name.Append(written, i, close - i + 1);
            }

            i = close + 1;
        }

        var primary = Text.Clean(name.ToString()) ?? written;
        string? remark = null;
        if (Trailing().Match(primary) is { Success: true } trailing && trailing.Index > 0)
        {
            remark = Text.Clean(trailing.Groups[1].Value);
            primary = Text.Clean(primary[..trailing.Index]) ?? primary;
        }

        return (primary, aliases, remark);
    }

    /// <summary>The index of the parenthesis closing the one at <paramref name="open"/>, or null if it isn't closed.</summary>
    private static int? Close(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return i;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(?:also known as|formerly known as|(?:and|including) (?:\w+ )?alias(?:es)?:)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AliasLead();

    [GeneratedRegex(@"^[A-Z0-9&-]{2,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex Acronym();

    [GeneratedRegex(@",?\s+(and (?:its subordinate and affiliated entities|subsidiaries))\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Trailing();

    [GeneratedRegex(@"Section\s+\d+\([a-z]\)\(\d+\)\([A-Z]\)\([ivx]+\)", RegexOptions.CultureInvariant)]
    private static partial Regex Section();

    /// <summary>One entity, from every row that names it.</summary>
    private sealed class Entity(string primary)
    {
        private readonly List<string> names = [];
        private readonly List<string> sections = [];
        private readonly List<string> remarks = [];
        private DateOnly? listedOn;

        public void Add(List<string> aliases, string? remark, string? section, DateOnly date)
        {
            names.AddRange(aliases);
            if (section is not null && !sections.Contains(section))
            {
                sections.Add(section);
            }

            if (remark is not null && !remarks.Contains(remark))
            {
                remarks.Add(remark);
            }

            if (listedOn is null || date < listedOn)
            {
                listedOn = date;
            }
        }

        public ParsedSubject? Build()
        {
            var builder = new SubjectBuilder(primary, SubjectType.Entity)
            {
                Programmes = sections.Count == 0 ? Programme : $"{Programme}, {string.Join("; ", sections)}",
                ListedOn = listedOn,
                Remarks = Text.Join("; ", remarks),
            };
            builder.AddName(primary, isWeak: false, isPrimary: true);
            foreach (var name in names)
            {
                builder.AddName(name, isWeak: false, isPrimary: false);
            }

            return builder.Build();
        }
    }
}

/// <summary>The tables of an HTML page, read with regular expressions (no HTML library): each table's rows, each row's
/// cells (<c>td</c> or <c>th</c>) as text (tags dropped, character references decoded, whitespace collapsed), and the
/// page's text between the previous table and this one (headings name a table).</summary>
public static partial class HtmlTables
{
    public sealed record Table(string Before, List<List<string>> Rows);

    public static List<Table> Read(string html)
    {
        var tables = new List<Table>();
        var previousEnd = 0;
        foreach (Match table in TableElement().Matches(html))
        {
            var before = TextOf(html[previousEnd..table.Index]);
            previousEnd = table.Index + table.Length;
            var rows = new List<List<string>>();
            foreach (Match row in RowElement().Matches(table.Groups[1].Value))
            {
                var cells = CellElement().Matches(row.Groups[1].Value).Select(cell => TextOf(cell.Groups[1].Value)).ToList();
                if (cells.Count > 0)
                {
                    rows.Add(cells);
                }
            }

            tables.Add(new Table(before, rows));
        }

        return tables;
    }

    /// <summary>Whether a page holding <paramref name="tables"/> read tables is whole: as many table openings and
    /// closings as tables read, and the document's closing tag.</summary>
    public static bool IsComplete(string html, int tables) =>
        TableOpen().Count(html) == tables && TableClose().Count(html) == tables && DocumentEnd().IsMatch(html);

    /// <summary>A fragment's text: tags as spaces, character references decoded, whitespace collapsed.</summary>
    public static string TextOf(string fragment) =>
        Text.Clean(WebUtility.HtmlDecode(Tag().Replace(fragment, " "))) ?? string.Empty;

    [GeneratedRegex(@"<table\b[^>]*>(.*?)</table\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TableElement();

    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex RowElement();

    [GeneratedRegex(@"<t[dh]\b[^>]*>(.*?)</t[dh]\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CellElement();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"<table\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TableOpen();

    [GeneratedRegex(@"</table\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TableClose();

    [GeneratedRegex(@"</html\s*>\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocumentEnd();
}
