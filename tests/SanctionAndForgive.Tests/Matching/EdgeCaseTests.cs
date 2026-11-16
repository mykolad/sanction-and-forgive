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

    private static AddressState StateOf(string entered, string listed)
    {
        var subject = Subject("Northwind Shipping LLC", SubjectKind.Entity, listed);
        var result = Screener.Screen(new ScreeningRequest(null, "Northwind Shipping LLC", entered), [List("eu", subject)]);
        return result.Entries.First(entry => entry.Kind == EntryKind.Company).Cells.Single().Best!.AddressState;
    }

    [Theory]
    [InlineData("12 rue de Malte, Paris", "Rue de Malte 12, Paris, FR")]
    [InlineData("3 avenue de la Grèce, Lyon, France", "Lyon, FR")]
    [InlineData("12 rue de la République démocratique du Congo, Paris", "Paris, FR")]
    [InlineData("4 allee de Malte, Paris", "Paris, FR")]
    [InlineData("Lenina 5, Moscou, RUSSIE", "Moscow, RU")]
    [InlineData("Sheikh Zayed Road, UAE", "Dubai, AE")]
    [InlineData("Office 4, Some Road, Malta", "Valletta, MT")]
    public void AStreetNamedAfterACountry_IsAStreet_ButACountryNamedAfterAStreetIsStillTheCountry(string entered, string listed)
    {
        // French streets are often named after countries: "rue de Malte" in Paris is in France. A country name counts as
        // the street only after a street type and a particle, so "Road, UAE" still names the UAE (commas are gone).
        Assert.Equal(AddressState.Corroborated, StateOf(entered, listed));
    }

    [Fact]
    public void FrenchStreets_DontCorroborateOnTheirParticles()
    {
        // "de" and "la" are filler, like "of" and "the": two different streets in the same city don't agree on them.
        Assert.NotEqual(AddressState.Corroborated, StateOf("5 rue de la Paix", "12 rue de la Gare"));
        Assert.NotEqual(AddressState.Corroborated, StateOf("2 place Vendome", "9 place Bellecour"));
    }

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
    [InlineData("TATRIEV K.H.", "Khasan Kureyshevich TATRIEV")]
    [InlineData("TATRIEV K.H.", "Kureyshevich Khasan TATRIEV")]
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

    [Fact]
    public void NearGenericWord_StaysDistinctive_WhenNothingElseIs()
    {
        var subject = Subject("Expert Partners", SubjectKind.Entity, null);
        var cell = Screener.Screen(new ScreeningRequest(null, "Experts Partners", null), [List("eu", subject)]).Entries[0].Cells.Single();

        AssertBand(cell, Outcome.Review);
    }
}