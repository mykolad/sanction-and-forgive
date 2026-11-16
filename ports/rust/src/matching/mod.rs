//! The Rust port of SanctionAndForgive.Matching: pure, deterministic, no I/O beyond its embedded dictionaries. Every
//! rule follows the .NET reference exactly (docs/conformance.md), except where improvements land here first (owner's
//! decision, 2026-11-17): Arabic-script names compared in their own script (data/arabic.json, #92). The speed work
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
        assert_eq!(Screener::new(Options::default()).matcher_version, "9ffa31f8de38");
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
