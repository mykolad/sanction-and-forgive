using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Tests.Matching;

// The spec's golden tests (section 5.10), one row each. Names are fictional fixtures. Exact percentages aren't
// pinned (calibration changes them); each must sit in its outcome's band.
public class GoldenTests
{
    private static readonly Screener Screener = new(MatchingOptions.Default);

    public static TheoryData<string, string, Outcome> LegalFormsAndShortNames => new()
    {
        { "AB Inc.", "AB Incorporated", Outcome.Match },
        { "AB INC", "AB Incorporated", Outcome.Match },
        { "ab incorporated", "AB Incorporated", Outcome.Match },
        { "A.B. Incorporated", "AB Incorporated", Outcome.Match },
        { "A B Inc", "AB Incorporated", Outcome.Match },
        { "A Incorporated", "AB Incorporated", Outcome.NoMatch },
        { "ABC Incorporated", "AB Incorporated", Outcome.NoMatch },
        { "BA Incorporated", "AB Incorporated", Outcome.NoMatch },
        { "Incorporated", "AB Incorporated", Outcome.NoMatch },
        { "Inc.", "AB Incorporated", Outcome.NoMatch },
        { "Inc. Ltd", "AB Incorporated", Outcome.NoMatch },
        { "AB", "AB Incorporated", Outcome.Review },
        { "AB Corp", "AB Incorporated", Outcome.Review },
        { "AB Trading Inc", "AB Incorporated", Outcome.Review },
        { "AB Volvo Trading", "Volvo Trading AB", Outcome.Match },
    };

    public static TheoryData<string, string, SubjectKind, Outcome> FuzzyAndWordOrder => new()
    {
        { "Nortwind Shipping L.L.C.", "Northwind Shipping LLC", SubjectKind.Entity, Outcome.Match },
        { "Southwind Shipping LLC", "Northwind Shipping LLC", SubjectKind.Entity, Outcome.NoMatch },
        { "Bank of Ireland", "Bank of Iran", SubjectKind.Entity, Outcome.NoMatch },
        { "Ivanov Ivan", "Ivan Ivanov", SubjectKind.Individual, Outcome.Match },
        { "Ivan Petrovich Ivanov", "Ivan Ivanov", SubjectKind.Individual, Outcome.Review },
        { "Muhammad Al Rashid", "Mohammed Al Rashid", SubjectKind.Individual, Outcome.Match },
        { "Anna Petrova", "Anna Petrov", SubjectKind.Individual, Outcome.Review },
        { "Alpha Beta Gamma Delta Sigma Omega Kappa Zeta", "Alpha Beta Gamma Delta Sigma Omega Kappa Theta", SubjectKind.Entity, Outcome.Review },
    };

    public static TheoryData<string, string, Outcome> TransliterationCompanies => new()
    {
        { "ООО Ромашка", "Romashka LLC", Outcome.Match },
        { "ООО «Ромашка»", "Romashka LLC", Outcome.Match },
        { "ТОВ Ромашка", "Romashka LLC", Outcome.Match },
        { "Общество с ограниченной ответственностью Ромашка", "Romashka LLC", Outcome.Match },
        { "Товариство з обмеженою відповідальністю «Ромашка»", "Romashka LLC", Outcome.Match },
        { "Romashka LLC", "ООО Ромашка", Outcome.Match },
        { "OOО Ромашка", "Romashka LLC", Outcome.Match },
        { "АВ Incorporated", "AB Incorporated", Outcome.Match },
        { "Волга Трейд", "Volga Trade", Outcome.Match },
        { "Волга Трейд", "Volta Trade", Outcome.Review },
        { "Ромашка", "Romanko", Outcome.NoMatch },
    };

