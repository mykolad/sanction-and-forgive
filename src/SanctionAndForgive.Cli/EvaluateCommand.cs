using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SanctionAndForgive.Data;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Cli;

/// <summary><c>evaluate</c> (Phase 6): measures the matcher on the active lists and writes the report. Synthetic
/// calibration only: perturbed list names (positives), Cyrillic aliases against Latin-only lists, and made-up names
/// (negatives). Deterministic (fixed seed). Reads lists only; nothing is stored.</summary>
internal sealed class EvaluateCommand(ListsDbContext context, MatchingOptions options, TextWriter output)
{
    private const int SamplePerSource = 200;
    private const int Negatives = 600;

    private static readonly string[] Perturbations = ["typo", "diacritics dropped", "words swapped", "legal form changed", "other romanisation", "surname and initials"];

    private static readonly string[] RuGiven = ["Александр", "Дмитрий", "Сергей", "Андрей", "Алексей", "Иван", "Михаил", "Николай", "Владимир", "Павел", "Ольга", "Елена", "Наталья", "Татьяна", "Анна"];
    private static readonly string[] UkGiven = ["Олександр", "Дмитро", "Сергій", "Андрій", "Олексій", "Іван", "Михайло", "Микола", "Володимир", "Тарас", "Оксана", "Олена", "Наталія", "Тетяна", "Ганна"];
    private static readonly string[] Patronymics = ["Иванович", "Петрович", "Сергеевич", "Александрович", "Миколайович", "Олександрович", "Ивановна", "Петрівна"];
    private static readonly string[] Surnames = ["Зайченко", "Литвиненко", "Гнатюк", "Сидоренко", "Ярошенко", "Кудрявцев", "Белоусов", "Мартыненко", "Пономарчук", "Савчук", "Демченко", "Олейник", "Голубев", "Тихонов", "Фролов", "Мельниченко", "Рудницкий", "Захарченко", "Филатов", "Комаров"];
    private static readonly string[] LatinGiven = ["Oleksandr", "Dmytro", "Sergei", "Andrei", "Olena", "Natalia", "Mikhail", "Pavlo", "Iryna", "Viktor", "John", "Maria", "Ahmed", "Chen", "Lucia"];
    private static readonly string[] LatinSurnames = ["Zaichenko", "Lytvynenko", "Hnatiuk", "Sydorenko", "Kudryavtsev", "Belousov", "Martynenko", "Savchuk", "Demchenko", "Oliinyk", "Holubev", "Tikhonov", "Fernandes", "Okafor", "Lindqvist"];
    private static readonly string[] CompanyCores = ["Северный Ветер", "Ясная Поляна", "Зелений Гай", "Сонячна Долина", "Brightwater", "Copperleaf", "Stonebridge", "Bluecrest", "Ivory Harbour", "Maple Ridge", "Tallinn Fjord", "Kestrel Peak"];
    private static readonly string[] CompanyForms = ["ООО {0}", "ТОВ «{0}»", "{0} LLC", "{0} Trading Ltd", "{0} Logistics OÜ", "АО {0}"];

