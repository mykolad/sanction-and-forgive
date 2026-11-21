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
pub mod destinations;
pub mod identifiers;
pub mod index;
pub mod korean;
pub mod names;
pub mod options;
pub mod scorer;
pub mod screener;
pub mod text;

pub use addresses::AddressState;
pub use destinations::{Destination, DestinationKind, DestinationNotice, DestinationState, DestinationStatus, Destinations};
pub use index::{ListIndex, ListedAddress, ListedIdentifier, ListedName, ListedSubject, SubjectKind, SubjectMatch};
pub use options::{Options, Outcome};
pub use scorer::WordAlignment;
pub use screener::{
    AllowList, AllowListEntry, Cell, Entry, EntryKind, ListedHit, OverallOutcome, Party, PartyTag, ScreenedList, Screener,
    ScreeningRequest, ScreeningResult, ROLES,
};
pub use text::release_unused_words;

#[cfg(test)]
mod tests {
    use super::*;

    /// The reference's MatcherVersion for the shipped dictionaries and default options.
    /// The owner's rules for identifiers and dates of birth (#68).
    #[test]
    fn an_equal_identifier_is_a_match_a_different_one_never_lowers_and_a_date_of_birth_is_only_said() {
        let listed = |id: &str, name: &str, kind: SubjectKind, identifiers: &[(&str, &str)], dates: &[&str]| ListedSubject {
            external_id: id.to_string(),
            kind,
            primary_name: name.to_string(),
            names: vec![ListedName { full_name: name.to_string(), is_weak: false }],
            addresses: Vec::new(),
            programme: None,
            source_url: None,
            identifiers: identifiers
                .iter()
                .map(|(k, v)| ListedIdentifier { kind: k.to_string(), value: v.to_string(), country: None })
                .collect(),
            birth_dates: dates.iter().map(|d| d.to_string()).collect(),
        };
        let list = ScreenedList {
            code: "uk".to_string(),
            name: "UK".to_string(),
            index: Some(std::sync::Arc::new(ListIndex::build(vec![
                listed("A", "Orion Shipping Holdings LLC", SubjectKind::Entity, &[("Tax ID No.", "OGRN 1027700132195")], &[]),
                listed("B", "Ivan Petrovich Sokolov", SubjectKind::Individual, &[("Passport", "P04581926")], &["1960-03-12"]),
            ]))),
        };
        let screener = Screener::new(Options::default());
        let screen = |request: ScreeningRequest| screener.screen(&request, std::slice::from_ref(&list), &AllowList::empty()).unwrap();

        // An equal identifier: a Match whatever the name (a renamed company), the number found inside the listed value.
        let renamed = screen(ScreeningRequest {
            company_name: "Northwind Trading".to_string(),
            company_identifiers: vec!["1027700132195".to_string()],
            ..Default::default()
        });
        let cell = &renamed.entries[0].cells[0];
        assert_eq!((cell.outcome, cell.percent), (Some(Outcome::Match), Some(100)));
        let best = cell.best.as_ref().unwrap();
        assert_eq!(best.subject.external_id, "A");
        assert!(best.alignment[0].starts_with("Identifier: equals the listed Tax ID No. OGRN 1027700132195"), "{:?}", best.alignment);
        assert_eq!(renamed.outcome, OverallOutcome::Match);
        assert_eq!(renamed.entries[0].identifiers, 1);

        // A different identifier with the name matching: never lower, only said; the date of birth said too.
        let plain = screen(ScreeningRequest { receiver_name: "Ivan Sokolov".to_string(), ..Default::default() });
        let different = screen(ScreeningRequest {
            receiver_name: "Ivan Sokolov".to_string(),
            receiver_identifiers: vec!["X9999999".to_string()],
            receiver_birth_date: Some("1961".to_string()),
            ..Default::default()
        });
        let (before, after) = (&plain.entries[0].cells[0], &different.entries[0].cells[0]);
        assert_eq!(after.percent, before.percent);
        assert_ne!(after.outcome, Some(Outcome::NoMatch));
        let notes = &after.best.as_ref().unwrap().alignment;
        assert!(
            notes.iter().any(|n| n.starts_with("Identifiers: the entered ones aren't among the listed (Passport P04581926)")),
            "{notes:?}"
        );
        assert!(notes.iter().any(|n| n == "Date of birth: differs from the listed (1960-03-12)"), "{notes:?}");
        let same = screen(ScreeningRequest {
            receiver_name: "Ivan Sokolov".to_string(),
            receiver_birth_date: Some("1960-03-12".to_string()),
            ..Default::default()
        });
        let notes = &same.entries[0].cells[0].best.as_ref().unwrap().alignment;
        assert!(notes.iter().any(|n| n == "Date of birth: the entered one is among the listed (1960-03-12)"), "{notes:?}");
        assert_eq!(same.entries[0].cells[0].percent, before.percent);

        // A short identifier is never compared; a party's identifiers are its own.
        let short = screen(ScreeningRequest {
            company_name: "Northwind Trading".to_string(),
            company_identifiers: vec!["1027".to_string()],
            ..Default::default()
        });
        assert_eq!(short.entries[0].cells[0].outcome, Some(Outcome::NoMatch));
        let party = Party {
            role: "consignee",
            name: "Somebody Else".to_string(),
            person: true,
            identifiers: vec!["p 0458-1926".to_string()],
            ..Default::default()
        };
        let shipment = screener
            .screen_parties(
                &ScreeningRequest::default(),
                &[party],
                std::slice::from_ref(&list),
                &AllowList::empty(),
                &Destinations::empty(),
            )
            .unwrap();
        assert_eq!(shipment.entries[0].cells[0].best.as_ref().unwrap().subject.external_id, "B");
        assert_eq!(shipment.outcome, OverallOutcome::Match);
    }

