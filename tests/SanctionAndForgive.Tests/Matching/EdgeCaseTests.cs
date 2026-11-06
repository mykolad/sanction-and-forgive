using System.Globalization;
using SanctionAndForgive.Matching;
using static SanctionAndForgive.Tests.Matching.GoldenTests;

namespace SanctionAndForgive.Tests.Matching;

// Edge cases beyond the spec's golden rows, each once a wrong or fail-open result.
public class EdgeCaseTests
{
    private static readonly Screener Screener = new(MatchingOptions.Default);

    private static Cell NameCell(string query, ListedSubject subject) =>
        Screener.Screen(new ScreeningRequest(query, null, null), [List("eu", subject)]).Entries.Single().Cells.Single();

    private static Cell AddressCell(string address, ListedSubject subject) =>
        Screener.Screen(new ScreeningRequest(null, null, address), [List("eu", subject)]).Entries.Single().Cells.Single();

    [Fact]
    public void PersonNamedLikeALegalForm_KeepsThatName()
    {
        var patLee = Subject("Pat Lee", SubjectKind.Individual, null);

        AssertBand(NameCell("Lee", patLee), Outcome.NoMatch);
        AssertBand(NameCell("Pat", Subject("Pat", SubjectKind.Individual, null)), Outcome.Match);
    }

    [Fact]
    public void SoleTraderMarker_IsDroppedFromAPersonsName()
    {
        AssertBand(NameCell("ФОП Шевченко Т.Г.", Subject("Taras Shevchenko", SubjectKind.Individual, null)), Outcome.Review);
    }

    [Fact]
    public void ShortSurname_ContradictingInitials_AreNotIgnored()
    {
        AssertBand(NameCell("Li A.", Subject("Li B.", SubjectKind.Individual, null)), Outcome.NoMatch);
    }

    [Fact]
    public void ShortSurname_SameInitials_StayBelowMatch()
    {
        AssertBand(NameCell("Li A.", Subject("Li A.", SubjectKind.Individual, null)), Outcome.Review);
    }

    [Theory]
    [InlineData("Taras Shevchenko H.", "T. H. Shevchenko")]
    [InlineData("T. H. Shevchenko", "Taras Shevchenko H.")]
    public void InitialsOnBothSides_SameResultWhicheverIsListed(string query, string listed)
    {
        AssertBand(NameCell(query, Subject(listed, SubjectKind.Individual, null)), Outcome.Review);
    }

    [Fact]
    public void ShortTransposition_IsFoundAndAligned()
    {
        var subject = Subject("Matra LLC", SubjectKind.Entity, null);
        var cell = Screener.Screen(new ScreeningRequest(null, "Marta LLC", null), [List("eu", subject)]).Entries.Single().Cells.Single();

        AssertBand(cell, Outcome.Review);
    }

    [Theory]
    [InlineData("Marta 5 10", "Matra 5 10")]
    [InlineData("Li 5 10", "Li 5 10")]
    public void AddressOnly_FindsFuzzyAndShortWords(string entered, string listed)
    {
        AssertBand(AddressCell(entered, Subject("Northwind Shipping LLC", SubjectKind.Entity, listed)), Outcome.Review);
    }

    [Fact]
    public void NoListsAtAll_IsIncomplete()
    {
        Assert.Equal(OverallOutcome.Incomplete, Screener.Screen(new ScreeningRequest("Ivan Ivanov", null, null), []).Outcome);
    }

    [Fact]
    public void MatcherVersion_DoesNotDependOnCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var english = new Screener(MatchingOptions.Default).MatcherVersion;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var german = new Screener(MatchingOptions.Default).MatcherVersion;

            Assert.Equal(english, german);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
