using SanctionAndForgive.Matching;
using static SanctionAndForgive.Tests.Matching.GoldenTests;

namespace SanctionAndForgive.Tests.Matching;

// Section 5.10, per-list results: fixture lists EU and OFAC, plus UK, which is stale (its index withheld, as the
// API does for a list past its allowed age).
public class PerListTests
{
    private static readonly Screener Screener = new(MatchingOptions.Default);

    private static readonly ListedSubject Romashka = Subject("Romashka LLC", SubjectKind.Entity, "Moscow, RU");
    private static readonly ListedSubject AbIncorporated = Subject("AB Incorporated", SubjectKind.Entity, null);
    private static readonly ListedSubject Northwind = Subject("Northwind Shipping LLC", SubjectKind.Entity, "Tallinn, EE");
    private static readonly ListedSubject Shevchenko = Subject("Taras Shevchenko", SubjectKind.Individual, null);
    private static readonly ListedSubject Volga = Subject("Volga Trade", SubjectKind.Entity, null);

    private static readonly ScreenedList Eu = List("eu", Romashka, Northwind, Shevchenko);
    private static readonly ScreenedList Ofac = List("ofac-sdn", AbIncorporated, Northwind);
    private static readonly ScreenedList UkCurrent = List("uk", Volga);
    private static readonly ScreenedList UkStale = UkCurrent with { Index = null };

    [Fact]
    public void ListedOnEuOnly_EuMatch_OfacNoMatch_UkNotChecked_OverallMatch()
    {
        var result = Screener.Screen(new ScreeningRequest(null, "ТОВ Ромашка", null), [Eu, Ofac, UkStale]);

        var cells = Assert.Single(result.Entries).Cells;
        AssertBand(cells[0], Outcome.Match);
        AssertBand(cells[1], Outcome.NoMatch);
        AssertNotChecked(cells[2]);
        Assert.Equal(OverallOutcome.Match, result.Outcome);
    }

    [Fact]
    public void ListedOnOfacOnly_OfacReview_EuNoMatch_UkNotChecked_OverallReview()
    {
        var result = Screener.Screen(new ScreeningRequest(null, "AB Corp", null), [Eu, Ofac, UkStale]);

        var cells = Assert.Single(result.Entries).Cells;
        AssertBand(cells[1], Outcome.Review);
        AssertBand(cells[0], Outcome.NoMatch);
        AssertNotChecked(cells[2]);
        Assert.Equal(OverallOutcome.Review, result.Outcome);
    }

    [Fact]
    public void UnlistedName_CurrentListsNoMatch_StaleListIncomplete()
    {
        var request = new ScreeningRequest("Zephyrine Quillbrook", null, null);

        Assert.Equal(OverallOutcome.NoMatch, Screener.Screen(request, [Eu, Ofac, UkCurrent]).Outcome);
        Assert.Equal(OverallOutcome.Incomplete, Screener.Screen(request, [Eu, Ofac, UkStale]).Outcome);
    }

    [Fact]
    public void ReceiverAndCompany_OneEntryEach_ScreenedSeparately()
    {
        var result = Screener.Screen(new ScreeningRequest("Шевченко Т.Г.", "Volga Trade", null), [Eu, Ofac, UkCurrent]);

        Assert.Equal([EntryKind.Receiver, EntryKind.Company], result.Entries.Select(entry => entry.Kind));
        AssertBand(result.Entries[0].Cells[0], Outcome.Review);
        AssertBand(result.Entries[0].Cells[2], Outcome.NoMatch);
        AssertBand(result.Entries[1].Cells[2], Outcome.Match);
        AssertBand(result.Entries[1].Cells[0], Outcome.NoMatch);
    }

    [Fact]
    public void SamePartyOnEuAndOfac_HitInBothRows()
    {
        var result = Screener.Screen(new ScreeningRequest(null, "Northwind Shipping LLC", null), [Eu, Ofac, UkCurrent]);

        var cells = Assert.Single(result.Entries).Cells;
        AssertBand(cells[0], Outcome.Match);
        AssertBand(cells[1], Outcome.Match);
        Assert.Equal("Northwind Shipping LLC", cells[0].Best!.Subject.PrimaryName);
        Assert.Equal("Northwind Shipping LLC", cells[1].Best!.Subject.PrimaryName);
    }

    [Fact]
    public void AddressOnly_Accepted_OneAddressColumn()
    {
        var result = Screener.Screen(new ScreeningRequest(null, null, "Tallinn"), [Eu, Ofac, UkCurrent]);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(EntryKind.Address, entry.Kind);
        Assert.Equal(3, entry.Cells.Count);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "  ", "\t")]
    public void AllFieldsEmpty_IsRejected(string? receiver, string? company, string? address)
    {
        Assert.Throws<ArgumentException>(() => Screener.Screen(new ScreeningRequest(receiver, company, address), [Eu]));
    }

    [Fact]
    public void SameInputAndLists_SameResult()
    {
        var request = new ScreeningRequest("Шевченко Т.Г.", "ТОВ Ромашка", "Москва");

        var first = Screener.Screen(request, [Eu, Ofac, UkStale]);
        var second = new Screener(MatchingOptions.Default).Screen(request, [List("eu", Romashka, Northwind, Shevchenko), Ofac, UkStale]);

        Assert.Equal(first.MatcherVersion, second.MatcherVersion);
        Assert.Equal(
            first.Entries.SelectMany(entry => entry.Cells).Select(cell => (cell.List, cell.Percent, cell.Outcome, cell.Best?.Alias)),
            second.Entries.SelectMany(entry => entry.Cells).Select(cell => (cell.List, cell.Percent, cell.Outcome, cell.Best?.Alias)));
    }

    [Fact]
    public void ChangedOptions_ChangeTheMatcherVersion()
    {
        Assert.NotEqual(
            new Screener(MatchingOptions.Default).MatcherVersion,
            new Screener(MatchingOptions.Default with { GenericCap = 70 }).MatcherVersion);
    }

    private static void AssertNotChecked(Cell cell)
    {
        Assert.False(cell.Checked);
        Assert.Null(cell.Percent);
        Assert.Null(cell.Outcome);
        Assert.Null(cell.Best);
    }
}