    public static TheoryData<string, string, Outcome> TransliterationPeople => new()
    {
        { "Иван Иванов", "Ivan Ivanov", Outcome.Match },
        { "Іван Іванов", "Ivan Ivanov", Outcome.Match },
        { "Ivan Ivanov", "Иван Иванов", Outcome.Match },
        { "Григорий Шевченко", "Grigoriy Shevchenko", Outcome.Match },
        { "Григорій Шевченко", "Hryhorii Shevchenko", Outcome.Match },
        { "Григорій Шевченко", "Grigoriy Shevchenko", Outcome.Match },
        { "Хмельницкий", "Khmelnitsky", Outcome.Match },
        { "Хмельницький", "Khmelnytskyi", Outcome.Match },
        { "Хмельницький", "Khmelnitsky", Outcome.Match },
        { "Юрий Щербаков", "Yuriy Shcherbakov", Outcome.Match },
        { "Юрий Щербаков", "Iurii Shcherbakov", Outcome.Match },
        { "Гончар", "Honchar", Outcome.Match },
        { "Гончар", "Gonchar", Outcome.Match },
        { "Мар'яна Коваль", "Maryana Koval", Outcome.Match },
        { "Мар’яна Коваль", "Mariana Koval", Outcome.Match },
        { "Ковалёв", "Kovalev", Outcome.Match },
        { "Ковалёв", "Kovaleff", Outcome.Match },
        { "Hryhorii Shevchenko", "Grigoriy Shevchenko", Outcome.Review },
        { "Микола Петренко", "Nikolay Petrenko", Outcome.Review },
        { "Сергій Іваненко", "Sergey Ivanenko", Outcome.Review },
        { "Савченко", "Shevchenko", Outcome.NoMatch },
        { "Иван Петров", "Ivan Ivanov", Outcome.NoMatch },
        { "Роман Ковальчук", "Roman Kovalenko", Outcome.NoMatch },
        { "Шевченко", "Taras Shevchenko", Outcome.NoMatch },
    };

    public static TheoryData<string, string, Outcome> Initials => new()
    {
        { "Шевченко Т.Г.", "Taras Shevchenko", Outcome.Review },
        { "Шевченко Т. Г.", "Taras Hryhorovych Shevchenko", Outcome.Review },
        { "Т.Г. Шевченко", "Taras Shevchenko", Outcome.Review },
        { "Shevchenko T.", "Taras Shevchenko", Outcome.Review },
        { "Taras Shevchenko", "Шевченко Т.Г.", Outcome.Review },
        { "Щербаков Ю.О.", "Iurii Shcherbakov", Outcome.Review },
        { "Гончар Г.", "Hryhorii Honchar", Outcome.Review },
        { "Гончар Г.", "Grigoriy Gonchar", Outcome.Review },
        { "Петренко М.И.", "Nikolay Petrenko", Outcome.Review },
        { "Шевченко А.О.", "Andrii Shevchenko", Outcome.Review },
        { "Иванов И.П.", "Ivan Ivanov", Outcome.Review },
        { "Шевченко А.Б.", "Taras Shevchenko", Outcome.NoMatch },
    };

    public static TheoryData<string?, string?, Outcome, AddressState> Addresses => new()
    {
        { null, "Tallinn, EE", Outcome.Match, AddressState.NotCompared },
        { "Tallinn, EE", null, Outcome.Match, AddressState.NotCompared },
        { null, null, Outcome.Match, AddressState.NotCompared },
        { "Tallinn, Estonia", "EE", Outcome.Match, AddressState.Corroborated },
        { "Tallinn", "Tallinn, EE", Outcome.Match, AddressState.Corroborated },
        { "Estonia", "Tallinn, EE", Outcome.Match, AddressState.Corroborated },
        { "Eesti, Tallinn", "Tallinn, EE", Outcome.Match, AddressState.Corroborated },
        { "Deliver in Tallinn", "Tallinn, EE", Outcome.Match, AddressState.Corroborated },
        { "Москва, Россия", "Moscow, RU", Outcome.Match, AddressState.Corroborated },
        { "Київ, Україна", "Kiev, UA", Outcome.Match, AddressState.Corroborated },
        { "РФ", "Russian Federation", Outcome.Match, AddressState.Corroborated },
        { "ул. Ленина 5, Москва", "Lenin St. 5, Moscow, Russia", Outcome.Match, AddressState.Corroborated },
        { "ул. Ленина 5, Москва", "Tverskaya 1, Moscow, RU", Outcome.Match, AddressState.Corroborated },
        { "Some Road 5", "Tallinn, EE", Outcome.Match, AddressState.NotCompared },
        { "Riga, LV", "Tallinn, EE", Outcome.Review, AddressState.Conflicting },
        { "Riga", "Tallinn, EE", Outcome.Review, AddressState.Conflicting },
        { "Riga, Latvia", "Tallinn, Estonia", Outcome.Review, AddressState.Conflicting },
    };

