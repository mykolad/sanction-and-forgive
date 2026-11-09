using System.Globalization;

namespace SanctionAndForgive.Conformance.Feeds;

/// <summary>A listed party in the suite's fictional lists: names in Latin and (often) Cyrillic, aliases, an address.
/// Every name is made up from the parts below; any likeness to a real listing is chance.</summary>
public sealed record FixtureSubject(
    int Number,
    bool Person,
    string? Given,
    string Family,
    string? CyrillicGiven,
    string? CyrillicFamily,
    IReadOnlyList<string> Aliases,
    string? WeakAlias,
    FixtureAddress? Address,
    string Programme,
    DateOnly ListedOn)
{
    /// <summary>"Ivan Ivanov", or the company name with its legal form ("Volga Trade LLC").</summary>
    public string LatinName => Person ? $"{Given} {Family}" : Family;

    /// <summary>"Иван Иванов" or "ООО «Волга Трейд»", if the party has a Cyrillic name.</summary>
    public string? CyrillicName => CyrillicFamily is null ? null : Person ? $"{CyrillicGiven} {CyrillicFamily}" : CyrillicFamily;
}

public sealed record FixtureAddress(string Street, string City, string Country, string Iso, string Numeric);

/// <summary>A small deterministic generator (xorshift): the same lists on every machine and every run, independent of
/// the platform's own random number generator.</summary>
public sealed class Rng(ulong seed)
{
    private ulong state = seed == 0 ? 0x9E3779B97F4A7C15 : seed;

    public int Next(int maxExclusive)
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return (int)(state % (ulong)maxExclusive);
    }

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    public bool Chance(int percent) => Next(100) < percent;
}

/// <summary>The fictional parties every list draws from. Lists overlap, as real ones do: a party is often on several.</summary>
public static class Fixtures
{
    public const int Seed = 20261109;

    private static readonly (string Latin, string Cyrillic)[] Given =
    [
        ("Ivan", "Иван"), ("Oleksandr", "Олександр"), ("Dmitry", "Дмитрий"), ("Sergei", "Сергей"), ("Andriy", "Андрій"),
        ("Mykola", "Микола"), ("Anna", "Анна"), ("Olena", "Олена"), ("Natalia", "Наталья"), ("Yuri", "Юрий"),
        ("Viktor", "Виктор"), ("Pavel", "Павел"), ("Tetiana", "Тетяна"), ("Maksym", "Максим"), ("Igor", "Игорь"),
        ("Svetlana", "Светлана"), ("Alexei", "Алексей"), ("Roman", "Роман"), ("Hryhorii", "Григорій"), ("Elena", "Елена"),
    ];

    private static readonly (string Latin, string Cyrillic)[] Family =
    [
        ("Ivanov", "Иванов"), ("Petrenko", "Петренко"), ("Kovalenko", "Коваленко"), ("Sokolov", "Соколов"),
        ("Morozov", "Морозов"), ("Shevchuk", "Шевчук"), ("Bondarenko", "Бондаренко"), ("Volkov", "Волков"),
        ("Kuznetsov", "Кузнецов"), ("Lebedev", "Лебедев"), ("Melnyk", "Мельник"), ("Tkachenko", "Ткаченко"),
        ("Orlov", "Орлов"), ("Zaitsev", "Зайцев"), ("Polishchuk", "Поліщук"), ("Gusev", "Гусев"),
        ("Fedorov", "Федоров"), ("Romanenko", "Романенко"), ("Belov", "Белов"), ("Honchar", "Гончар"),
    ];

    // People without a Cyrillic name: romanised from other scripts, as the lists write them.
    private static readonly (string Given, string Family)[] Others =
    [
        ("Ahmed", "Hassan"), ("Mohammed", "Al-Rashid"), ("Li", "Wei"), ("Kim", "Chol Su"), ("Reza", "Tehrani"),
        ("Abdul", "Karimov"), ("Hamid", "Nouri"), ("Omar", "Haddad"), ("Jamal", "Saleh"), ("Chen", "Jianguo"),
    ];

    private static readonly (string Latin, string Cyrillic)[] CompanyWords =
    [
        ("Volga", "Волга"), ("Baltic", "Балтик"), ("Northwind", "Нордвинд"), ("Polar", "Полар"), ("Ural", "Урал"),
        ("Dnipro", "Дніпро"), ("Severny", "Северный"), ("Amur", "Амур"), ("Granit", "Гранит"), ("Vostok", "Восток"),
        ("Atlas", "Атлас"), ("Orion", "Орион"), ("Delta", "Дельта"), ("Kristall", "Кристалл"), ("Zenit", "Зенит"),
    ];

    private static readonly (string Latin, string Cyrillic)[] CompanyKinds =
    [
        ("Trade", "Трейд"), ("Shipping", "Шиппинг"), ("Logistics", "Логистикс"), ("Energy", "Энерджи"),
        ("Holding", "Холдинг"), ("Invest", "Инвест"), ("Metals", "Металс"), ("Engineering", "Инжиниринг"),
        ("Systems", "Системс"), ("Aero", "Аэро"), ("Petroleum", "Петролеум"), ("Group", "Груп"),
    ];

    private static readonly (string Latin, string Cyrillic)[] LegalForms =
    [
        ("LLC", "ООО"), ("JSC", "АО"), ("PJSC", "ПАО"), ("OOO", "ООО"), ("Ltd", "ООО"), ("Limited", "ООО"),
    ];