    public async Task<int> RunAsync(string path, int seed, CancellationToken cancellationToken)
    {
        var sources = await context.Sources.AsNoTracking().Where(source => source.ActiveImportId != null).OrderBy(source => source.Code).ToListAsync(cancellationToken);
        var lists = new List<(Source Source, List<ListedSubject> Subjects, ListIndex Index)>();
        foreach (var source in sources)
        {
            var subjects = await context.Subjects.AsNoTracking().Where(subject => subject.ImportId == source.ActiveImportId)
                .Include(subject => subject.Names).Include(subject => subject.Addresses).AsSplitQuery().OrderBy(subject => subject.Id)
                .ToListAsync(cancellationToken);
            var listed = subjects.Select(SubjectMapper.ToListed).ToList();
            lists.Add((source, listed, ListIndex.Build(listed)));
            output.WriteLine($"{source.Code}: {listed.Count} subjects indexed");
        }

        if (lists.Count == 0)
        {
            output.WriteLine("No active lists: import some first.");
            return 1;
        }

        var screener = new Screener(options);
        var random = new Random(seed);
        var positives = new List<Trial>();
        var cyrillic = new List<Trial>();

        foreach (var (source, subjects, index) in lists)
        {
            var list = new ScreenedList(source.Code, source.Name, source.LastCheckedAt, index);
            var sample = subjects.Where(subject => IsLatin(subject.PrimaryName) && Words(subject.PrimaryName).Length >= 1)
                .OrderBy(_ => random.Next()).Take(SamplePerSource).ToList();
            foreach (var subject in sample)
            {
                foreach (var kind in Perturbations)
                {
                    if (Perturb(subject, kind, random) is { } query)
                    {
                        positives.Add(Run(screener, list, source.Code, kind, query, subject));
                    }
                }
            }

            // Cyrillic aliases against the same list with every Cyrillic name removed: only transliteration can find them.
            var withBoth = subjects.Where(subject => subject.Names.Any(name => HasCyrillic(name.FullName)) && subject.Names.Any(name => IsLatin(name.FullName))).ToList();
            if (withBoth.Count > 0)
            {
                // The primary name is indexed too: a Cyrillic primary takes the first Latin name's place, and a subject
                // with no Latin name left is left out.
                var latinOnly = ListIndex.Build(subjects
                    .Select(subject => (Subject: subject, Latin: subject.Names.Where(name => !HasCyrillic(name.FullName)).ToList()))
                    .Where(item => item.Latin.Count > 0)
                    .Select(item => item.Subject with
                    {
                        Names = item.Latin,
                        PrimaryName = HasCyrillic(item.Subject.PrimaryName) ? item.Latin[0].FullName : item.Subject.PrimaryName,
                    }));
                var latinList = list with { Index = latinOnly };
                foreach (var subject in withBoth.OrderBy(_ => random.Next()).Take(SamplePerSource))
                {
                    var alias = subject.Names.Where(name => HasCyrillic(name.FullName)).OrderBy(_ => random.Next()).First().FullName;
                    cyrillic.Add(Run(screener, latinList, source.Code, "Cyrillic alias", alias, subject));
                }
            }

            output.WriteLine($"{source.Code}: {positives.Count(trial => trial.List == source.Code)} perturbed and {cyrillic.Count(trial => trial.List == source.Code)} Cyrillic queries");
        }

        // Negatives: made-up names against every list at once; any Review or Match is a false positive.
        var allLists = lists.Select(item => new ScreenedList(item.Source.Code, item.Source.Name, item.Source.LastCheckedAt, item.Index)).ToList();
        var negatives = new List<(string Query, string Kind, int Percent, string? Hit)>();
        foreach (var (query, kind) in MadeUpNames(random).Take(Negatives))
        {
            var result = screener.Screen(new ScreeningRequest(kind == "person" ? query : null, kind == "person" ? null : query, null), allLists);
            var best = result.Entries.SelectMany(entry => entry.Cells).Where(cell => cell.Best is not null).MaxBy(cell => cell.Percent);
            negatives.Add((query, kind, best?.Percent ?? 0, best?.Best?.Subject.PrimaryName));
        }

        var report = Report(lists.Select(item => (item.Source.Code, item.Subjects.Count)).ToList(), positives, cyrillic, negatives, screener.MatcherVersion, seed);
        await File.WriteAllTextAsync(path, report, new UTF8Encoding(false), cancellationToken);
        output.WriteLine($"Wrote {path}");
        return 0;
    }

    private static Trial Run(Screener screener, ScreenedList list, string code, string kind, string query, ListedSubject expected)
    {
        var isPerson = expected.Kind == SubjectKind.Individual;
        var result = screener.Screen(new ScreeningRequest(isPerson ? query : null, isPerson ? null : query, null), [list]);
        var cell = result.Entries[0].Cells[0];
        var found = cell.Best is { } best && best.Subject.ExternalId == expected.ExternalId
            ? best
            : cell.Others.FirstOrDefault(other => other.Subject.ExternalId == expected.ExternalId);
        return new Trial(code, kind, query, expected.PrimaryName, cell.Percent ?? 0, found?.Percent, cell.Best?.Subject.ExternalId == expected.ExternalId);
    }

