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

    [Theory]
    [InlineData("Гончар Gónchar")]
    [InlineData("Gónchar Гончар")]
    public void WordOrder_NeverChangesTheResult(string query)
    {
        AssertBand(NameCell(query, Subject("Gonchar Honchar", SubjectKind.Individual, null)), Outcome.Match);
    }

    [Fact]
    public void CorroboratingAddress_NeverLiftsAboveACap()
    {
        var subject = Subject("AB Incorporated", SubjectKind.Entity, "Tallinn, EE");
        var cell = Screener.Screen(new ScreeningRequest(null, "AB Corp", "Tallinn, EE"), [List("eu", subject)]).Entries[0].Cells.Single();

        Assert.Equal(AddressState.Corroborated, cell.Best!.AddressState);
        Assert.Equal(MatchingOptions.Default.ThinCoreCap, cell.Percent);
    }

    [Theory]
    [InlineData("г-н Иван Иванов", Outcome.Match)]
    [InlineData("Г-жа Анна Петрова", Outcome.Match)]
    [InlineData("Иванов Г.Н.", Outcome.NoMatch)]
    public void HyphenatedHonorific_IsNotInitials(string query, Outcome expected)
    {
        var listed = query.Contains("Анна", StringComparison.Ordinal) ? "Anna Petrova" : "Ivan Ivanov";
        AssertBand(NameCell(query, Subject(listed, SubjectKind.Individual, null)), expected);
    }

    [Fact]
    public void ContainedName_NeedsMoreThanApproximateWords()
    {
        var subject = Subject("Aviatsionnyi Kompleks im S V Ilyushina", SubjectKind.Entity, null);
        var cell = Screener.Screen(new ScreeningRequest(null, "ООО Авиационные Системы и Комплексы", null), [List("eu", subject)]).Entries[0].Cells.Single();

        AssertBand(cell, Outcome.NoMatch);
    }

    [Theory]
    [InlineData("NESTEROV O.Y.", "Oleg Yuryevich NESTEROV")]
    [InlineData("TATRIEV K.K.", "Khasan Kureyshevich TATRIEV")]
    public void LatinInitials_MatchTheNamesOwnFirstLetter(string query, string listed)
    {
        AssertBand(NameCell(query, Subject(listed, SubjectKind.Individual, null)), Outcome.Review);
    }

    [Theory]
    [InlineData("Ibex Shippig Inc.", "Ibex Shipping Inc.")]
    [InlineData("Expert Partnes", "Expert Partners")]
    public void TypoInALongGenericWord_IsStillThatWord(string query, string listed)
    {
        var subject = Subject(listed, SubjectKind.Entity, null);
        var cell = Screener.Screen(new ScreeningRequest(null, query, null), [List("eu", subject)]).Entries[0].Cells.Single();

        AssertBand(cell, Outcome.Match);
    }
}