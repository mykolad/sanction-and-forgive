using SanctionAndForgive.Matching;
using static SanctionAndForgive.Tests.Matching.GoldenTests;

namespace SanctionAndForgive.Tests.Matching;

// Latin letters that aren't a base letter plus an accent (#94, Data/letters.json): each folds to what it reads as
// (ł l, ß ss, þ th, ð d, ħ h, Turkish ı and İ as i), and some have a second spelling their languages write (German
// ü ö ä as ue oe ae, Danish and Norwegian ø as oe and å as aa, Serbian and Croatian đ as dj), both ways round.
public class LatinLetterTests
{
    private static readonly Screener Screener = new(MatchingOptions.Default);

    public static TheoryData<string, string, SubjectKind, Outcome> Rows => new()
    {
        { "Lukasz Nowak", "Łukasz Nowak", SubjectKind.Individual, Outcome.Match },
        { "Jorgen Hansen", "Jørgen Hansen", SubjectKind.Individual, Outcome.Match },
        { "Joergen Hansen", "Jørgen Hansen", SubjectKind.Individual, Outcome.Match },
        { "Jørgen Hansen", "Joergen Hansen", SubjectKind.Individual, Outcome.Match },
        { "Nordstrasse Logistik GmbH", "Nordstraße Logistik GmbH", SubjectKind.Entity, Outcome.Match },
        { "Isik Yilmaz", "Işık Yılmaz", SubjectKind.Individual, Outcome.Match },
        { "Ismail Ozturk", "İsmail Öztürk", SubjectKind.Individual, Outcome.Match },
        { "Ismail Oeztuerk", "İsmail Öztürk", SubjectKind.Individual, Outcome.Match },
        { "Asa Lindstrom", "Åsa Lindström", SubjectKind.Individual, Outcome.Match },
        { "Aasa Lindstroem", "Åsa Lindström", SubjectKind.Individual, Outcome.Match },
        { "Thor Gudmundsson", "Þór Guðmundsson", SubjectKind.Individual, Outcome.Match },
        { "Djordje Petrovic", "Đorđe Petrović", SubjectKind.Individual, Outcome.Match },
        { "Dorde Petrovic", "Đorđe Petrović", SubjectKind.Individual, Outcome.Match },
        { "Hamrun Trading Ltd", "Ħamrun Trading Ltd", SubjectKind.Entity, Outcome.Match },
        { "Maersk Line AS", "Mærsk Line AS", SubjectKind.Entity, Outcome.Match },
        { "Oeuvre Logistique SARL", "Œuvre Logistique SARL", SubjectKind.Entity, Outcome.Match },
        { "Broendby Shipping ApS", "Brøndby Shipping ApS", SubjectKind.Entity, Outcome.Match },

        // A folded letter with an accent on it decomposes to the letter and its mark: ǿ is ø, ǽ is æ.
        { "Orn Hansen", "Ǿrn Hansen", SubjectKind.Individual, Outcome.Match },
        { "Aegir Hansen", "Ǽgir Hansen", SubjectKind.Individual, Outcome.Match },

        // A second spelling is a reading of the same letter, not a licence for any vowel pair.
        { "Jaergen Hansen", "Jørgen Hansen", SubjectKind.Individual, Outcome.Review },
        { "Lukasz Kowal", "Łukasz Nowak", SubjectKind.Individual, Outcome.NoMatch },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Letters_(string query, string listed, SubjectKind kind, Outcome expected)
    {
        var result = Screener.Screen(new ScreeningRequest(null, query, null), [List("eu", Subject(listed, kind, null))]);
        AssertBand(Assert.Single(Assert.Single(result.Entries).Cells), expected);
    }
}
