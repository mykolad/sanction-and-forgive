using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SanctionAndForgive.Data;

namespace SanctionAndForgive.Cli;

/// <summary><c>load-inputs</c>: writes the load test's inputs (<c>tests/load/inputs.csv</c>) from the active lists
/// (section 7, "Load test"): about 30% listed names, 20% near misses, 20% Cyrillic, 20% unlisted names, 10% with an
/// address or as a surname with initials. Common surnames are the costliest case (many candidates), so the unlisted
/// and initials rows use them. Deterministic: the same lists and seed give the same file. Reads lists only.</summary>
internal sealed class LoadInputsCommand(ListsDbContext context, TextWriter output)
{
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
        var rows = new List<(string? Receiver, string? Company, string? Address, string Kind)>(count);
        for (var i = 0; i < count; i++)
        {
            var roll = random.Next(100);
            var subject = subjects[random.Next(subjects.Count)];
            rows.Add(roll switch
            {
                < 30 => Named(subject, subject.Names.Count > 0 ? subject.Names[random.Next(subject.Names.Count)].FullName : subject.PrimaryName, "listed"),
                < 50 => Named(subject, NearMiss(subject.PrimaryName, random), "near-miss"),
                < 70 when cyrillicNames.Count > 0 => Cyrillic(cyrillicNames[random.Next(cyrillicNames.Count)]),
                < 70 => Named(subject, CyrillicSurnames[random.Next(CyrillicSurnames.Length)] + " " + CyrillicGivenNames[random.Next(CyrillicGivenNames.Length)], "cyrillic"),
                < 90 => Unlisted(random),
                _ => random.Next(2) == 0 && withAddress.Count > 0
                    ? WithAddress(withAddress[random.Next(withAddress.Count)])
                    : (Initials(random), null, null, "initials"),
            });
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
