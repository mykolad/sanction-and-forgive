using System.Text;

namespace SanctionAndForgive.Lists;

/// <summary>Manual records (section 4, "Script"): UTF-8 CSV with a header,
/// <c>external_id,type,name,aliases,address,country,programme,listed_on,remarks</c>. Only external_id, type
/// (<c>Individual</c> or <c>Entity</c>) and name are required. Aliases are separated by <c>|</c>; the address is
/// free text; country is an ISO 3166-1 alpha-2 code; listed_on is <c>yyyy-MM-dd</c>. Fields may be quoted ("…",
/// with "" for a quote).</summary>
public static class ManualCsvReader
{
    private static readonly string[] Columns = ["external_id", "type", "name", "aliases", "address", "country", "programme", "listed_on", "remarks"];
    private static readonly string[] RequiredColumns = ["external_id", "type", "name"];

    /// <summary>Throws <see cref="ListFormatException"/> naming the line for a missing column or a bad value.</summary>
    public static List<ParsedSubject> Read(TextReader reader)
    {
        var rows = Rows(reader).ToList();
        if (rows.Count == 0)
        {
            throw new ListFormatException("The CSV is empty: a header is required.");
        }

        var header = rows[0].Select(column => column.Trim().ToLowerInvariant()).ToList();
        var index = Columns.ToDictionary(column => column, column => header.IndexOf(column));
        foreach (var required in RequiredColumns.Where(required => index[required] < 0))
        {
            throw new ListFormatException($"The header has no {required} column.");
        }

        var subjects = new List<ParsedSubject>();
        for (var line = 1; line < rows.Count; line++)
        {
            var row = rows[line];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string? Field(string column) => index[column] is >= 0 and var i && i < row.Count ? Text.Clean(row[i]) : null;
            var externalId = Field("external_id") ?? throw new ListFormatException($"Line {line + 1}: external_id is required.");
            var name = Field("name") ?? throw new ListFormatException($"Line {line + 1}: name is required.");
            var type = Field("type")?.ToLowerInvariant() switch
            {
                "individual" => SubjectType.Individual,
                "entity" => SubjectType.Entity,
                var other => throw new ListFormatException($"Line {line + 1}: type must be Individual or Entity, not '{other}'."),
            };
            var country = Field("country")?.ToUpperInvariant();
            if (country is not null && (country.Length != 2 || !country.All(char.IsAsciiLetterUpper)))
            {
                throw new ListFormatException($"Line {line + 1}: country must be a two-letter ISO code, not '{country}'.");
            }

            var listedOn = Field("listed_on");
            var date = Text.ParseDate(listedOn, "yyyy-MM-dd");
            if (listedOn is not null && date is null)
            {
                throw new ListFormatException($"Line {line + 1}: listed_on must be yyyy-MM-dd, not '{listedOn}'.");
            }

            var names = new List<ParsedName> { new(name, false, Text.DetectScript(name)) };
            names.AddRange((Field("aliases") ?? string.Empty).Split('|').Select(Text.Clean).OfType<string>()
                .Where(alias => alias != name).Distinct().Select(alias => new ParsedName(alias, false, Text.DetectScript(alias))));
            var address = Field("address");
            List<ParsedAddress> addresses = address is not null || country is not null ? [new ParsedAddress(address ?? country!, country)] : [];
            subjects.Add(new ParsedSubject(externalId, type, name, names, addresses, Field("programme"), date, Field("remarks"), SourceUrl: null));
        }

        return subjects;
    }

    /// <summary>RFC 4180 rows: commas, quoted fields with "" for a quote, line breaks inside quotes.</summary>
    private static IEnumerable<List<string>> Rows(TextReader reader)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            any = true;
            var ch = (char)c;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        field.Append('"');
                        reader.Read();
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row;
                    row = [];
                    any = false;
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (quoted)
        {
            throw new ListFormatException("The CSV ends inside a quoted field: a closing quote is missing.");
        }

        if (any || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
