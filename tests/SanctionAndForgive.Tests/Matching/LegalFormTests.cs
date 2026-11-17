using SanctionAndForgive.Matching;
using static SanctionAndForgive.Tests.Matching.GoldenTests;

namespace SanctionAndForgive.Tests.Matching;

// Legal forms of the countries a European logistics company trades with (#94, Data/legal-forms.json): Polish, Czech
// and Slovak, Turkish, Baltic, Hungarian, Romanian, Bulgarian, Belarusian, Kazakh and the Gulf free zones, abbreviated
// or written out (the phrases), each stripped like LLC or GmbH. A form that is also a word or a name ("Ad", "BT") stays
// a name in Latin script.
public class LegalFormTests
{
    private static readonly Screener Screener = new(MatchingOptions.Default);

    public static TheoryData<string, string, Outcome> Rows => new()
    {
        { "Polimex Sp. z o.o.", "Polimex", Outcome.Match },
        { "Polimex spółka z ograniczoną odpowiedzialnością", "Polimex Sp. z o.o.", Outcome.Match },
        { "Polimex Sp. z o.o.", "Polimex Spolka z o.o.", Outcome.Match },
        { "Polimex S.A.", "Polimex Spółka Akcyjna", Outcome.Match },
        { "Polimex sp.k.", "Polimex Spółka Komandytowa", Outcome.Match },
        { "Polimex sp.j.", "Polimex", Outcome.Match },
        { "Strojimport s.r.o.", "Strojimport spol. s r.o.", Outcome.Match },
        { "Strojimport s.r.o.", "Strojimport", Outcome.Match },
        { "Anadolu Lojistik Ltd. Şti.", "Anadolu Lojistik Limited Şirketi", Outcome.Match },
        { "Anadolu Lojistik A.Ş.", "Anadolu Lojistik Anonim Şirketi", Outcome.Match },
        { "ANADOLU LOJİSTİK LTD. ŞTİ.", "Anadolu Lojistik Ltd.", Outcome.Match },
        { "ANADOLU LOJİSTİK LİMİTED ŞİRKETİ", "Anadolu Lojistik Ltd.", Outcome.Match },
        { "ANADOLU LOJİSTİK ANONİM ŞİRKETİ", "Anadolu Lojistik A.Ş.", Outcome.Match },
        { "Baltic Freight UAB", "UAB Baltic Freight", Outcome.Match },
        { "Riga Cargo SIA", "Riga Cargo", Outcome.Match },
        { "Duna Trans Kft.", "Duna Trans", Outcome.Match },
        { "Duna Trans Zrt.", "Duna Trans Nyrt.", Outcome.Match },
        { "Балкан Транс ООД", "Balkan Trans", Outcome.Match },
        { "Балкан Транс ЕООД", "Balkan Trans OOD", Outcome.Match },
        { "Balkan Trans EAD", "Балкан Транс", Outcome.Match },
        { "Мінск Транс ТАА", "Minsk Trans", Outcome.Match },
        { "Minsk Trans TAA", "Мінск Транс", Outcome.Match },
        { "Алатау Логистика ЖШС", "Alatau Logistika", Outcome.Match },
        { "Gulf Star FZE", "Gulf Star", Outcome.Match },
        { "Gulf Star FZCO", "Gulf Star FZ-LLC", Outcome.Match },
        { "Gulf Star DMCC", "Gulf Star", Outcome.Match },
        { "Shenzhen Hengda Trading Co., Ltd.", "Shenzhen Hengda Trading", Outcome.Match },

        // Forms that are also names stay names: no "Ad" or "BT" in Latin script, and "Sia" only beside a core word.
        { "Ad Astra Logistics", "Astra Logistics", Outcome.NoMatch },
        { "BT Freight", "Freight Systems", Outcome.NoMatch },
        { "Sia Trading", "Siam Trading", Outcome.NoMatch },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void LegalForms_(string query, string listed, Outcome expected)
    {
        var result = Screener.Screen(new ScreeningRequest(null, query, null), [List("eu", Subject(listed, SubjectKind.Entity, null))]);
        AssertBand(Assert.Single(Assert.Single(result.Entries).Cells), expected);
    }
}