    [Theory]
    [MemberData(nameof(LegalFormsAndShortNames))]
    public void LegalFormsAndShortNames_(string query, string listed, Outcome expected) =>
        AssertName(query, listed, SubjectKind.Entity, expected);

    [Theory]
    [MemberData(nameof(FuzzyAndWordOrder))]
    public void FuzzyAndWordOrder_(string query, string listed, SubjectKind kind, Outcome expected) =>
        AssertName(query, listed, kind, expected);

    [Theory]
    [MemberData(nameof(TransliterationCompanies))]
    public void TransliterationCompanies_(string query, string listed, Outcome expected) =>
        AssertName(query, listed, SubjectKind.Entity, expected);

    [Theory]
    [MemberData(nameof(TransliterationPeople))]
    public void TransliterationPeople_(string query, string listed, Outcome expected) =>
        AssertName(query, listed, SubjectKind.Individual, expected);

    [Theory]
    [MemberData(nameof(Initials))]
    public void Initials_(string query, string listed, Outcome expected) =>
        AssertName(query, listed, SubjectKind.Individual, expected);

    [Theory]
    [MemberData(nameof(Addresses))]
    public void Addresses_(string? enteredAddress, string? listedAddress, Outcome expected, AddressState expectedState)
    {
        var subject = Subject("Northwind Shipping LLC", SubjectKind.Entity, listedAddress);
        var result = Screener.Screen(new ScreeningRequest(null, "Northwind Shipping LLC", enteredAddress), [List("eu", subject)]);

        var cell = Assert.Single(result.Entries.Single(entry => entry.Kind == EntryKind.Company).Cells);
        AssertBand(cell, expected);
        Assert.Equal(expectedState, cell.Best!.AddressState);
    }

    [Theory]
    [InlineData("ул. Ленина 5, Москва", "Lenin St. 5, Moscow, Russia", Outcome.Review)]
    [InlineData("Lenina, Moscow", "Lenin St. 5, Moscow, Russia", Outcome.NoMatch)]
    public void AddressOnly(string enteredAddress, string listedAddress, Outcome expected)
    {
        var subject = Subject("Northwind Shipping LLC", SubjectKind.Entity, listedAddress);
        var result = Screener.Screen(new ScreeningRequest(null, "Completely Unrelated Holding", enteredAddress), [List("eu", subject)]);

        var cell = Assert.Single(result.Entries.Single(entry => entry.Kind == EntryKind.Address).Cells);
        AssertBand(cell, expected);
    }

    private static void AssertName(string query, string listed, SubjectKind kind, Outcome expected)
    {
        var result = Screener.Screen(new ScreeningRequest(null, query, null), [List("eu", Subject(listed, kind, null))]);
        var cell = Assert.Single(Assert.Single(result.Entries).Cells);
        AssertBand(cell, expected);
    }

    internal static void AssertBand(Cell cell, Outcome expected)
    {
        Assert.True(cell.Checked);
        var explanation = cell.Best is null ? "no candidate" : string.Join("; ", cell.Best.Alignment);
        Assert.True(expected == cell.Outcome, $"Expected {expected}, got {cell.Outcome} at {cell.Percent}%: {explanation}");
        var (low, high) = expected switch
        {
            Outcome.Match => (90, 99),
            Outcome.Review => (50, 89),
            _ => (0, 49),
        };
        Assert.InRange(cell.Percent!.Value, low, high);
    }

    internal static ListedSubject Subject(string name, SubjectKind kind, string? address) =>
        new($"X-{name.GetHashCode(StringComparison.Ordinal):x}", kind, name, [new ListedName(name, IsWeak: false)],
            address is null ? [] : [new ListedAddress(address, CountryCode: null)], "TEST", "https://example.invalid/list");

    internal static ScreenedList List(string code, params ListedSubject[] subjects) =>
        new(code, code.ToUpperInvariant(), DateTimeOffset.Parse("2026-11-06T03:30:00Z", System.Globalization.CultureInfo.InvariantCulture), ListIndex.Build(subjects));
}
