//! Every number the matcher uses (MatchingOptions in the reference), the matcher version, and the percentage bands.

use sha2::{Digest, Sha256};

use super::data::MatcherData;

#[derive(Debug, Clone)]
pub struct Options {
    pub match_score: f64,
    pub review_score: f64,
    pub match_min_percent: i32,
    pub match_max_percent: i32,
    pub review_min_percent: i32,
    pub review_max_percent: i32,
    pub no_match_max_percent: i32,
    pub not_all_aligned_cap: i32,
    pub thin_core_cap: i32,
    pub generic_cap: i32,
    pub weak_alias_cap: i32,
    pub capped_method_cap: i32,
    pub conflicting_address_cap: i32,
    pub contained_name_percent: i32,
    pub two_initials_percent: i32,
    pub one_initial_percent: i32,
    pub unchecked_initials_percent: i32,
    pub contradicting_initials_percent: i32,
    pub corroborated_bonus: i32,
    pub address_only_percent: i32,
    pub min_token_weight: f64,
    pub candidate_limit: usize,
    pub hits_per_cell: usize,
    pub best_candidate_min_percent: i32,
}

impl Default for Options {
    /// The reference's defaults.
    fn default() -> Self {
        Options {
            match_score: 0.95,
            review_score: 0.85,
            match_min_percent: 90,
            match_max_percent: 99,
            review_min_percent: 50,
            review_max_percent: 89,
            no_match_max_percent: 49,
            not_all_aligned_cap: 89,
            thin_core_cap: 60,
            generic_cap: 75,
            weak_alias_cap: 75,
            capped_method_cap: 75,
            conflicting_address_cap: 80,
            contained_name_percent: 70,
            two_initials_percent: 70,
            one_initial_percent: 60,
            unchecked_initials_percent: 50,
            contradicting_initials_percent: 30,
            corroborated_bonus: 5,
            address_only_percent: 50,
            min_token_weight: 1.0,
            candidate_limit: 2000,
            hits_per_cell: 5,
            best_candidate_min_percent: 20,
        }
    }
}

/// A number as .NET's invariant culture writes a double ("R" round-trip): the shortest that reads back the same.
fn invariant(v: f64) -> String {
    // Rust's Display is the shortest round-trip form too, without an exponent for these values.
    format!("{v}")
}

impl Options {
    /// The reference's MatcherVersion: SHA-256 of the dictionaries' hash and every option by name (ordinal order,
    /// invariant formatting), the first 12 hex characters. The same dictionaries and options give the same version in
    /// every implementation.
    pub fn version(&self, data: &MatcherData) -> String {
        let i = |v: i64| v.to_string();
        let mut values = vec![
            format!("BestCandidateMinPercent={}", i(self.best_candidate_min_percent.into())),
            format!("CandidateLimit={}", i(self.candidate_limit as i64)),
            format!("CappedMethodCap={}", i(self.capped_method_cap.into())),
            format!("ConflictingAddressCap={}", i(self.conflicting_address_cap.into())),
            format!("ContainedNamePercent={}", i(self.contained_name_percent.into())),
            format!("ContradictingInitialsPercent={}", i(self.contradicting_initials_percent.into())),
            format!("CorroboratedBonus={}", i(self.corroborated_bonus.into())),
            format!("GenericCap={}", i(self.generic_cap.into())),
            format!("HitsPerCell={}", i(self.hits_per_cell as i64)),
            format!("MatchMaxPercent={}", i(self.match_max_percent.into())),
            format!("MatchMinPercent={}", i(self.match_min_percent.into())),
            format!("MatchScore={}", invariant(self.match_score)),
            format!("MinTokenWeight={}", invariant(self.min_token_weight)),
            format!("NoMatchMaxPercent={}", i(self.no_match_max_percent.into())),
            format!("NotAllAlignedCap={}", i(self.not_all_aligned_cap.into())),
            format!("OneInitialPercent={}", i(self.one_initial_percent.into())),
            format!("ReviewMaxPercent={}", i(self.review_max_percent.into())),
            format!("ReviewMinPercent={}", i(self.review_min_percent.into())),
            format!("ReviewScore={}", invariant(self.review_score)),
            format!("ThinCoreCap={}", i(self.thin_core_cap.into())),
            format!("TwoInitialsPercent={}", i(self.two_initials_percent.into())),
            format!("UncheckedInitialsPercent={}", i(self.unchecked_initials_percent.into())),
            format!("WeakAliasCap={}", i(self.weak_alias_cap.into())),
            format!("AddressOnlyPercent={}", i(self.address_only_percent.into())),
        ];
        values.sort();
        let digest = Sha256::digest(format!("{}|{}", data.hash, values.join(";")).as_bytes());
        hex::encode(digest)[..12].to_string()
    }

    pub fn to_percent(&self, score: f64) -> i32 {
        if score >= self.match_score {
            linear(score, self.match_score, 1.0, self.match_min_percent, self.match_max_percent)
        } else if score >= self.review_score {
            linear(score, self.review_score, self.match_score, self.review_min_percent, self.review_max_percent)
        } else {
            linear(score, 0.0, self.review_score, 0, self.no_match_max_percent)
        }
    }

    pub fn to_outcome(&self, percent: i32) -> Outcome {
        if percent >= self.match_min_percent {
            Outcome::Match
        } else if percent >= self.review_min_percent {
            Outcome::Review
        } else {
            Outcome::NoMatch
        }
    }

    pub fn band_max(&self, percent: i32) -> i32 {
        match self.to_outcome(percent) {
            Outcome::Match => self.match_max_percent,
            Outcome::Review => self.review_max_percent,
            Outcome::NoMatch => self.no_match_max_percent,
        }
    }
}

/// A cell's outcome from its percentage.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum Outcome {
    NoMatch,
    Review,
    Match,
}

impl Outcome {
    pub fn as_str(&self) -> &'static str {
        match self {
            Outcome::NoMatch => "NoMatch",
            Outcome::Review => "Review",
            Outcome::Match => "Match",
        }
    }
}

fn linear(score: f64, low: f64, high: f64, low_percent: i32, high_percent: i32) -> i32 {
    let fraction = ((score - low) / (high - low)).clamp(0.0, 1.0);
    let value = (low_percent as f64 + fraction * (high_percent - low_percent) as f64).floor() as i32;
    value.clamp(low_percent, high_percent)
}
