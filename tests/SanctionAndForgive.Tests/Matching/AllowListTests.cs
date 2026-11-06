using SanctionAndForgive.Matching;
using static SanctionAndForgive.Tests.Matching.GoldenTests;

namespace SanctionAndForgive.Tests.Matching;

// The allow-list (owner's decision, 2026-11-06): an entered name that is an allow-listed name has its possible matches
// (Review) shown as allowed, and they don't raise the outcome. A Match is never suppressed.
public class AllowListTests
{
    private static readonly Screener Screener = new(MatchingOptions.Default);

    private static readonly ScreenedList Eu = List("eu", Subject("Romashka LLC", SubjectKind.Entity, "Moscow, RU"));
    private static readonly ScreenedList Ofac = List("ofac-sdn", Subject("AB Incorporated", SubjectKind.Entity, null));

    private static AllowList Allow(params string[] names) =>
        AllowList.Build(names.Select((name, i) => new AllowListEntry(i + 1, name, "Known legitimate company")), "v7");

    [Fact]
    public void APossibleMatch_OnAnAllowListedName_IsAllowed_AndTheOutcomeDoesntRise()
    {
        var without = Screener.Screen(new ScreeningRequest(null, "AB Corp", null), [Eu, Ofac]);
        var with = Screener.Screen(new ScreeningRequest(null, "AB Corp", null), [Eu, Ofac], Allow("AB Corp"));

        Assert.Equal(OverallOutcome.Review, without.Outcome);
        var entry = Assert.Single(with.Entries);
        Assert.Equal("AB Corp", entry.AllowedBy?.Name);
        var ofac = entry.Cells[1];
        Assert.Equal((Outcome.Review, true), (ofac.Outcome, ofac.Allowed));
        Assert.Equal(without.Entries[0].Cells[1].Percent, ofac.Percent);
        Assert.False(entry.Cells[0].Allowed);
        Assert.Equal(OverallOutcome.NoMatch, with.Outcome);
        Assert.Equal("v7", with.AllowListVersion);
    }

    [Fact]
    public void AMatch_IsNeverAllowed()
    {
        var result = Screener.Screen(new ScreeningRequest(null, "ТОВ Ромашка", null), [Eu, Ofac], Allow("ТОВ Ромашка"));

        var eu = result.Entries[0].Cells[0];
        Assert.Equal((Outcome.Match, false), (eu.Outcome, eu.Allowed));
        Assert.Equal(OverallOutcome.Match, result.Outcome);
    }

    [Theory]
    [InlineData("A.B. Corp.", true)]
    [InlineData("ab  CORP", true)]
    [InlineData("AB Corporation", false)]
    [InlineData("AB Corp Holding", false)]
    [InlineData("AB", false)]
    public void OnlyTheAllowListedNameItself_CaseAndPunctuationAside(string entered, bool allowed)
    {
        var result = Screener.Screen(new ScreeningRequest(null, entered, null), [Eu, Ofac], Allow("AB Corp"));

        Assert.Equal(allowed, result.Entries[0].AllowedBy is not null);
        Assert.DoesNotContain(result.Entries[0].Cells, cell => cell.Allowed && !allowed);
    }

    [Fact]
    public void TheAllowList_AppliesToReceiverAndCompany_NeverToTheAddress()
    {
        var result = Screener.Screen(new ScreeningRequest("AB Corp", null, "AB Corp"), [Eu, Ofac], Allow("AB Corp"));

        Assert.NotNull(result.Entries.Single(entry => entry.Kind == EntryKind.Receiver).AllowedBy);
        var address = result.Entries.Single(entry => entry.Kind == EntryKind.Address);
        Assert.Null(address.AllowedBy);
        Assert.DoesNotContain(address.Cells, cell => cell.Allowed);
    }

    [Fact]
    public void WithoutAnAllowList_NothingChanges()
    {
        var request = new ScreeningRequest(null, "AB Corp", null);

        var plain = Screener.Screen(request, [Eu, Ofac]);
        var empty = Screener.Screen(request, [Eu, Ofac], AllowList.Empty);

        Assert.Equal((plain.Outcome, "none"), (empty.Outcome, empty.AllowListVersion));
        Assert.DoesNotContain(empty.Entries.SelectMany(entry => entry.Cells), cell => cell.Allowed);
    }

    [Fact]
    public void Without_DropsOneEntry_AndSaysSoInItsVersion()
    {
        var list = Allow("AB Corp", "Northwind Freight");

        var without = list.Without(1);

        Assert.Equal((1, "v7-1"), (without.Count, without.Version));
        Assert.Null(without.Find("AB Corp"));
        Assert.NotNull(without.Find("Northwind Freight"));
    }

    [Fact]
    public void OfTwoEntriesForOneName_TheOlderOneIsKept_WhateverTheOrder()
    {
        var list = AllowList.Build([new AllowListEntry(9, "AB Corp", "second"), new AllowListEntry(3, "ab corp.", "first")], "v2");

        Assert.Equal((3, 1), (list.Find("AB CORP")!.Id, list.Count));
    }
}
