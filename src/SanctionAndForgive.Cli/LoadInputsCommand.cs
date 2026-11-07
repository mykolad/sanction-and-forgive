using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SanctionAndForgive.Data;

namespace SanctionAndForgive.Cli;

/// <summary><c>load-inputs</c>: writes the load test's inputs (<c>tests/load/inputs.csv</c>) from the active lists
/// (section 7, "Load test"). Half are <b>hard</b>: listed names, near misses, Cyrillic, common surnames, addresses and
/// initials, the costliest cases (many candidates). Half are <b>regular</b> (owner's decision, 2026-11-07), what a
/// shipper mostly enters: ordinary Latin company and person names, and listed Latin companies written the way people
/// shorten them ("AAA Inc." or "AAA" for "AAA Corporation"). Deterministic: the same lists and seed give the same file.
/// Reads lists only.</summary>
internal sealed class LoadInputsCommand(ListsDbContext context, TextWriter output)
{
    /// <summary>The screening API's per-field limit (ScreeningsController.MaxFieldLength).</summary>
    private const int MaxField = 300;

    private static readonly string[] CommonSurnames =
    [
        "Ivanov", "Petrov", "Smirnov", "Kuznetsov", "Popov", "Sokolov", "Kovalenko", "Shevchenko", "Bondarenko", "Tkachenko",
        "Kravchenko", "Melnyk", "Boyko", "Kovalchuk", "Kim", "Lee", "Wang", "Zhang", "Khan", "Ali", "Hussein", "Mohammed",
    ];

    private static readonly string[] CyrillicSurnames = ["Иванов", "Петров", "Смирнов", "Коваленко", "Шевченко", "Бондаренко", "Ткаченко", "Мельник", "Бойко", "Кравченко"];

    private static readonly string[] GivenNames = ["Ivan", "Sergey", "Oleksandr", "Mykola", "Anna", "Olena", "Dmitry", "Taras", "Maria", "Yuriy", "Igor", "Natalia"];

    private static readonly string[] CyrillicGivenNames = ["Иван", "Сергей", "Олександр", "Микола", "Анна", "Олена", "Дмитрий", "Тарас", "Марія", "Юрій"];

    private static readonly string[] CompanyWords = ["Northwind", "Bluefield", "Silverline", "Harbor", "Meridian", "Kestrel", "Granite", "Aurora", "Pinecrest", "Lumen", "Volga", "Dnipro", "Baltic", "Carpathian"];

    private static readonly string[] CompanyTails = ["Trading LLC", "Logistics Ltd", "Shipping OÜ", "Group AB", "Holding GmbH", "Import Export", "ТОВ", "ООО"];

    private static readonly string[] RegularCompanyWords =
    [
        "Acme", "Global", "Nordic", "Pacific", "Atlas", "Summit", "Evergreen", "Riverside", "Redwood", "Horizon", "Sterling",
        "Oak", "Brightstar", "Coastal", "United", "Premier", "Delta", "Alpine", "Metro", "Sunrise", "Liberty", "Crown", "Falcon",
        "Green Valley", "Blue Ocean", "Smith & Sons", "Johnson", "Miller", "Baltic Sea", "Tallinn", "Riga", "Hanseatic",
    ];

    private static readonly string[] RegularCompanyActivities =
    [
        "", "", "Trading", "Logistics", "Freight", "Foods", "Textiles", "Electronics", "Machinery", "Imports", "Supplies",
        "Industries", "Technologies", "Packaging", "Furniture", "Auto Parts", "Medical", "Chemicals", "Paper", "Retail",
    ];

    private static readonly string[] RegularLegalForms = ["", "Inc.", "Ltd", "LLC", "GmbH", "AB", "AS", "OÜ", "SIA", "Sp. z o.o.", "B.V.", "S.A.", "Corp.", "Co.", "Limited", "Oy"];

    private static readonly string[] RegularGivenNames =
    [
        "John", "Maria", "James", "Anna", "Peter", "Laura", "Michael", "Sofia", "David", "Emma", "Thomas", "Eva", "Daniel", "Julia",
        "Martin", "Katrin", "Andrew", "Liis", "Mark", "Elena", "Jonas", "Kristina", "Paul", "Ingrid",
    ];

    private static readonly string[] RegularSurnames =
    [
        "Smith", "Johnson", "Brown", "Garcia", "Muller", "Schmidt", "Rossi", "Dubois", "Tamm", "Saar", "Kask", "Berzins", "Nowak",
        "Kowalski", "Jansen", "Andersson", "Nielsen", "Virtanen", "Novak", "Horvath", "Silva", "Martin", "Wilson", "Taylor",
    ];