    /// Belarusian and Russian forms of one name (cognates.json, #91): a cognate caps the match at Review, and an initial
    /// never stands for a patronymic (no patronymic pairs).
    #[test]
    fn belarusian_and_russian_forms_of_a_name_are_a_review() {
        for (query, listed, expected) in [
            ("Лідзія Ярмошына", "Lidia Yermoshina", Outcome::Review),
            ("Аляксандр Лукашэнка", "Alexander Lukashenko", Outcome::Review),
            ("Віктар Сямашка", "Viktor Semashko", Outcome::Review),
            ("Дзяніс Быкаў", "Denis Bykov", Outcome::Review),
            ("Popov M.", "Aleksandr Nikolaevich Popov", Outcome::NoMatch),
        ] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
            assert_eq!(outcome, Some(expected), "{query} / {listed}: {percent:?}");
        }
    }

    /// Vessels (#69): in an index of their own, found only by a vessel entry, by name or by IMO number.
    #[test]
    fn a_vessel_is_found_by_its_name_or_its_imo_number_and_only_by_a_vessel_entry() {
        let subject = |id: &str, name: &str, kind: SubjectKind, imo: Option<&str>| ListedSubject {
            external_id: id.to_string(),
            kind,
            primary_name: name.to_string(),
            names: vec![ListedName { full_name: name.to_string(), is_weak: false }],
            addresses: Vec::new(),
            programme: None,
            source_url: None,
            identifiers: imo
                .map(|v| ListedIdentifier { kind: "IMO number".to_string(), value: v.to_string(), country: None })
                .into_iter()
                .collect(),
            birth_dates: Vec::new(),
        };
        let index = ListIndex::build(vec![
            subject("P", "Kelly Grace", SubjectKind::Individual, None),
            subject("V", "M/V Ocean Faye", SubjectKind::Vessel, Some("9321689")),
        ]);
        assert_eq!((index.subjects().len(), index.vessels().map(|v| v.subjects().len()), index.all_subjects().count()), (1, Some(1), 2));
        let list = ScreenedList { code: "nz".to_string(), name: "NZ".to_string(), index: Some(std::sync::Arc::new(index)) };
        let empty = ScreenedList {
            code: "un".to_string(),
            name: "UN".to_string(),
            index: Some(std::sync::Arc::new(ListIndex::build(vec![subject("U", "Kelly Grace", SubjectKind::Individual, None)]))),
        };
        let screener = Screener::new(Options::default());
        let lists = [list, empty];
        let screen = |request: ScreeningRequest| screener.screen(&request, &lists, &AllowList::empty()).unwrap();

        // By name, the prefix aside; a list without vessels is searched and finds none.
        let named = screen(ScreeningRequest { vessel_name: "MT Ocean Faye".to_string(), ..Default::default() });
        let entry = &named.entries[0];
        assert_eq!((entry.kind, entry.input.as_str()), (EntryKind::Vessel, "MT Ocean Faye"));
        assert_eq!(entry.cells[0].outcome, Some(Outcome::Match));
        assert_eq!(entry.cells[0].best.as_ref().unwrap().subject.external_id, "V");
        assert_eq!((entry.cells[1].checked, entry.cells[1].outcome), (true, Some(Outcome::NoMatch)));

        // By IMO number alone, written however: a Match whatever the name.
        let numbered = screen(ScreeningRequest { vessel_imo_number: Some("9321689".to_string()), ..Default::default() });
        let entry = &numbered.entries[0];
        assert_eq!((entry.input.as_str(), entry.cells[0].percent), ("IMO 9321689", Some(100)));
        assert!(entry.cells[0].best.as_ref().unwrap().alignment[0].starts_with("Identifier: equals the listed IMO number 9321689"));
        let renamed = screen(ScreeningRequest {
            vessel_name: "Glacia".to_string(),
            vessel_imo_number: Some("9321689".to_string()),
            ..Default::default()
        });
        assert_eq!(renamed.outcome, OverallOutcome::Match);

        // An IMO number is compared with listed IMO numbers alone: another number of the ship's that's equal is no match.
        let other = ListIndex::build(vec![ListedSubject {
            identifiers: vec![
                ListedIdentifier { kind: "IMO number".to_string(), value: "9321689".to_string(), country: None },
                ListedIdentifier { kind: "Registration Number".to_string(), value: "1234567".to_string(), country: None },
            ],
            ..subject("W", "Glacia", SubjectKind::Vessel, None)
        }]);
        let other = [ScreenedList { code: "xx".to_string(), name: "XX".to_string(), index: Some(std::sync::Arc::new(other)) }];
        let by_registration = screener
            .screen(&ScreeningRequest { vessel_imo_number: Some("1234567".to_string()), ..Default::default() }, &other, &AllowList::empty())
            .unwrap();
        assert_eq!(by_registration.entries[0].cells[0].outcome, Some(Outcome::NoMatch));

        // A list that lists ships but was imported before they were kept: its vessel cell is Not checked.
        let old = [ScreenedList {
            code: "uk".to_string(),
            name: "UK".to_string(),
            index: Some(std::sync::Arc::new(ListIndex::build(vec![subject("U", "Kelly Grace", SubjectKind::Individual, None)]))),
        }];
        let before = screener
            .screen(&ScreeningRequest { vessel_name: "Ocean Faye".to_string(), ..Default::default() }, &old, &AllowList::empty())
            .unwrap();
        assert_eq!((before.entries[0].cells[0].checked, before.outcome), (false, OverallOutcome::Incomplete));

        // A person's or a company's name never finds a ship, and a ship's name never finds a person.
        let person = screen(ScreeningRequest { receiver_name: "Ocean Faye".to_string(), ..Default::default() });
        assert!(person.entries[0].cells[0].best.as_ref().is_none_or(|b| b.subject.external_id != "V"));
        let ship = screen(ScreeningRequest { vessel_name: "Kelly Grace".to_string(), ..Default::default() });
        assert_eq!(ship.outcome, OverallOutcome::NoMatch);
    }

    #[test]
    fn matcher_version_is_the_references() {
        assert_eq!(Screener::new(Options::default()).matcher_version, "0d436265fc5d");
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
            identifiers: Vec::new(),
            birth_dates: Vec::new(),
        };
        let list = ScreenedList {
            code: "eu".to_string(),
            name: "EU".to_string(),
            index: Some(std::sync::Arc::new(ListIndex::build(vec![subject]))),
        };
        let request = ScreeningRequest {
            receiver_name: String::new(),
            company_name: query.to_string(),
            address: String::new(),
            ..Default::default()
        };
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
    /// Chinese names in characters and in pinyin (data/pinyin.json, #93): each way round, a surname's own reading, a
    /// city read as a word; never two Chinese names that only sound alike.
    #[test]
    fn a_chinese_name_matches_its_pinyin_but_never_another_name_that_sounds_alike() {
        for (query, listed, kind) in [
            ("张伟", "Zhang Wei", SubjectKind::Individual),
            ("習近平", "Xi Jinping", SubjectKind::Individual),
            ("Zhang Wei", "张伟", SubjectKind::Individual),
            ("曾国藩", "Zeng Guofan", SubjectKind::Individual),
            ("华为", "Huawei", SubjectKind::Entity),
            ("重庆", "Chongqing", SubjectKind::Entity),
            // The given name first, as OFAC writes it; a single character a word, never an initial.
            ("张伟", "Wei ZHANG", SubjectKind::Individual),
            ("张 Guofan", "Zhang Guofan", SubjectKind::Individual),
            // A traditional character's own reading, and a word read as a whole after a surname.
            ("乾隆", "Qianlong", SubjectKind::Individual),
            ("曾长江", "Zeng Changjiang", SubjectKind::Individual),
        ] {
            let (outcome, percent) = screen(query, listed, kind);
            assert_eq!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
        // A word joined from particles is in Latin script too, compared whole; a surname as a word of its own reads as one.
        for (query, listed) in [("An Na", "安娜"), ("曾 Guofan", "Zeng Guofan")] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
            assert_eq!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
        // Never a particle reading of a Latin word; marked pinyin reads as plain.
        for (query, listed) in [("Al Lina", "利娜"), ("Abouguofan", "阿布国藩")] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
            assert_ne!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
        let (outcome, percent) = screen("Ān Nà", "安娜", SubjectKind::Individual);
        assert_eq!(outcome, Some(Outcome::Match), "Ān Nà / 安娜: {percent:?}");
        // A Chinese word aligned by its pinyin is long enough for initials, as its Latin spelling is.
        let person = |name: &str, address: Option<&str>| ListedSubject {
            external_id: "P".to_string(),
            kind: SubjectKind::Individual,
            primary_name: name.to_string(),
            names: vec![ListedName { full_name: name.to_string(), is_weak: false }],
            addresses: address.map(|a| ListedAddress { raw: a.to_string(), country_code: String::new() }).into_iter().collect(),
            programme: None,
            source_url: None,
            identifiers: Vec::new(),
            birth_dates: Vec::new(),
        };
        let one = |subject: ListedSubject, request: ScreeningRequest| {
            let list = ScreenedList {
                code: "xx".to_string(),
                name: "XX".to_string(),
                index: Some(std::sync::Arc::new(ListIndex::build(vec![subject]))),
            };
            Screener::new(Options::default()).screen(&request, &[list], &AllowList::empty()).unwrap().entries[0].cells[0].outcome
        };
        for entered in ["张 W.", "Zhang W."] {
            let outcome = one(person("Zhang Wei", None), ScreeningRequest { receiver_name: entered.to_string(), ..Default::default() });
            assert_eq!(outcome, Some(Outcome::Review), "{entered} / Zhang Wei");
        }
        // A name read given name first is a person's: an address never aligns by it (武汉 isn't Hanwu).
        let outcome = one(
            person("Somebody", Some("Albert Hanwu 12")),
            ScreeningRequest { address: "Albert 武汉 12".to_string(), ..Default::default() },
        );
        assert_eq!(outcome, Some(Outcome::NoMatch), "Albert 武汉 12 / Albert Hanwu 12");
        // A partial name in either script is a contained name, as in one script.
        // 〇 and a supplementary character have readings; 々 reads as the character it repeats.
        for (query, listed) in [("七〇八", "Qilingba"), ("王𠵾", "Wang Huo"), ("王㗲", "Wang Huo")] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
            assert_eq!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
        assert_eq!(crate::matching::text::pinyin_readings("丽々"), ["lili"]);
        assert_eq!(crate::matching::text::pinyin_readings("重庆々"), ["chongqingqing"]);
        let (outcome, percent) = screen("张 Wei", "Zhang Wei Ming", SubjectKind::Individual);
        assert_eq!(outcome, Some(Outcome::Review), "张 Wei / Zhang Wei Ming: {percent:?}");
        // A Chinese name's pinyin written a syllable a word (#93): three words in Latin script read as one, either way
        // round, a compound surname's too; the Chinese word stays whole, so its other readings and spacing still match.
        for (query, listed) in [
            ("张伟明", "Zhang Wei Ming"),
            ("Zhang Wei Ming", "张伟明"),
            ("张伟明", "Wei Ming ZHANG"),
            ("Wei Ming Zhang", "张伟明"),
            ("张伟明", "Weiming ZHANG"),
            ("Zhangweiming", "张伟明"),
            ("欧阳娜娜", "Ouyang Na Na"),
            ("曾国藩", "Zeng Guo Fan"),
            ("Weizhang", "张伟"),
            ("Cengguofan", "曾国藩"),
            ("张 伟明", "张伟明"),
            ("张伟明", "张 伟明"),
            ("高彬 倪", "Gaobin NI"),
        ] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
            assert_eq!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
        // Never three words in another script, nor a given name read the other way round.
        for (query, listed) in [("李娜", "Лина"), ("Wang Nali", "王李娜"), ("Ли Вэй Мин", "李伟明")] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
            assert_ne!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
        // Never two Chinese names that only sound alike, a transliteration, or a reading alike only after folding; a
        // place's name isn't read given name first, and one character is a short name on either side.
        for (query, listed, kind) in [
            ("章伟", "张伟", SubjectKind::Individual),
            ("利娜", "Лина", SubjectKind::Individual),
            ("张伟", "Zang Wei", SubjectKind::Individual),
            ("Ли На", "利娜", SubjectKind::Individual),
            ("Jingbei", "北京", SubjectKind::Entity),
            ("Zhang", "张", SubjectKind::Entity),
        ] {
            let (outcome, percent) = screen(query, listed, kind);
            assert_ne!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
        // The exact reading is found however many aliases read alike only after folding.
        let mut subjects: Vec<ListedSubject> = (0..2100)
            .map(|i| ListedSubject {
                external_id: format!("Z-{i}"),
                kind: SubjectKind::Individual,
                primary_name: "Zang Wei".to_string(),
                names: vec![ListedName { full_name: "Zang Wei".to_string(), is_weak: false }],
                addresses: Vec::new(),
                programme: None,
                source_url: None,
                identifiers: Vec::new(),
                birth_dates: Vec::new(),
            })
            .collect();
        subjects.push(ListedSubject {
            external_id: "REAL".to_string(),
            primary_name: "Zhang Wei".to_string(),
            names: vec![ListedName { full_name: "Zhang Wei".to_string(), is_weak: false }],
            ..subjects[0].clone()
        });
        let list =
            ScreenedList { code: "xx".to_string(), name: "XX".to_string(), index: Some(std::sync::Arc::new(ListIndex::build(subjects))) };
        let result = Screener::new(Options::default())
            .screen(&ScreeningRequest { receiver_name: "张伟".to_string(), ..Default::default() }, &[list], &AllowList::empty())
            .unwrap();
        assert_eq!(result.entries[0].cells[0].best.as_ref().map(|b| b.subject.external_id.as_str()), Some("REAL"));
        // And in both directions, whatever the votes of the aliases alike only after folding.
        for (crowd, real, entered) in [
            ("Zangwei Weizang", "Zhang Wei", "张伟"),
            ("Zangwei", "张伟", "Zhangwei"),
            ("张伟 Smith", "Zhangwei Ivanov", "Zhangwei Ivanov"),
            ("Zangweiming", "张伟明", "Zhangweiming"),
            ("Gao Bin", "高彬 倪", "Gaobin Ni"),
            // Three words read as one find only a Chinese word's pinyin: an ordinary query never votes through them.
            ("Zhangweiming Zhang Wei Ming Ouyang Na Na", "Zhangweiming Ouyangnana", "Zhangweiming Ouyangnana"),
            // Three words in Latin script find the Chinese name they read as past a crowd of partial Latin names, and
            // the Chinese name finds the three words past names alike only after folding.
            ("Zhang Wei Ming Smith", "张伟明", "Zhang Wei Ming"),
            ("Zang Wei Ming", "Zhang Wei Ming", "张伟明"),
            // A Chinese word votes once for an alias however many of its readings find it: both orders of the name in
            // one alias don't outvote the exact one.
            ("Zhang Wei Ming Wei Ming Zhang Smith", "Zhangweiming Smith", "张伟明 Smith"),
            ("Zangwei Weizang", "张伟", "张伟"),
        ] {
            let mut subjects: Vec<ListedSubject> = (0..2100)
                .map(|i| ListedSubject {
                    external_id: format!("Z-{i}"),
                    kind: SubjectKind::Individual,
                    primary_name: crowd.to_string(),
                    names: vec![ListedName { full_name: crowd.to_string(), is_weak: false }],
                    addresses: Vec::new(),
                    programme: None,
                    source_url: None,
                    identifiers: Vec::new(),
                    birth_dates: Vec::new(),
                })
                .collect();
            subjects.push(ListedSubject {
                external_id: "REAL".to_string(),
                primary_name: real.to_string(),
                names: vec![ListedName { full_name: real.to_string(), is_weak: false }],
                ..subjects[0].clone()
            });
            let list = ScreenedList {
                code: "xx".to_string(),
                name: "XX".to_string(),
                index: Some(std::sync::Arc::new(ListIndex::build(subjects))),
            };
            let result = Screener::new(Options::default())
                .screen(&ScreeningRequest { receiver_name: entered.to_string(), ..Default::default() }, &[list], &AllowList::empty())
                .unwrap();
            assert_eq!(
                result.entries[0].cells[0].best.as_ref().map(|b| b.subject.external_id.as_str()),
                Some("REAL"),
                "{entered} among {crowd}"
            );
        }
        use crate::matching::text::pinyin_readings;
        assert_eq!(pinyin_readings("曾国藩"), ["cengguofan", "zengguofan", "guofanceng", "guofanzeng"]);
        assert_eq!(pinyin_readings("重庆"), ["chongqing"]);
    }

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
                identifiers: Vec::new(),
                birth_dates: Vec::new(),
            };
            let list = ScreenedList {
                code: "eu".to_string(),
                name: "EU".to_string(),
                index: Some(std::sync::Arc::new(ListIndex::build(vec![subject]))),
            };
            let request = ScreeningRequest {
                receiver_name: String::new(),
                company_name: String::new(),
                address: address.to_string(),
                ..Default::default()
            };
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

    /// Korean names in their different romanisations (data/korean.json, #93): the South's, McCune-Reischauer's, the
    /// North's and popular spellings of one name match.
    #[test]
    fn korean_names_match_in_any_romanisation() {
        for (query, listed) in [
            ("Kim Chong Un", "Kim Jong Un"),
            ("Kim Chŏng-ŭn", "Kim Jong Un"),
            ("Kim Jeong Eun", "Kim Jong Un"),
            ("Ri Yong Ho", "Lee Yong Ho"),
            ("Yi Yong Ho", "Ri Yong Ho"),
            ("Pak Pong Ju", "Park Pong Ju"),
            ("Choe Ryong Hae", "Choi Ryong Hae"),
            ("Choe Yong Hae", "Choe Ryong Hae"),
            ("Kim Il Sung", "Kim Il-song"),
            ("Kim Il Sung", "Kim Il Seong"),
            ("Jang Song Thaek", "Chang Song Taek"),
            ("Kim Yong Chol", "Kim Yong Chul"),
            ("Kim Young Nam", "Kim Yong Nam"),
            ("Jin Ho Park", "Jin Ho Pak"),
            ("Choe Young Hae", "Choe Ryong Hae"),
        ] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
            assert_eq!(outcome, Some(Outcome::Match), "{query} / {listed}: {percent:?}");
        }
    }

    /// A title doesn't keep a name from reading as Korean (General Ri Yong Ho / Lee Yong Ho). A single letter stays an
    /// initial even where it could spell a surname: Ali O. is still a possible match for Ali.
    #[test]
    fn a_title_keeps_a_name_korean_and_a_single_letter_stays_an_initial() {
        let (outcome, percent) = screen("General Ri Yong Ho", "Lee Yong Ho", SubjectKind::Individual);
        assert_eq!(outcome, Some(Outcome::Match), "{percent:?}");
        let (outcome, percent) = screen("Ali O.", "Ali", SubjectKind::Individual);
        assert_eq!(outcome, Some(Outcome::Review), "{percent:?}");
    }

    /// A Korean word's many readings count once per alias: surname-only aliases can't outvote the full name past the
    /// candidate limit (Lee Cheol / Ri Chol among a limit's worth of Lee Nam).
    #[test]
    fn korean_readings_dont_outvote_the_full_name() {
        let named = |id: usize, name: &str| ListedSubject {
            external_id: format!("X-{id}"),
            kind: SubjectKind::Individual,
            primary_name: name.to_string(),
            names: vec![ListedName { full_name: name.to_string(), is_weak: false }],
            addresses: Vec::new(),
            programme: None,
            source_url: None,
            identifiers: Vec::new(),
            birth_dates: Vec::new(),
        };
        let options = Options::default();
        let mut subjects: Vec<ListedSubject> = (0..options.candidate_limit + 10).map(|i| named(i, "Lee Nam")).collect();
        subjects.push(named(usize::MAX, "Ri Chol"));
        let list =
            ScreenedList { code: "eu".to_string(), name: "EU".to_string(), index: Some(std::sync::Arc::new(ListIndex::build(subjects))) };
        let request = ScreeningRequest {
            receiver_name: "Lee Cheol".to_string(),
            company_name: String::new(),
            address: String::new(),
            ..Default::default()
        };
        let result = Screener::new(options).screen(&request, &[list], &AllowList::empty()).unwrap();
        let cell = &result.entries[0].cells[0];
        assert_eq!(cell.outcome, Some(Outcome::Match), "{:?}", cell.percent);
    }

    /// Only a Korean name's words read alike: a different given name stays different, and Jung or Chung in a name
    /// that isn't Korean keeps its own spelling.
    #[test]
    fn korean_readings_never_make_different_names_match() {
        for (query, listed) in [
            ("Kim Jong Un", "Kim Jong Il"),
            ("Kim Jong Un", "Kim Yo Jong"),
            ("Ri Yong Ho", "Ri Yong Nam"),
            ("Kim Mira", "Kim Mina"),
            ("Kim Mi-ra", "Kim Mi-na"),
            ("Kim Mi Ra", "Kim Mi Na"),
            ("Carl Jung", "Carl Chung"),
            ("Maria Jung", "Maria Chung"),
        ] {
            let (outcome, percent) = screen(query, listed, SubjectKind::Individual);
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
            identifiers: Vec::new(),
            birth_dates: Vec::new(),
        };
        let options = Options::default();
        let mut subjects: Vec<ListedSubject> = (0..options.candidate_limit + 10).map(|i| named(i, "Hua Huawei")).collect();
        subjects.push(named(usize::MAX, "Hua Wei"));
        let list =
            ScreenedList { code: "eu".to_string(), name: "EU".to_string(), index: Some(std::sync::Arc::new(ListIndex::build(subjects))) };
        let request = ScreeningRequest {
            receiver_name: String::new(),
            company_name: "Hua Wei".to_string(),
            address: String::new(),
            ..Default::default()
        };
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
            identifiers: Vec::new(),
            birth_dates: Vec::new(),
        };
        let list = ScreenedList {
            code: "eu".to_string(),
            name: "EU".to_string(),
            index: Some(std::sync::Arc::new(ListIndex::build(vec![subject]))),
        };
        let request = ScreeningRequest {
            receiver_name: String::new(),
            company_name: "Zhong Xing Telecom".to_string(),
            address: String::new(),
            ..Default::default()
        };
        let result = Screener::new(Options::default()).screen(&request, &[list], &AllowList::empty()).unwrap();
        let best = result.entries[0].cells[0].best.as_ref().unwrap();
        let entered: Vec<Option<i32>> = best.words.iter().map(|w| w.entered).collect();
        assert_eq!(entered, [Some(1), Some(3)], "{:?}", best.words);
    }

    /// A shipment's parties (#65): each party's name and address are entries of their own, tagged with the party, and
    /// the outcome is the highest of them all.
    #[test]
    fn every_party_of_a_shipment_is_screened() {
        let subject = ListedSubject {
            external_id: "X-1".to_string(),
            kind: SubjectKind::Entity,
            primary_name: "Northwind Trading LLC".to_string(),
            names: vec![ListedName { full_name: "Northwind Trading LLC".to_string(), is_weak: false }],
            addresses: Vec::new(),
            programme: None,
            source_url: None,
            identifiers: Vec::new(),
            birth_dates: Vec::new(),
        };
        let list = ScreenedList {
            code: "eu".to_string(),
            name: "EU".to_string(),
            index: Some(std::sync::Arc::new(ListIndex::build(vec![subject]))),
        };
        let request = ScreeningRequest {
            receiver_name: String::new(),
            company_name: "Bluefield Logistics".to_string(),
            address: String::new(),
            ..Default::default()
        };
        let parties = [
            Party {
                role: "shipper",
                name: "Pinecrest Exports GmbH".to_string(),
                person: false,
                address: "Hamburg, Germany".to_string(),
                ..Default::default()
            },
            Party {
                role: "consignee",
                name: "Northwind Trading LLC".to_string(),
                person: false,
                address: String::new(),
                ..Default::default()
            },
        ];
        let result = Screener::new(Options::default())
            .screen_parties(&request, &parties, &[list], &AllowList::empty(), &Destinations::empty())
            .unwrap();
        let tags: Vec<(Option<u32>, &str)> = result.entries.iter().map(|e| (e.party.map(|p| p.number), e.kind.as_str())).collect();
        assert_eq!(tags, [(None, "Company"), (Some(1), "Company"), (Some(1), "Address"), (Some(2), "Company")]);
        assert_eq!(result.entries[3].party.unwrap().role, "consignee");
        assert_eq!(result.outcome, OverallOutcome::Match);
        // Parties alone are enough; nothing at all isn't.
        let empty = ScreeningRequest::default();
        let list = ScreenedList { code: "eu".to_string(), name: "EU".to_string(), index: None };
        assert!(Screener::new(Options::default())
            .screen_parties(&empty, &parties[..1], std::slice::from_ref(&list), &AllowList::empty(), &Destinations::empty())
            .is_ok());
        assert!(Screener::new(Options::default())
            .screen_parties(&empty, &[], &[list], &AllowList::empty(), &Destinations::empty())
            .is_err());
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
