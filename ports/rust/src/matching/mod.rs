//! The Rust port of SanctionAndForgive.Matching: pure, deterministic, no I/O beyond its embedded dictionaries. Every
//! rule follows the .NET reference exactly (docs/conformance.md), and the speed work follows the Go port's
//! (ports/go/README.md, "Speed and memory").

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
        assert_eq!(Screener::new(Options::default()).matcher_version, "566ac3d19d5b");
    }
}
