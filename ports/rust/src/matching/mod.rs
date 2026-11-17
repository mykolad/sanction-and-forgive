//! The Rust port of SanctionAndForgive.Matching: pure, deterministic, no I/O beyond its embedded dictionaries. Every
//! rule follows the .NET reference exactly (docs/conformance.md), except where improvements land here first (owner's
//! decision, 2026-11-17): Arabic-script names compared in their own script and Arabic name particles in Latin script
//! (data/arabic.json, #92), Chinese and Korean names (data/cjk.json, #93). The speed work
//! follows the Go port's (ports/go/README.md, "Speed and memory").

/// The matcher's maps and sets: FxHash, not the default SipHash (which resists hash flooding, at a cost every lookup
/// pays). Their keys come from the dictionaries and the lists, and the order of a map is never what a result depends on.
pub type HashMap<K, V> = std::collections::HashMap<K, V, rustc_hash::FxBuildHasher>;
pub type HashSet<T> = std::collections::HashSet<T, rustc_hash::FxBuildHasher>;

pub mod addresses;
pub mod compare;
pub mod data;
pub mod index;
pub mod names;
pub mod options;
pub mod scorer;
pub mod screener;
pub mod text;

pub use addresses::AddressState;
pub use index::{ListIndex, ListedAddress, ListedName, ListedSubject, SubjectKind, SubjectMatch};
pub use options::{Options, Outcome};
pub use scorer::WordAlignment;
pub use screener::{
    AllowList, AllowListEntry, Cell, Entry, EntryKind, ListedHit, OverallOutcome, ScreenedList, Screener, ScreeningRequest, ScreeningResult,
};

#[cfg(test)]
mod tests {
    use super::*;

    /// The reference's MatcherVersion for the shipped dictionaries and default options.
    #[test]
    fn matcher_version_is_the_references() {
        assert_eq!(Screener::new(Options::default()).matcher_version, "29cac07855fe");
    }

    /// The outcome and percentage of screening `query` (a company) against one subject named `listed`.
    fn screen(query: &str, listed: &str, kind: SubjectKind) -> (Option<Outcome>, Option<i32>) {
        let subject = ListedSubject {
            external_id: "X-1".to_string(),
            kind,
            primary_name: listed.to_string(),
            names: vec![ListedName { full_name: listed.to_string(), is_weak: false }],
            addresses: Vec::new(),
            programme: None,
            source_url: None,
        };
        let list = ScreenedList {
            code: "eu".to_string(),
            name: "EU".to_string(),
            index: Some(std::sync::Arc::new(ListIndex::build(vec![subject]))),
        };
        let request = ScreeningRequest { receiver_name: String::new(), company_name: query.to_string(), address: String::new() };
        let result = Screener::new(Options::default()).screen(&request, &[list], &AllowList::empty()).unwrap();
        let cell = &result.entries[0].cells[0];
        (cell.outcome, cell.percent)
    }

    /// Arabic-script names compared in their own script (data/arabic.json, #92): vowel marks, tatweel, hamza seats and
    /// alef forms, ta marbuta, alef maqsura, Persian ya and kaf, and Eastern digits don't keep a name from matching.
    #[test]
    fn arabic_script_spellings_that_differ_by_marks_or_letter_variants_match() {
        for (query, listed) in [
            ("مُحَمَّد عبد الله", "محمد عبد الله"),
            ("محـمد عبد الله", "محمد عبد الله"),
            ("احمد حسن", "أحمد حسن"),
            ("إبراهيم حسن", "ابراهيم حسن"),
            ("حمزه علي", "حمزة علي"),
            ("مصطفي علي", "مصطفى علي"),
            ("علی کریمی", "علي كريمي"),
        ] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
            assert_eq!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
        let (outcome, _) = screen("شركة النور ٢٠٠٠", "شركة النور 2000", SubjectKind::Entity);
        assert_eq!(outcome, Some(Outcome::Match));
        // A small mark above (U+0618) is dropped too.
        let (outcome, percent) = screen("عؘلي حسن", "علي حسن", SubjectKind::Individual);
        assert_eq!(outcome, Some(Outcome::Match), "{percent:?}");
    }

