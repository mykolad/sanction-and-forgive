using SanctionAndForgive.Matching;
using static SanctionAndForgive.Tests.Matching.GoldenTests;

namespace SanctionAndForgive.Tests.Matching;

// How a name in a script with no transliteration table (anything but Latin and Cyrillic) behaves today (#90), measured
// on 2026-11-16: the same spelling matches in every script; Arabic vowel marks, tatweel and letter variants, Greek
// accents, Chinese and Korean spacing or partial names, traditional against simplified characters, and any other script
// against its Latin spelling don't. The locale issues (#91-#93) change these rows on purpose; until then they record
// what a user gets.
public class ScriptTests
{
    private static readonly Screener Screener = new(MatchingOptions.Default);

    public static TheoryData<string, string, SubjectKind, Outcome> SameSpelling => new()
    {
        { "محمد عبد الله", "محمد عبد الله", SubjectKind.Individual, Outcome.Match },
        { "中兴通讯股份有限公司", "中兴通讯股份有限公司", SubjectKind.Entity, Outcome.Match },
        { "김정은", "김정은", SubjectKind.Individual, Outcome.Match },
        { "Εργατικό Κόμμα του Κουρδιστάν", "Εργατικό Κόμμα του Κουρδιστάν", SubjectKind.Entity, Outcome.Match },
        { "משה כהן", "משה כהן", SubjectKind.Individual, Outcome.Match },
        { "ソコロフ・イワン", "ソコロフ・イワン", SubjectKind.Individual, Outcome.Match },
        { "გიორგი ბერიძე", "გიორგი ბერიძე", SubjectKind.Individual, Outcome.Match },
        { "Արամ Պետրոսյան", "Արամ Պետրոսյան", SubjectKind.Individual, Outcome.Match },
        { "สมชาย ใจดี", "สมชาย ใจดี", SubjectKind.Individual, Outcome.Match },

        // NFKC already folds the micro sign the EU's Greek names use for μ, and a space reads as the katakana dot.
        { "Εργατικό Κόμμα του Κουρδιστάν", "Εργατικό Κόµµα του Κουρδιστάν", SubjectKind.Entity, Outcome.Match },
        { "ソコロフ イワン", "ソコロフ・イワン", SubjectKind.Individual, Outcome.Match },
    };

    public static TheoryData<string, string, SubjectKind, Outcome> NotReadToday => new()
    {
        // Arabic script (#92): vowel marks, tatweel, alef with hamza, ta marbuta, Persian ya and kaf.
        { "مُحَمَّد عبد الله", "محمد عبد الله", SubjectKind.Individual, Outcome.NoMatch },
        { "محـمد عبد الله", "محمد عبد الله", SubjectKind.Individual, Outcome.NoMatch },
        { "احمد حسن", "أحمد حسن", SubjectKind.Individual, Outcome.NoMatch },
        { "حمزه علي", "حمزة علي", SubjectKind.Individual, Outcome.NoMatch },
        { "علی کریمی", "علي كريمي", SubjectKind.Individual, Outcome.NoMatch },

        // Chinese and Korean (#93): a name is one token, so a part of it, a space inside it, or traditional characters
        // for simplified ones find nothing.
        { "中兴通讯", "中兴通讯股份有限公司", SubjectKind.Entity, Outcome.NoMatch },
        { "中兴 通讯", "中兴通讯", SubjectKind.Entity, Outcome.NoMatch },
        { "中興通訊", "中兴通讯", SubjectKind.Entity, Outcome.NoMatch },
        { "김 정은", "김정은", SubjectKind.Individual, Outcome.NoMatch },

        // Greek: accents are stripped from Latin letters only.
        { "Εργατικο Κομμα του Κουρδισταν", "Εργατικό Κόμμα του Κουρδιστάν", SubjectKind.Entity, Outcome.NoMatch },

        // Latin input against a name listed only in another script: no Latin form exists to compare with.
        { "Muhammad Abdullah", "محمد عبد الله", SubjectKind.Individual, Outcome.NoMatch },
        { "Zhongxing Tongxun", "中兴通讯", SubjectKind.Entity, Outcome.NoMatch },
        { "Kim Jong Un", "김정은", SubjectKind.Individual, Outcome.NoMatch },
        { "Ergatiko Komma tou Kourdistan", "Εργατικό Κόμμα του Κουρδιστάν", SubjectKind.Entity, Outcome.NoMatch },
        { "Ivan Sokolov", "ソコロフ・イワン", SubjectKind.Individual, Outcome.NoMatch },
    };

    [Theory]
    [MemberData(nameof(SameSpelling))]
    public void SameSpelling_(string query, string listed, SubjectKind kind, Outcome expected) => AssertName(query, listed, kind, expected);

    [Theory]
    [MemberData(nameof(NotReadToday))]
    public void NotReadToday_(string query, string listed, SubjectKind kind, Outcome expected) => AssertName(query, listed, kind, expected);

    private static void AssertName(string query, string listed, SubjectKind kind, Outcome expected)
    {
        var result = Screener.Screen(new ScreeningRequest(null, query, null), [List("eu", Subject(listed, kind, null))]);
        AssertBand(Assert.Single(Assert.Single(result.Entries).Cells), expected);
    }
}