    private static readonly FixtureAddress[] Places =
    [
        new("Lenina 5", "Moscow", "Russia", "RU", "643"),
        new("Tverskaya 12", "Moscow", "Russian Federation", "RU", "643"),
        new("Nevsky Prospekt 28", "Saint Petersburg", "Russia", "RU", "643"),
        new("Pobediteley 7", "Minsk", "Belarus", "BY", "112"),
        new("Valiasr Street 220", "Tehran", "Iran", "IR", "364"),
        new("Sheikh Zayed Road 101", "Dubai", "United Arab Emirates", "AE", "784"),
        new("Nathan Road 88", "Hong Kong", "Hong Kong", "HK", "344"),
        new("Istiklal Caddesi 40", "Istanbul", "Turkey", "TR", "792"),
        new("Abay Avenue 15", "Almaty", "Kazakhstan", "KZ", "398"),
        new("Baghdad Street 3", "Damascus", "Syria", "SY", "760"),
        new("Sungri Street 9", "Pyongyang", "North Korea", "KP", "408"),
    ];

    private static readonly string[] Programmes = ["RUS", "BLR", "IRN", "SYR", "DPRK", "TERR", "CYBER", "UKR"];

    /// <summary>Every party, numbered from 1, the same on every run.</summary>
    public static IReadOnlyList<FixtureSubject> All { get; } = Generate(600);

    private static List<FixtureSubject> Generate(int count)
    {
        var rng = new Rng(Seed);
        var subjects = new List<FixtureSubject>(count);
        for (var number = 1; number <= count; number++)
        {
            var listedOn = new DateOnly(2014, 1, 1).AddDays(rng.Next(4300));
            var address = rng.Chance(70) ? rng.Pick(Places) : null;
            var programme = rng.Pick(Programmes);
            if (rng.Chance(55))
            {
                subjects.Add(Person(number, rng, address, programme, listedOn));
            }
            else
            {
                subjects.Add(Company(number, rng, address, programme, listedOn));
            }
        }

        return subjects;
    }

    private static FixtureSubject Person(int number, Rng rng, FixtureAddress? address, string programme, DateOnly listedOn)
    {
        if (rng.Chance(20))
        {
            var (otherGiven, otherFamily) = rng.Pick(Others);
            var weak = rng.Chance(40) ? $"Abu {otherGiven}" : null;
            return new FixtureSubject(number, Person: true, otherGiven, otherFamily, null, null, [], weak, address, programme, listedOn);
        }

        var first = rng.Pick(Given);
        var last = rng.Pick(Family);
        var aliases = new List<string>();
        if (rng.Chance(30))
        {
            // Another romanisation, as a second list might write it.
            aliases.Add($"{Respell(first.Latin)} {Respell(last.Latin)}");
        }

        var weakAlias = rng.Chance(25) ? first.Latin[..Math.Min(4, first.Latin.Length)] + "ka" : null;
        var cyrillic = rng.Chance(75);
        // Some lists write the family name in capitals ("Ivan IVANOV").
        var family = rng.Chance(30) ? last.Latin.ToUpperInvariant() : last.Latin;
        return new FixtureSubject(number, Person: true, first.Latin, family,
            cyrillic ? first.Cyrillic : null, cyrillic ? last.Cyrillic : null, aliases, weakAlias, address, programme, listedOn);
    }

    private static FixtureSubject Company(int number, Rng rng, FixtureAddress? address, string programme, DateOnly listedOn)
    {
        var word = rng.Pick(CompanyWords);
        var kind = rng.Pick(CompanyKinds);
        var form = rng.Pick(LegalForms);
        var latin = rng.Chance(15) ? $"Joint Stock Company {word.Latin} {kind.Latin}" : $"{word.Latin} {kind.Latin} {form.Latin}";
        var cyrillic = rng.Chance(60) ? $"{form.Cyrillic} «{word.Cyrillic} {kind.Cyrillic}»" : null;
        var aliases = new List<string>();
        if (rng.Chance(35))
        {
            aliases.Add($"{word.Latin.ToUpperInvariant()} {kind.Latin.ToUpperInvariant()}");
        }

        var weak = rng.Chance(20) ? word.Latin : null;
        return new FixtureSubject(number, Person: false, null, latin, null, cyrillic, aliases, weak, address, programme, listedOn);
    }

    // A second romanisation: the spellings different lists use for the same name.
    private static string Respell(string name) => name
        .Replace("y", "i", StringComparison.Ordinal).Replace("kh", "h", StringComparison.Ordinal)
        .Replace("ov", "off", StringComparison.Ordinal).Replace("ii", "iy", StringComparison.Ordinal);

    /// <summary>The parties on one list: a deterministic share of all of them (lists overlap), in number order.</summary>
    public static IReadOnlyList<FixtureSubject> For(string list, int count)
    {
        var rng = new Rng((ulong)Seed ^ (uint)list.GetHashCodeStable());
        var chosen = new SortedSet<int>();
        while (chosen.Count < count)
        {
            chosen.Add(rng.Next(All.Count));
        }

        return [.. chosen.Select(index => All[index])];
    }

    /// <summary>A hash that doesn't change between runs (string.GetHashCode does).</summary>
    public static int GetHashCodeStable(this string text)
    {
        unchecked
        {
            var hash = 23;
            foreach (var character in text)
            {
                hash = (hash * 31) + character;
            }

            return hash;
        }
    }

    public static string Date(DateOnly date, string format) => date.ToString(format, CultureInfo.InvariantCulture);
}