    /// <summary>A perturbed copy of the subject's primary name, or null when this perturbation doesn't apply.</summary>
    private static string? Perturb(ListedSubject subject, string kind, Random random)
    {
        var name = subject.PrimaryName;
        var words = Words(name);
        switch (kind)
        {
            case "typo":
                var longest = Array.IndexOf(words, words.MaxBy(word => word.Length));
                var word = words[longest];
                if (word.Length < 5)
                {
                    return null;
                }

                var at = 1 + random.Next(word.Length - 2);
                var edit = random.Next(3);

                // Swapping two equal letters ("Shipping") changes nothing: drop one instead.
                if (edit == 2 && word[at] == word[at + 1])
                {
                    edit = 0;
                }

                words[longest] = edit switch
                {
                    0 => word.Remove(at, 1),
                    1 => word.Insert(at, word[at].ToString()),
                    _ => string.Concat(word.AsSpan(0, at), word[at + 1].ToString(), word[at].ToString(), word.AsSpan(at + 2)),
                };
                var typo = string.Join(' ', words);
                return typo == name ? null : typo;
            case "diacritics dropped":
                var stripped = StripDiacritics(name);
                return stripped == name ? null : stripped;
            case "words swapped":
                return words.Length < 2 ? null : string.Join(' ', words.Reverse());
            case "legal form changed":
                if (subject.Kind == SubjectKind.Individual)
                {
                    return null;
                }

                // The name without its legal words, with a legal form it didn't have.
                var core = words.Where(w => !LegalWords.Contains(w.Trim('.', ',').ToLowerInvariant())).ToArray();
                if (core.Length == 0)
                {
                    return null;
                }

                var had = words.Select(w => w.Trim('.', ',').ToLowerInvariant()).ToHashSet();
                var form = ReplacementForms.First(candidate => !had.Contains(candidate.ToLowerInvariant()));
                var changed = $"{string.Join(' ', core)} {form}";
                return changed == name ? null : changed;
            case "other romanisation":
                var flipped = OtherRomanisation(name);
                return flipped == name ? null : flipped;
            case "surname and initials":
                if (subject.Kind != SubjectKind.Individual || words.Length < 2)
                {
                    return null;
                }

                // Only where the surname is certain: exactly one word in capitals among mixed-case ones ("Ivan IVANOV",
                // as OFAC, the EU and the UK write it). All capitals (UN) or no capitals (SECO) leave the order unknown.
                var capitals = words.Where(w => w.Count(char.IsLetter) > 1 && w.All(c => !char.IsLetter(c) || char.IsUpper(c))).ToList();
                if (capitals.Count != 1 || capitals.Count == words.Length)
                {
                    return null;
                }

                var surname = capitals[0];
                var initials = string.Concat(words.Where(w => w != surname && w.Length > 0 && char.IsLetter(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0]) + "."));
                return initials.Length == 0 ? null : $"{surname.Trim(',')} {initials}";
            default:
                return null;
        }
    }

    private static readonly string[] ReplacementForms = ["LLC", "Ltd", "JSC", "GmbH"];

    private static readonly HashSet<string> LegalWords = ["llc", "ltd", "limited", "inc", "corp", "corporation", "jsc", "ojsc", "cjsc", "pjsc", "plc", "gmbh", "co", "company", "ooo", "oao", "zao", "pao", "ao", "sa", "ag", "ab", "oy", "bv", "llp"];

    /// <summary>The same name in another common romanisation: the differences between passport, BGN/PCGN and
    /// Ukrainian national spellings (y/i, iy/yi, kh/h, yu/iu, ya/ia, ff/v).</summary>
    private static string OtherRomanisation(string name)
    {
        var builder = new StringBuilder(name);
        (string From, string To)[] swaps = [("iy", "yi"), ("yy", "yi"), ("kh", "h"), ("Kh", "H"), ("yu", "iu"), ("Yu", "Iu"), ("ya", "ia"), ("Ya", "Ia"), ("ye", "ie"), ("Ye", "E"), ("ts", "tz"), ("ii", "iy")];
        foreach (var (from, to) in swaps)
        {
            builder.Replace(from, to);
        }

        // The old -ff ending (Ivanoff) only at the end of a word: "Officer" isn't a romanisation.
        return System.Text.RegularExpressions.Regex.Replace(builder.ToString(), @"ov\b", "off");
    }

    private static IEnumerable<(string Query, string Kind)> MadeUpNames(Random random)
    {
        while (true)
        {
            yield return random.Next(6) switch
            {
                0 => ($"{RuGiven[random.Next(RuGiven.Length)]} {Patronymics[random.Next(Patronymics.Length)]} {Surnames[random.Next(Surnames.Length)]}", "person"),
                1 => ($"{UkGiven[random.Next(UkGiven.Length)]} {Surnames[random.Next(Surnames.Length)]}", "person"),
                2 => ($"{Surnames[random.Next(Surnames.Length)]} {RuGiven[random.Next(RuGiven.Length)][0]}.{Patronymics[random.Next(Patronymics.Length)][0]}.", "person"),
                3 => ($"{LatinGiven[random.Next(LatinGiven.Length)]} {LatinSurnames[random.Next(LatinSurnames.Length)]}", "person"),
                _ => (string.Format(CultureInfo.InvariantCulture, CompanyForms[random.Next(CompanyForms.Length)], CompanyCores[random.Next(CompanyCores.Length)]), "company"),
            };
        }
    }

    private static string Report(
        List<(string Code, int Subjects)> lists,
        List<Trial> positives,
        List<Trial> cyrillic,
        List<(string Query, string Kind, int Percent, string? Hit)> negatives,
        string matcherVersion,
        int seed)
    {
        static string Pct(int part, int whole) => whole == 0 ? "–" : (100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        static bool Recalled(Trial trial) => trial.FoundPercent >= 50;

        var md = new StringBuilder();
        md.AppendLine("# Matching evaluation");
        md.AppendLine();
        md.AppendLine("**This is synthetic calibration.** The positives are the lists' own names, perturbed by code; the negatives are");
        md.AppendLine("names made up from common Russian, Ukrainian and other name parts and invented companies. No real screening");
        md.AppendLine("cases were reviewed, so \"about 70%\" here means \"on this test set\", not a validated probability.");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Generated by `dotnet SanctionAndForgive.Cli.dll evaluate` on {DateTime.UtcNow:yyyy-MM-dd}, matcher `{matcherVersion}`, seed {seed}.");
        md.AppendLine(CultureInfo.InvariantCulture, $"Lists: {string.Join(", ", lists.Select(list => $"{list.Code} ({list.Subjects:N0})"))}. Up to {SamplePerSource} subjects sampled per list.");
        md.AppendLine();
        md.AppendLine("A query is **recalled** when the right subject is in its list's cell at Review or above (best or among the");
        md.AppendLine("other hits); **top** when it is the best candidate.");
        md.AppendLine();
        md.AppendLine("## Perturbed names");
        md.AppendLine();
        md.AppendLine("| Perturbation | Queries | Recalled (≥ Review) | Match (≥ 90%) | Top |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var group in positives.GroupBy(trial => trial.Kind).OrderBy(group => Array.IndexOf(Perturbations, group.Key)))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count()} | {Pct(group.Count(Recalled), group.Count())} | {Pct(group.Count(trial => trial.FoundPercent >= 90), group.Count())} | {Pct(group.Count(trial => trial.Top), group.Count())} |");
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"| **All** | {positives.Count} | **{Pct(positives.Count(Recalled), positives.Count)}** | {Pct(positives.Count(trial => trial.FoundPercent >= 90), positives.Count)} | {Pct(positives.Count(trial => trial.Top), positives.Count)} |");
        md.AppendLine();
        md.AppendLine("| List | Queries | Recalled |");
        md.AppendLine("|---|---|---|");
        foreach (var group in positives.GroupBy(trial => trial.List).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count()} | {Pct(group.Count(Recalled), group.Count())} |");
        }

        md.AppendLine();
        md.AppendLine("## Cyrillic aliases against Latin-only lists");
        md.AppendLine();
        md.AppendLine("Subjects listed with both Cyrillic and Latin names. The query is a Cyrillic alias; the index has the same list");
        md.AppendLine("with every Cyrillic name removed, so only transliteration can connect them. This is real transliteration data.");
        md.AppendLine();
        md.AppendLine("| List | Queries | Recalled (≥ Review) | Match | Top |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var group in cyrillic.GroupBy(trial => trial.List).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count()} | {Pct(group.Count(Recalled), group.Count())} | {Pct(group.Count(trial => trial.FoundPercent >= 90), group.Count())} | {Pct(group.Count(trial => trial.Top), group.Count())} |");
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"| **All** | {cyrillic.Count} | **{Pct(cyrillic.Count(Recalled), cyrillic.Count)}** | {Pct(cyrillic.Count(trial => trial.FoundPercent >= 90), cyrillic.Count)} | {Pct(cyrillic.Count(trial => trial.Top), cyrillic.Count)} |");
        md.AppendLine();
        md.AppendLine("## Made-up names (false positives)");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"{negatives.Count} names against every list at once. The best cell across all lists counts.");
        md.AppendLine();
        md.AppendLine("| Kind | Names | Review or above | Match |");
        md.AppendLine("|---|---|---|---|");
        foreach (var group in negatives.GroupBy(negative => negative.Kind))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count()} | {Pct(group.Count(negative => negative.Percent >= 50), group.Count())} | {Pct(group.Count(negative => negative.Percent >= 90), group.Count())} |");
        }

        md.AppendLine();
        md.AppendLine("The highest-scoring made-up names, to see what the false positives look like:");
        md.AppendLine();
        md.AppendLine("| Query | Best listed match | % |");
        md.AppendLine("|---|---|---|");
        foreach (var negative in negatives.OrderByDescending(negative => negative.Percent).Take(12))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {negative.Query} | {negative.Hit} | {negative.Percent} |");
        }

        md.AppendLine();
        md.AppendLine("## Calibration");
        md.AppendLine();
        md.AppendLine("Each query's best candidate, by its percentage: how often it was the right subject. Positives count as right when");
        md.AppendLine("the best candidate is the perturbed subject; every made-up name's best candidate is wrong. The mix of positives");
        md.AppendLine("and negatives is arbitrary, so the absolute rates depend on it: read the trend, not the exact figure.");
        md.AppendLine();
        md.AppendLine("| Percent | Best candidates | Right | Share right |");
        md.AppendLine("|---|---|---|---|");
        var points = positives.Concat(cyrillic).Select(trial => (trial.BestPercent, trial.Top))
            .Concat(negatives.Select(negative => (negative.Percent, false))).ToList();
        foreach (var (low, high) in new[] { (0, 19), (20, 49), (50, 59), (60, 69), (70, 79), (80, 89), (90, 94), (95, 99) })
        {
            var inBand = points.Where(point => point.Item1 >= low && point.Item1 <= high).ToList();
            md.AppendLine(CultureInfo.InvariantCulture, $"| {low}-{high} | {inBand.Count} | {inBand.Count(point => point.Item2)} | {Pct(inBand.Count(point => point.Item2), inBand.Count)} |");
        }

        md.AppendLine();
        md.AppendLine("Queries the matcher missed, a sample:");
        md.AppendLine();
        md.AppendLine("| Perturbation | Query | Listed name | Its % |");
        md.AppendLine("|---|---|---|---|");
        foreach (var miss in positives.Where(trial => !Recalled(trial)).Take(15).Concat(cyrillic.Where(trial => !Recalled(trial)).Take(15)))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {miss.Kind} | {miss.Query} | {miss.Expected} | {miss.FoundPercent?.ToString(CultureInfo.InvariantCulture) ?? "not found"} |");
        }

        return md.ToString();
    }

    private static string[] Words(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsLatin(string value) => value.Any(char.IsLetter) && value.All(c => !char.IsLetter(c) || c < 0x250);

    private static bool HasCyrillic(string value) => value.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    private static string StripDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        return new string([.. decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)]).Normalize(NormalizationForm.FormC);
    }

    /// <param name="BestPercent">The cell's best candidate's percentage, whoever it is.</param>
    /// <param name="FoundPercent">The expected subject's percentage, if it was a hit at all.</param>
    private sealed record Trial(string List, string Kind, string Query, string Expected, int BestPercent, int? FoundPercent, bool Top);
}