    /// A short name stays short however many marks or tatweels it's written with: the cap for a short name applies alike.
    #[test]
    fn marks_dont_lift_a_short_arabic_name_past_its_cap() {
        let plain = screen("نور", "نور", SubjectKind::Entity);
        assert_eq!(screen("نُور", "نور", SubjectKind::Entity), plain);
        assert_eq!(screen("نـور", "نور", SubjectKind::Entity), plain);
    }

    /// An Arabic initial is one however it's written: with a vowel mark or a tatweel it screens as without.
    #[test]
    fn an_arabic_initial_with_a_mark_or_tatweel_is_still_an_initial() {
        let plain = screen("ع حسين", "علي حسين", SubjectKind::Individual);
        assert_eq!(plain.0, Some(Outcome::Review), "{:?}", plain.1);
        assert_eq!(screen("عُ حسين", "علي حسين", SubjectKind::Individual), plain);
        assert_eq!(screen("عـ حسين", "علي حسين", SubjectKind::Individual), plain);
    }

    /// A stray tatweel between words is no word: the names still match.
    #[test]
    fn a_stray_tatweel_is_no_word() {
        let (outcome, percent) = screen("محمد ـ حسن", "محمد حسن", SubjectKind::Individual);
        assert_eq!(outcome, Some(Outcome::Match), "{percent:?}");
    }