    // Legal forms as listed companies end or begin with them ("... Corporation", "Limited Liability Company ..."), to
    // drop or swap for a short form.
    private static readonly HashSet<string> TrailingLegalWords = new(
        ["LLC", "LTD", "LIMITED", "INC", "INCORPORATED", "CORP", "CORPORATION", "CO", "COMPANY", "GMBH", "SA", "AG", "JSC", "OJSC", "PJSC", "CJSC", "PLC", "LLP", "BV", "NV", "SRL", "SPA", "OOO", "AO", "TOO", "FZE", "FZCO", "PTE", "PVT", "SDN", "BHD", "DMCC", "LTDA", "SAS"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> LeadingLegalWords = new(
        ["LIMITED", "LIABILITY", "COMPANY", "JOINT", "STOCK", "OPEN", "CLOSED", "PUBLIC", "PRIVATE", "THE", "LLC", "JSC", "OJSC", "PJSC", "CJSC", "OOO", "OAO", "ZAO", "PAO", "AO", "TOO", "FEDERAL", "STATE", "UNITARY", "ENTERPRISE"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<int> RunAsync(string path, int count, int seed, CancellationToken cancellationToken)
    {
        var activeImports = await context.Sources.Where(source => source.ActiveImportId != null).Select(source => source.ActiveImportId!.Value).ToListAsync(cancellationToken);
        var subjects = await context.Subjects.AsNoTracking().Where(subject => activeImports.Contains(subject.ImportId))
            .Include(subject => subject.Names).Include(subject => subject.Addresses).AsSplitQuery()
            .OrderBy(subject => subject.Id).ToListAsync(cancellationToken);
        if (subjects.Count == 0)
        {
            output.WriteLine("No active lists: import some first.");
            return 1;
        }

        var random = new Random(seed);
        var cyrillicNames = subjects.SelectMany(subject => subject.Names.Where(name => name.Script == NameScript.Cyrillic).Select(name => (subject, name.FullName))).ToList();
        var withAddress = subjects.Where(subject => subject.Addresses.Count > 0).ToList();
        var latinEntities = subjects.Where(subject => subject.Type == SubjectType.Entity && subject.PrimaryName.All(c => c < 0x0400 || c > 0x04FF)).ToList();
        var rows = new List<(string? Receiver, string? Company, string? Address, string Kind)>(count);
        while (rows.Count < count)
        {
            // Half regular, half hard: the hard half keeps its own mix (the roll below, out of 100).
            if (random.Next(2) == 0)
            {
                var regular = random.Next(100) switch
                {
                    < 40 => RegularCompany(random),
                    < 70 => RegularPerson(random),
                    _ when latinEntities.Count > 0 => (null, Shorten(latinEntities[random.Next(latinEntities.Count)].PrimaryName, random), null, "short"),
                    _ => RegularCompany(random),
                };
                if (regular.Item2 is null or { Length: <= MaxField })
                {
                    rows.Add(regular);
                }

                continue;
            }

            var roll = random.Next(100);
            var subject = subjects[random.Next(subjects.Count)];
            var row = roll switch
            {
                < 30 => Named(subject, subject.Names.Count > 0 ? subject.Names[random.Next(subject.Names.Count)].FullName : subject.PrimaryName, "listed"),
                < 50 => Named(subject, NearMiss(subject.PrimaryName, random), "near-miss"),
                < 70 when cyrillicNames.Count > 0 => Cyrillic(cyrillicNames[random.Next(cyrillicNames.Count)]),
                < 70 => Named(subject, CyrillicSurnames[random.Next(CyrillicSurnames.Length)] + " " + CyrillicGivenNames[random.Next(CyrillicGivenNames.Length)], "cyrillic"),
                < 90 => Unlisted(random),
                _ => random.Next(2) == 0 && withAddress.Count > 0
                    ? WithAddress(withAddress[random.Next(withAddress.Count)])
                    : (Initials(random), null, null, "initials"),
            };

            // The API refuses fields over its limit (400 before any matching): such a row would measure nothing. Draw
            // again for the same kind of row, so the mix stays as intended.
            if (new[] { row.Item1, row.Item2, row.Item3 }.All(field => field is null || field.Length <= MaxField))
            {
                rows.Add(row);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync("receiverName,companyName,address,kind");
        foreach (var (receiver, company, address, kind) in rows)
        {
            await writer.WriteLineAsync(string.Join(',', Csv(receiver), Csv(company), Csv(address), kind));
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {rows.Count} rows to {path} from {subjects.Count} subjects: ") + string.Join(", ", rows.GroupBy(row => row.Kind).Select(group => $"{group.Key} {group.Count()}")));
        return 0;
    }

    private static (string?, string?, string?, string) Named(Subject subject, string name, string kind) =>
        subject.Type == SubjectType.Individual ? (name, null, null, kind) : (null, name, null, kind);

    private static (string?, string?, string?, string) Cyrillic((Subject Subject, string FullName) pick) => Named(pick.Subject, pick.FullName, "cyrillic");

    private static (string?, string?, string?, string) WithAddress(Subject subject) =>
        subject.Type == SubjectType.Individual
            ? (subject.PrimaryName, null, subject.Addresses[0].Raw, "address")
            : (null, subject.PrimaryName, subject.Addresses[0].Raw, "address");

    private static (string?, string?, string?, string) Unlisted(Random random) => random.Next(2) == 0
        ? ($"{GivenNames[random.Next(GivenNames.Length)]} {CommonSurnames[random.Next(CommonSurnames.Length)]}", null, null, "unlisted")
        : (null, $"{CompanyWords[random.Next(CompanyWords.Length)]} {CompanyWords[random.Next(CompanyWords.Length)]} {CompanyTails[random.Next(CompanyTails.Length)]}", null, "unlisted");

    /// <summary>An ordinary company name: a word or two, sometimes what it does, often a legal form, in Latin.</summary>
    private static (string?, string?, string?, string) RegularCompany(Random random)
    {
        var parts = new[]
        {
            RegularCompanyWords[random.Next(RegularCompanyWords.Length)],
            random.Next(3) == 0 ? RegularCompanyWords[random.Next(RegularCompanyWords.Length)] : string.Empty,
            RegularCompanyActivities[random.Next(RegularCompanyActivities.Length)],
            RegularLegalForms[random.Next(RegularLegalForms.Length)],
        };
        return (null, string.Join(' ', parts.Where(part => part.Length > 0)), null, "company");
    }

    /// <summary>An ordinary person's name in Latin, given name first, sometimes surname first or with a middle initial.</summary>
    private static (string?, string?, string?, string) RegularPerson(Random random)
    {
        var given = RegularGivenNames[random.Next(RegularGivenNames.Length)];
        var surname = RegularSurnames[random.Next(RegularSurnames.Length)];
        var name = random.Next(5) switch
        {
            0 => $"{surname} {given}",
            1 => $"{given} {RegularGivenNames[random.Next(RegularGivenNames.Length)][0]}. {surname}",
            _ => $"{given} {surname}",
        };
        return (name, null, null, "person");
    }

    /// <summary>A listed company's name as people shorten it: the legal form dropped, swapped for a short one, or only
    /// the first word ("AAA Corporation": "AAA Inc.", "AAA").</summary>
    private static string Shorten(string name, Random random)
    {
        static string Bare(string word) => word.Trim('.', ',', '(', ')').Replace(".", string.Empty, StringComparison.Ordinal);
        var words = name.Replace('"', ' ').Replace('«', ' ').Replace('»', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && TrailingLegalWords.Contains(Bare(words[^1])))
        {
            words.RemoveAt(words.Count - 1);
        }

        while (words.Count > 1 && LeadingLegalWords.Contains(Bare(words[0])))
        {
            words.RemoveAt(0);
        }

        var core = string.Join(' ', words).TrimEnd(',');
        return random.Next(3) switch
        {
            0 => $"{core} {(random.Next(2) == 0 ? "Inc." : "Ltd")}",
            1 => core,
            _ => words.Count >= 2 && words[0].Trim(',').Length >= 3 ? words[0].Trim(',') : core,
        };
    }

    /// <summary>A common surname with initials, half in Cyrillic: the case with the most candidates.</summary>
    private static string Initials(Random random) => random.Next(2) == 0
        ? $"{CyrillicSurnames[random.Next(CyrillicSurnames.Length)]} {CyrillicGivenNames[random.Next(CyrillicGivenNames.Length)][0]}.{CyrillicGivenNames[random.Next(CyrillicGivenNames.Length)][0]}."
        : $"{CommonSurnames[random.Next(CommonSurnames.Length)]} {GivenNames[random.Next(GivenNames.Length)][0]}.";

    /// <summary>One letter dropped, doubled or swapped in the longest word, or two words swapped.</summary>
    private static string NearMiss(string name, Random random)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && random.Next(4) == 0)
        {
            (words[0], words[1]) = (words[1], words[0]);
            return string.Join(' ', words);
        }

        var i = Array.IndexOf(words, words.MaxBy(word => word.Length));
        var word = words[i];
        if (word.Length >= 4)
        {
            var at = 1 + random.Next(word.Length - 2);
            words[i] = random.Next(3) switch
            {
                0 => word.Remove(at, 1),
                1 => word.Insert(at, word[at].ToString()),
                _ => string.Concat(word.AsSpan(0, at), word[at + 1].ToString(), word[at].ToString(), word.AsSpan(at + 2)),
            };
        }

        return string.Join(' ', words);
    }

    private static string Csv(string? value) =>
        value is null ? string.Empty : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ') + "\"";
}