    /// Arabic name particles in Latin script (#92): Abd and its article, the article written apart or joined, Abu, and
    /// Allah, in the ways the lists and users write them.
    #[test]
    fn arabic_name_particles_read_alike_however_theyre_written() {
        for (query, listed, expected) in [
            ("Mohammed Abdel Rahman", "Muhammad Abd al-Rahman", Outcome::Match),
            ("Mohamed Abdelrahman", "Muhammad Abd al-Rahman", Outcome::Match),
            ("Muhammad Abdulrahman", "Muhammad Abd al-Rahman", Outcome::Match),
            ("Muhammad Abdurrahman", "Muhammad Abd al-Rahman", Outcome::Match),
            ("Abd al Rahman Hassan", "Abdul Rahman Hasan", Outcome::Match),
            ("Khaled Abdel Aziz", "Khalid Abd al-Aziz", Outcome::Match),
            ("Khaled Abdulaziz", "Khalid Abd al-Aziz", Outcome::Match),
            ("Omar Abdul Qadir", "Umar Abd al-Qadir", Outcome::Match),
            ("Abdullah Hussein", "Abd Allah Husayn", Outcome::Match),
            ("Abubakr Albaghdadi", "Abu Bakr al-Baghdadi", Outcome::Match),
            ("Abu Bakr Baghdadi", "Abu Bakr al-Baghdadi", Outcome::Match),
            ("Hassan Nasrallah", "Hasan Nasr Allah", Outcome::Match),
            ("Hassan Nasrullah", "Hasan Nasr Allah", Outcome::Match),
            ("Ahmed El-Sayed", "Ahmad al-Sayed", Outcome::Match),
            ("Ahmed Elsayed", "Ahmad al-Sayed", Outcome::Match),
            ("Harun ar-Rashid", "Harun al-Rashid", Outcome::Match),
            ("Abu Abd Allah Hassan", "Abu Abdullah Hassan", Outcome::Match),
            ("Abdul Karim Hassan", "Abdul Kareem Hassan", Outcome::Match),
            ("Abdoul Rahman Hassan", "Abd al-Rahman Hassan", Outcome::Match),
            ("Abdoulrahman Hassan", "Abd al-Rahman Hassan", Outcome::Match),
            ("Abu Muhammad Hassan", "Abu Mohammed Hassan", Outcome::Match),
            ("Hassan Nasr Ellah", "Hassan Nasrellah", Outcome::Match),
            ("Abou Bakr Hassan", "Abu Bakr Hassan", Outcome::Match),
            ("Ahmed Ulsayed", "Ahmed al-Sayed", Outcome::Match),
            ("Harun Arrashid", "Harun ar-Rashid", Outcome::Match),
            ("Abu Bákr Hassan", "Abu Bakr Hassan", Outcome::Match),
            ("Абу Бакр Хасан", "Abu Bakr Hassan", Outcome::Match),
            ("Абд аль-Рахман Хасан", "Abd al-Rahman Hassan", Outcome::Match),
        ] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
            assert_eq!(outcome, Some(expected), "{query} / {listed}: {percent:?}");
        }
    }

    /// The particles are read only where they are particles: a name that merely starts like one keeps its own reading.
    #[test]
    fn words_that_only_look_like_particles_keep_their_meaning() {
        for (query, listed, kind) in [
            ("Alfred Smith", "Fred Smith", SubjectKind::Individual),
            ("Elmar Hansen", "Mar Hansen", SubjectKind::Individual),
            ("As Trading", "Trading", SubjectKind::Entity),
            ("Abdi Hassan", "Abd Hassan", SubjectKind::Individual),
            ("Abdul Hassan", "Hassan", SubjectKind::Individual),
            ("Attar Hassan", "Tar Hassan", SubjectKind::Individual),
            ("Ulrich Hansen", "Rich Hansen", SubjectKind::Individual),
        ] {
            let (outcome, percent) = screen(query, listed, kind);
            assert_ne!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
    }

    /// Abd is merged only before a name that follows it in Arabic names: "Abdul Karimov" is a given name and a surname,
    /// and still matches the list's family-name-first spelling.
    #[test]
    fn abd_before_a_surname_stays_a_given_name() {
        let (outcome, percent) = screen("Abdul Karimov", "Karimov Abdul", SubjectKind::Individual);
        assert_eq!(outcome, Some(Outcome::Match), "{percent:?}");
    }

    /// A country name names the street after a street type and a particle ("rue de Malte"), right after a street type
    /// with more of the address after it ("Via Germania 12"), or right before a street type written after the name
    /// ("Almanya Caddesi"); at the end after a street type it is still the country ("Some Road, Malta").
    #[test]
    fn a_street_named_after_a_country_is_a_street() {
        let data = data::data();
        let parse = |raw: &str| addresses::parse_address(data, &mut text::Tokenizer::new(data), raw, "");
        for (raw, country) in [
            ("12 rue de Malte, Paris", ""),
            ("Almanya Caddesi 12, Istanbul", ""),
            ("Via Germania 12, Roma", ""),
            ("Corso Italia 3, Milano", ""),
            ("12 Jordan Road, Kowloon", ""),
            ("Sheikh Zayed Road, UAE", "AE"),
            ("Office 4, Some Road, Malta", "MT"),
            ("Platz der Türkei 12, Berlin", ""),
            ("Ataturk Cad. 12, Istanbul", ""),
            ("Irak Mahallesi 5, Ankara", ""),
            ("Japonya Mah. 3, Ankara", ""),
            ("Mısır Çarşısı No: 4, Eminönü, İstanbul", ""),
            ("Suriye Pasajı 3, İstanbul", ""),
            ("Kuba Kowalski, Kwiatowa 5, Warszawa", ""),
            ("Corso Italia 3, Milano, Italia", "IT"),
            ("12 Example Avenue, Germany, 10115", "DE"),
            ("12 Example Avenue\nGermany 10115", "DE"),
            ("Office 4 Some Road Malta", "MT"),
        ] {
            assert_eq!(parse(raw).country, country, "{raw}");
        }
        // A final ISO code reads like the country's name: the street keeps its word in both.
        let texts = |raw: &str| parse(raw).words.iter().map(|t| t.text.clone()).collect::<Vec<_>>();
        assert_eq!(texts("Via Germania, IT"), texts("Via Germania, Italia"));
        assert_eq!(texts("Via Germania, IT"), ["germania"]);
        // "Sokagi" is "Sokağı" typed without Turkish letters: a street type, dropped like the accented one.
        for raw in ["Karanfil Sokagi 5", "Karanfil Sokağı 5", "Ataturk Bulvari 5"] {
            assert_eq!(parse(raw).words.len(), 1, "{raw}");
        }
    }

    /// Chinese and Korean names (data/cjk.json, #93): spacing, traditional and simplified characters, and company forms
    /// written without a space don't keep a name from matching.
    #[test]
    fn chinese_and_korean_names_match_however_spaced_or_written() {
        for (query, listed, kind) in [
            ("中兴通讯", "中兴通讯股份有限公司", SubjectKind::Entity),
            ("中興通訊", "中兴通讯", SubjectKind::Entity),
            ("中兴 通讯", "中兴通讯", SubjectKind::Entity),
            ("華為技術有限公司", "华为技术有限公司", SubjectKind::Entity),
            ("中兴通讯有限責任公司", "中兴通讯有限责任公司", SubjectKind::Entity),
            ("김 정은", "김정은", SubjectKind::Individual),
            ("주식회사 삼성전자", "삼성전자", SubjectKind::Entity),
            ("삼성전자 주식회사", "삼성전자", SubjectKind::Entity),
            ("삼성전자주식회사", "주식회사 삼성전자", SubjectKind::Entity),
        ] {
            let (outcome, percent) = screen(query, listed, kind);
            assert_eq!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
    }

    /// Joining Chinese and Korean words is for names: an address keeps its parts, so the same address in those scripts
    /// is found, and its country stays a word of its own.
    #[test]
    fn chinese_and_korean_addresses_keep_their_parts() {
        for address in ["서울 강남로 12", "中国, 北京, 长安街 1"] {
            let subject = ListedSubject {
                external_id: "X-1".to_string(),
                kind: SubjectKind::Entity,
                primary_name: "Northwind Trading".to_string(),
                names: vec![ListedName { full_name: "Northwind Trading".to_string(), is_weak: false }],
                addresses: vec![ListedAddress { raw: address.to_string(), country_code: String::new() }],
                programme: None,
                source_url: None,
            };
            let list = ScreenedList {
                code: "eu".to_string(),
                name: "EU".to_string(),
                index: Some(std::sync::Arc::new(ListIndex::build(vec![subject]))),
            };
            let request = ScreeningRequest { receiver_name: String::new(), company_name: String::new(), address: address.to_string() };
            let result = Screener::new(Options::default()).screen(&request, &[list], &AllowList::empty()).unwrap();
            let cell = &result.entries[0].cells[0];
            assert!(cell.best.is_some(), "{address}: {:?} {:?}", cell.outcome, cell.percent);
        }
        assert_eq!(text::words("中国, 北京, 1"), ["中国", "北京", "1"]);
    }

    /// A two-character Chinese or Korean core is a name of its own, like a Latin one of six letters: an exact match stays
    /// a Match with a generic word split off. A single character is still a short name.
    #[test]
    fn a_two_character_chinese_or_korean_name_is_not_short() {
        for name in ["中兴集团", "华为公司", "中兴", "삼성"] {
            let (outcome, percent) = screen(name, name, SubjectKind::Entity);
            assert_eq!(outcome, Some(Outcome::Match), "{name}: {percent:?}");
        }
        let (outcome, percent) = screen("华", "华", SubjectKind::Entity);
        assert_eq!(outcome, Some(Outcome::Review), "{percent:?}");
    }

    /// Every traditional character reads as its final simplified form, so a chain (薴 to 苧 to 苎) reads alike from
    /// either end.
    #[test]
    fn traditional_characters_read_to_the_end_of_a_chain() {
        assert_eq!(text::simplified("王薴華"), text::simplified("王苧華"));
        let (outcome, percent) = screen("王薴華", "王苧華", SubjectKind::Individual);
        assert_eq!(outcome, Some(Outcome::Match), "{percent:?}");
    }

    /// A character beyond the Basic Multilingual Plane is Han too: 王𠵾 (simplified, extension B) is 王㗲 (traditional),
    /// and its repeated characters still count.
    #[test]
    fn han_characters_beyond_the_basic_plane_are_han() {
        let (outcome, percent) = screen("王𠵾", "王㗲", SubjectKind::Individual);
        assert_eq!(outcome, Some(Outcome::Match), "{percent:?}");
        let (outcome, percent) = screen("王𠵾", "王𠵾𠵾", SubjectKind::Individual);
        assert_eq!(outcome, Some(Outcome::NoMatch), "{percent:?}");
        assert_eq!(text::words("王𠵾华 有限公司"), ["王𠵾华", "有限公司"]);
    }

    /// The ideographic zero is a character of the name: 七〇八研究所 isn't 七八研究所, at most a possible match for a
    /// person to review, as one letter more in a long Latin name is.
    #[test]
    fn the_ideographic_zero_is_kept() {
        let (outcome, percent) = screen("七〇八研究所", "七八研究所", SubjectKind::Entity);
        assert_ne!(outcome, Some(Outcome::Match), "{percent:?}");
        let (outcome, percent) = screen("七〇八研究所", "七〇八研究所", SubjectKind::Entity);
        assert_eq!(outcome, Some(Outcome::Match), "{percent:?}");
    }

    /// A repeated Han or Hangul character makes another name, not a spelling of the same one.
    #[test]
    fn a_repeated_chinese_or_korean_character_is_another_name() {
        for (query, listed) in [("李娜", "李娜娜"), ("김민수", "김민민수")] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
            assert_eq!(outcome, Some(Outcome::NoMatch), "{query} / {listed}: {percent:?}");
        }
    }

    /// The company forms alone, or different names, never match.
    #[test]
    fn different_chinese_and_korean_names_dont_match() {
        for (query, listed) in [("中兴通讯", "华为技术"), ("股份有限公司", "中兴通讯股份有限公司"), ("삼성전자", "현대자동차")]
        {
            let (outcome, percent) = screen(query, listed, SubjectKind::Entity);
            assert_ne!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
    }

    /// A name one side writes as one word and the other as two (#93): the two read together align with the one, both
    /// ways, and the list finds it by them.
    #[test]
    fn a_word_written_as_two_aligns_with_the_two() {
        for (query, listed, kind) in [
            ("Zhongxing Telecom", "Zhong Xing Telecom", SubjectKind::Entity),
            ("Zhong Xing Telecom", "Zhongxing Telecom", SubjectKind::Entity),
            ("Huawei", "Hua Wei", SubjectKind::Entity),
            ("Hua Wei Technologies", "Huawei Technologies", SubjectKind::Entity),
            ("Kim Jongun", "Kim Jong Un", SubjectKind::Individual),
            ("Kim Jong Un", "Kim Jongun", SubjectKind::Individual),
            ("Wang Xiaoming", "Wang Xiao-ming", SubjectKind::Individual),
            ("Xiao Ming Wang", "Wang Xiaoming", SubjectKind::Individual),
            ("Mc Donald Shipping", "McDonald Shipping", SubjectKind::Entity),
        ] {
            let (outcome, percent) = screen(query, listed, kind);
            assert_eq!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
    }

    /// Joining never loses what the words as written align: Hua Wei Huawei against Wei Huawei Hua is the same three
    /// words reordered.
    #[test]
    fn joining_words_never_loses_the_words_as_written() {
        let (outcome, percent) = screen("Hua Wei Huawei", "Wei Huawei Hua", SubjectKind::Entity);
        assert_eq!(outcome, Some(Outcome::Match), "{percent:?}");
        let (outcome, percent) = screen("Hua Wei Huawei", "Hua Wei Huawei", SubjectKind::Entity);
        assert_eq!((outcome, percent), (Some(Outcome::Match), Some(99)));
    }

    /// An alias found as written ranks ahead of one found partly joined, so many partial aliases can't push it past
    /// the candidate limit: Hua Wei finds the Hua Wei alias though thousands of Hua Huawei come first.
    #[test]
    fn an_alias_found_as_written_stays_a_candidate() {
        let named = |id: usize, name: &str| ListedSubject {
            external_id: format!("X-{id}"),
            kind: SubjectKind::Entity,
            primary_name: name.to_string(),
            names: vec![ListedName { full_name: name.to_string(), is_weak: false }],
            addresses: Vec::new(),
            programme: None,
            source_url: None,
        };
        let options = Options::default();
        let mut subjects: Vec<ListedSubject> = (0..options.candidate_limit + 10).map(|i| named(i, "Hua Huawei")).collect();
        subjects.push(named(usize::MAX, "Hua Wei"));
        let list =
            ScreenedList { code: "eu".to_string(), name: "EU".to_string(), index: Some(std::sync::Arc::new(ListIndex::build(subjects))) };
        let request = ScreeningRequest { receiver_name: String::new(), company_name: "Hua Wei".to_string(), address: String::new() };
        let result = Screener::new(options).screen(&request, &[list], &AllowList::empty()).unwrap();
        let cell = &result.entries[0].cells[0];
        assert_eq!(cell.outcome, Some(Outcome::Match), "{:?}", cell.percent);
    }

    /// Every choice of joins is tried: the first join mustn't block a better one, and some joins can help while others
    /// don't, both ways round.
    #[test]
    fn the_best_choice_of_joins_is_taken() {
        for (query, listed) in [
            ("Hua Wei Ren Huawei", "Hua Weiren Huawei"),
            ("Hua Weiren Huawei", "Hua Wei Ren Huawei"),
            ("Hua Wei Huawei Zhongxing", "Wei Huawei Hua Zhong Xing"),
            ("Wei Huawei Hua Zhong Xing", "Hua Wei Huawei Zhongxing"),
        ] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Entity);
            assert_eq!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
    }

    /// Joining is for words that read together as the other side's word: different names stay apart.
    #[test]
    fn joining_words_never_makes_different_names_match() {
        for (query, listed, kind) in [
            ("Zhongxing Telecom", "Zhong Hua Telecom", SubjectKind::Entity),
            ("Kim Jongun", "Kim Jong Il", SubjectKind::Individual),
            ("Hua Wei", "Weihua", SubjectKind::Entity),
        ] {
            let (outcome, percent) = screen(query, listed, kind);
            assert_ne!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
    }

    /// The explanation counts entered words as entered: two joined are the first's, and the word after them keeps its
    /// own place (Telecom is the third entered).
    #[test]
    fn a_joined_pair_is_explained_by_the_entered_words() {
        let subject = ListedSubject {
            external_id: "X-1".to_string(),
            kind: SubjectKind::Entity,
            primary_name: "Zhongxing Telecom".to_string(),
            names: vec![ListedName { full_name: "Zhongxing Telecom".to_string(), is_weak: false }],
            addresses: Vec::new(),
            programme: None,
            source_url: None,
        };
        let list = ScreenedList {
            code: "eu".to_string(),
            name: "EU".to_string(),
            index: Some(std::sync::Arc::new(ListIndex::build(vec![subject]))),
        };
        let request =
            ScreeningRequest { receiver_name: String::new(), company_name: "Zhong Xing Telecom".to_string(), address: String::new() };
        let result = Screener::new(Options::default()).screen(&request, &[list], &AllowList::empty()).unwrap();
        let best = result.entries[0].cells[0].best.as_ref().unwrap();
        let entered: Vec<Option<i32>> = best.words.iter().map(|w| w.entered).collect();
        assert_eq!(entered, [Some(1), Some(3)], "{:?}", best.words);
    }

    /// The same normalisation never makes different names match.
    #[test]
    fn different_arabic_names_still_dont_match() {
        for (query, listed) in [("محمد عبد الله", "خالد عبد الرحمن"), ("حسن علي", "حسين كريم")] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
            assert_eq!(outcome, Some(Outcome::NoMatch), "{query} / {listed}: {percent:?}");
        }
    }
}
