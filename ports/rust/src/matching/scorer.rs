//! A name's percentage against one listed alias (spec section 5.6): words aligned one to one, weighted by IDF, mapped
//! to the bands, capped; contained names; initials.

use super::compare::{AlignMethod, Alignment, TokenComparer};
use super::data::MatcherData;
use super::names::NameForm;
use super::options::Options;
use super::text::{to_upper_invariant, Token, TokenRef};

/// One word of a name alignment, without the entered text.
#[derive(Debug, Clone, PartialEq, serde::Serialize)]
pub struct WordAlignment {
    pub entered: Option<i32>,
    pub listed: Option<String>,
    pub how: &'static str,
}

#[derive(Debug, Clone, Default)]
pub struct NameScoreDetails {
    pub explanation: Vec<String>,
    pub words: Vec<WordAlignment>,
    pub notes: Vec<String>,
}

/// A name's percentage against one listed alias and the lowest cap on it: all a search needs to rank the aliases.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ScoreValue {
    pub percent: i32,
    pub ceiling: i32,
}

#[derive(Debug, Clone)]
struct ScoreCap {
    cap: i32,
    reason: &'static str,
}

#[derive(Debug, Clone, Copy)]
pub struct Pair {
    pub query: usize,
    pub listed: usize,
    pub alignment: Alignment,
}

/// An initials reading: its value, and its explanation when one was asked for.
struct InitialsReading {
    value: ScoreValue,
    details: Option<NameScoreDetails>,
}

/// What scoring a name decided, kept to explain it.
#[derive(Default)]
struct Evaluation {
    zero: Option<&'static str>,
    initials: Option<InitialsReading>,
    pairs: Vec<Pair>,
    caps: Vec<ScoreCap>,
    percent: i32,
    contained: bool,
}

/// Scores names for one search, on one thread: its buffers are reused from one alias to the next.
pub struct NameScorer<'a> {
    pub comparer: &'a mut TokenComparer,
    pub options: &'a Options,
    pub data: &'a MatcherData,
    scratch: Evaluation,
    cells: Vec<Alignment>,
    values: Vec<f64>,
    usable: Vec<bool>,
    columns: Vec<usize>,
    best: Vec<f64>,
}

const MAX_EXACT_TOKENS: usize = 12;

fn unit_weight(_: &Token) -> f64 {
    1.0
}

impl<'a> NameScorer<'a> {
    pub fn new(comparer: &'a mut TokenComparer, options: &'a Options, data: &'a MatcherData) -> Self {
        NameScorer {
            comparer,
            options,
            data,
            scratch: Evaluation::default(),
            cells: Vec::new(),
            values: Vec::new(),
            usable: Vec::new(),
            columns: Vec::new(),
            best: Vec::new(),
        }
    }

    /// Scores query against one listed alias. Nothing it computes outlives the call: it's the inner loop.
    pub fn score(&mut self, query: &NameForm, listed: &NameForm, weak_alias: bool, weight: &mut dyn FnMut(&Token) -> f64) -> ScoreValue {
        let mut e = std::mem::take(&mut self.scratch);
        e.zero = None;
        e.initials = None;
        e.pairs.clear();
        e.caps.clear();
        e.percent = 0;
        e.contained = false;
        let value = self.evaluate(query, listed, weak_alias, weight, &mut e, false);
        self.scratch = e;
        value
    }

    /// Scores query against the alias again, the same way, and explains the result.
    pub fn explain(
        &mut self,
        query: &NameForm,
        listed: &NameForm,
        weak_alias: bool,
        weight: &mut dyn FnMut(&Token) -> f64,
    ) -> NameScoreDetails {
        let mut e = Evaluation::default();
        self.evaluate(query, listed, weak_alias, weight, &mut e, true);
        if let Some(reason) = e.zero {
            return NameScoreDetails { explanation: vec![reason.to_string()], words: Vec::new(), notes: vec![reason.to_string()] };
        }
        if let Some(reading) = e.initials {
            return reading.details.unwrap_or_default();
        }

        let mut paired_query = vec![false; query.core.len()];
        let mut paired_listed = vec![false; listed.core.len()];
        for p in &e.pairs {
            paired_query[p.query] = true;
            paired_listed[p.listed] = true;
        }
        let mut explanation = Vec::new();
        let mut words = Vec::new();
        for p in &e.pairs {
            explanation.push(describe(&query.core[p.query], &listed.core[p.listed], p.alignment));
            words.push(WordAlignment {
                entered: Some(p.query as i32 + 1),
                listed: Some(listed.core[p.listed].text.clone()),
                how: how(p.alignment.method),
            });
        }
        for (i, t) in query.core.iter().enumerate() {
            if !paired_query[i] {
                explanation.push(format!("{}: not in the listed name", t.text));
            }
        }
        for (j, t) in listed.core.iter().enumerate() {
            if !paired_listed[j] {
                explanation.push(format!("{}: listed, not entered", t.text));
            }
        }
        for (i, &paired) in paired_query.iter().enumerate() {
            if !paired {
                words.push(WordAlignment { entered: Some(i as i32 + 1), listed: None, how: "NotListed" });
            }
        }
        for (j, t) in listed.core.iter().enumerate() {
            if !paired_listed[j] {
                words.push(WordAlignment { entered: None, listed: Some(t.text.clone()), how: "NotEntered" });
            }
        }
        if e.contained {
            explanation.push("One name contains the other".to_string());
        }
        for legal in union(&query.legal_classes, &listed.legal_classes) {
            explanation.push(format!("Legal form {legal} not compared"));
        }
        let notes = cap_notes(e.percent, &e.caps);
        explanation.extend(notes.iter().cloned());
        NameScoreDetails { explanation, words, notes }
    }

    fn evaluate(
        &mut self,
        query: &NameForm,
        listed: &NameForm,
        weak_alias: bool,
        weight: &mut dyn FnMut(&Token) -> f64,
        e: &mut Evaluation,
        explain: bool,
    ) -> ScoreValue {
        let o = self.options;
        if query.legal_only {
            e.zero = Some("Legal form only: nothing distinctive to compare");
            return ScoreValue { percent: 0, ceiling: 0 };
        }
        if query.is_empty() || listed.is_empty() {
            e.zero = Some("Nothing distinctive to compare");
            return ScoreValue { percent: 0, ceiling: 0 };
        }

        let has_initials = query.is_person && listed.is_person && (!query.initials.is_empty() || !listed.initials.is_empty());
        if has_initials {
            // The two readings in order; the first of equals wins.
            let mut best: Option<InitialsReading> = None;
            if !query.initials.is_empty() {
                best = self.score_initials(query, listed, weak_alias, true, explain);
            }
            if !listed.initials.is_empty() {
                if let Some(reading) = self.score_initials(listed, query, weak_alias, false, explain) {
                    if best.as_ref().is_none_or(|b| reading.value.percent > b.value.percent) {
                        best = Some(reading);
                    }
                }
            }
            if let Some(reading) = best {
                let value = reading.value;
                e.initials = Some(reading);
                return value;
            }
        }

        let mut pairs = std::mem::take(&mut e.pairs);
        self.align(&query.core, &listed.core, weight, &mut pairs);
        let mut aligned = 0.0;
        for p in &pairs {
            aligned += p.alignment.similarity * (weight(&query.core[p.query]) + weight(&listed.core[p.listed]));
        }
        let mut total = 0.0;
        for t in &query.core {
            total += weight(t);
        }
        let mut listed_total = 0.0;
        for t in &listed.core {
            listed_total += weight(t);
        }
        total += listed_total;
        let mut percent = o.to_percent(aligned / total);

        // Every pair holds one query word and one listed word, each at most once: a side is all aligned when it has
        // as many words as there are pairs.
        let unaligned = pairs.len() < query.core.len() || pairs.len() < listed.core.len();
        let shorter = query.core.len().min(listed.core.len());
        let contained = shorter >= 2
            && pairs.len() == shorter
            && query.core.len() != listed.core.len()
            && pairs.iter().all(|p| {
                matches!(
                    p.alignment.method,
                    AlignMethod::Identical | AlignMethod::Transliteration | AlignMethod::SpellingPair | AlignMethod::FoldKey
                )
            });

        if contained {
            percent = o.contained_name_percent;
        } else if unaligned {
            e.caps.push(ScoreCap { cap: o.not_all_aligned_cap, reason: "Not every distinctive word aligned" });
        }

        if !query.is_person
            && !listed.is_person
            && query.core.len() == 1
            && listed.core.len() == 1
            && pairs.len() == 1
            && query.core[0].len() < 4
            && !intersects(&query.legal_classes, &listed.legal_classes)
        {
            let reason =
                if query.legal_classes.is_empty() { "Short name with no legal form" } else { "Short name with a different legal form" };
            e.caps.push(ScoreCap { cap: o.thin_core_cap, reason });
        }
        if !query.is_person && !listed.is_person && query.generic != listed.generic {
            e.caps.push(ScoreCap { cap: o.generic_cap, reason: "Generic words differ" });
        }
        if weak_alias {
            e.caps.push(ScoreCap { cap: o.weak_alias_cap, reason: "The list marks this alias as low quality" });
        }
        if pairs.iter().any(|p| p.alignment.capped()) {
            e.caps.push(ScoreCap { cap: o.capped_method_cap, reason: "A word aligned only approximately" });
        }
        if has_initials {
            e.caps.push(ScoreCap { cap: o.not_all_aligned_cap, reason: "Initials couldn't be checked against given names" });
            let unaligned_query = unpaired(&query.core, &pairs, true);
            let unaligned_listed = unpaired(&listed.core, &pairs, false);
            let listed_candidates: Vec<TokenRef> = unaligned_listed.into_iter().chain(listed.initials.iter().cloned()).collect();
            let query_candidates: Vec<TokenRef> = unaligned_query.into_iter().chain(query.initials.iter().cloned()).collect();
            if self.contradicts(&query.initials, &listed_candidates) || self.contradicts(&listed.initials, &query_candidates) {
                e.caps.push(ScoreCap { cap: o.contradicting_initials_percent, reason: "An initial contradicts a given name or initial" });
            }
        }
        e.pairs = pairs;
        e.percent = percent;
        e.contained = contained;

        let ceiling = e.caps.iter().map(|c| c.cap).min().unwrap_or(i32::MAX);
        ScoreValue { percent: percent.min(ceiling), ceiling }
    }

    fn contradicts(&mut self, initials: &[TokenRef], candidates: &[TokenRef]) -> bool {
        let matches = self.match_initials(initials, candidates);
        matches.len() < initials.len() && matches.len() < candidates.len()
    }

    /// The largest one-to-one assignment of initials to candidates (Kuhn's augmenting paths): (initial, candidate)
    /// pairs, by initial.
    fn match_initials(&mut self, initials: &[TokenRef], candidates: &[TokenRef]) -> Vec<(usize, usize)> {
        let fits: Vec<Vec<bool>> =
            initials.iter().map(|initial| candidates.iter().map(|candidate| self.initial_matches(initial, candidate)).collect()).collect();
        let mut owner: Vec<Option<usize>> = vec![None; candidates.len()];
        fn augment(initial: usize, seen: &mut [bool], fits: &[Vec<bool>], owner: &mut [Option<usize>]) -> bool {
            for c in 0..owner.len() {
                if fits[initial][c] && !seen[c] {
                    seen[c] = true;
                    if owner[c].is_none_or(|o| augment(o, seen, fits, owner)) {
                        owner[c] = Some(initial);
                        return true;
                    }
                }
            }
            false
        }
        for i in 0..initials.len() {
            let mut seen = vec![false; candidates.len()];
            augment(i, &mut seen, &fits, &mut owner);
        }
        let mut matches: Vec<(usize, usize)> = owner.iter().enumerate().filter_map(|(c, o)| o.map(|i| (i, c))).collect();
        matches.sort();
        matches
    }

    /// An initial matches a given name whose fold key starts with the initial's, directly or through a cognate, or
    /// the same initial on the other side.
    fn initial_matches(&self, initial: &Token, candidate: &Token) -> bool {
        let starts = |names: &[String], prefixes: &[String]| {
            names.iter().any(|name| prefixes.iter().any(|prefix| !prefix.is_empty() && name.starts_with(prefix.as_str())))
        };
        if candidate.len() == 1 {
            return intersects(&initial.folds, &candidate.folds) || intersects(&initial.forms, &candidate.forms);
        }
        let mut names = candidate.folds.clone();
        for &group in candidate.cognate_groups() {
            names.extend(self.data.cognate_folds[group].iter().cloned());
        }
        starts(&candidate.forms, &initial.forms) || starts(&names, &initial.folds)
    }

    /// Reads one side's initials against the other's given names; the explanation only when explain.
    fn score_initials(
        &mut self,
        initials_side: &NameForm,
        other_side: &NameForm,
        weak_alias: bool,
        query_has_initials: bool,
        explain: bool,
    ) -> Option<InitialsReading> {
        let o = self.options;
        if initials_side.core.is_empty() {
            return None;
        }
        let mut pairs = Vec::new();
        self.align(&initials_side.core, &other_side.core, &mut unit_weight, &mut pairs);
        let any_long = pairs.iter().any(|p| initials_side.core[p.query].len() >= 4);
        if pairs.len() != initials_side.core.len() || !any_long {
            return None;
        }

        let mut explanation = Vec::new();
        let mut words = Vec::new();
        let mut paired_other = vec![false; other_side.core.len()];
        for p in &pairs {
            paired_other[p.listed] = true;
            if !explain {
                continue;
            }
            if query_has_initials {
                explanation.push(describe(&initials_side.core[p.query], &other_side.core[p.listed], p.alignment));
                words.push(WordAlignment {
                    entered: Some(p.query as i32 + 1),
                    listed: Some(other_side.core[p.listed].text.clone()),
                    how: how(p.alignment.method),
                });
            } else {
                explanation.push(describe(&other_side.core[p.listed], &initials_side.core[p.query], p.alignment));
                words.push(WordAlignment {
                    entered: Some(p.listed as i32 + 1),
                    listed: Some(initials_side.core[p.query].text.clone()),
                    how: how(p.alignment.method),
                });
            }
        }
        let unpaired_positions: Vec<usize> = (0..other_side.core.len()).filter(|&i| !paired_other[i]).collect();
        let candidates: Vec<TokenRef> =
            unpaired_positions.iter().map(|&i| other_side.core[i].clone()).chain(other_side.initials.iter().cloned()).collect();
        let candidate_position = |candidate: usize| unpaired_positions.get(candidate).map(|&i| i as i32 + 1);
        let matches = self.match_initials(&initials_side.initials, &candidates);
        let matched_candidates: Vec<usize> = matches.iter().map(|&(_, c)| c).collect();
        let left_over: Vec<&TokenRef> =
            candidates.iter().enumerate().filter(|(c, _)| !matched_candidates.contains(c)).map(|(_, t)| t).collect();
        let matched = matches.len();
        let mut contradicted = 0;
        for (i, initial_token) in initials_side.initials.iter().enumerate() {
            let found = matches.iter().find(|&&(initial, _)| initial == i).map(|&(_, c)| c);
            if found.is_none() && !left_over.is_empty() {
                contradicted += 1;
            }
            if !explain {
                continue;
            }
            let initial = to_upper_invariant(&initial_token.text);
            if let Some(c) = found {
                explanation.push(format!("Initial {initial} matches {}", candidates[c].text));
                if query_has_initials {
                    words.push(WordAlignment { entered: None, listed: Some(candidates[c].text.clone()), how: "Initial" });
                } else {
                    words.push(WordAlignment { entered: candidate_position(c), listed: Some(initial_token.text.clone()), how: "Initial" });
                }
            } else {
                let listed_text = if query_has_initials { None } else { Some(initial_token.text.clone()) };
                if !left_over.is_empty() {
                    let names: Vec<&str> = left_over.iter().map(|t| t.text.as_str()).collect();
                    explanation.push(format!("Initial {initial} doesn't match {}", names.join(", ")));
                    words.push(WordAlignment { entered: None, listed: listed_text, how: "InitialContradicts" });
                } else {
                    explanation.push(format!("Initial {initial}: nothing to compare with"));
                    words.push(WordAlignment { entered: None, listed: listed_text, how: "InitialUnchecked" });
                }
            }
        }

        let percent = if contradicted > 0 {
            o.contradicting_initials_percent
        } else if matched >= 2 {
            o.two_initials_percent
        } else if matched == 1 {
            o.one_initial_percent
        } else {
            o.unchecked_initials_percent
        };
        let mut caps = Vec::new();
        if weak_alias {
            caps.push(ScoreCap { cap: o.weak_alias_cap, reason: "The list marks this alias as low quality" });
        }
        if pairs.iter().any(|p| p.alignment.capped()) {
            caps.push(ScoreCap { cap: o.capped_method_cap, reason: "A word aligned only approximately" });
        }
        let ceiling = caps.iter().map(|c| c.cap).min().unwrap_or(i32::MAX);
        let details = explain.then(|| {
            let notes = cap_notes(percent, &caps);
            explanation.extend(notes.iter().cloned());
            NameScoreDetails { explanation, words, notes }
        });
        Some(InitialsReading { value: ScoreValue { percent: percent.min(ceiling), ceiling }, details })
    }

    /// Appends the best one-to-one pairs to out: the highest total weighted similarity, and among equal totals the
    /// fewest capped alignments (exact up to 12 tokens a side, greedy beyond). Its buffers are the scorer's.
    pub fn align(&mut self, query: &[TokenRef], listed: &[TokenRef], weight: &mut dyn FnMut(&Token) -> f64, out: &mut Vec<Pair>) {
        let n = listed.len();
        let cells = query.len() * n;
        self.cells.clear();
        self.cells.resize(cells, Alignment::default());
        self.values.clear();
        self.values.resize(cells, f64::NEG_INFINITY);
        self.usable.clear();
        self.usable.resize(n, false);
        for (i, q) in query.iter().enumerate() {
            for (j, l) in listed.iter().enumerate() {
                let cell = i * n + j;
                let a = self.comparer.compare(q, l);
                self.cells[cell] = a;
                if a.aligned() {
                    let capped = if a.capped() { 1e-9 } else { 0.0 };
                    self.values[cell] = a.similarity * (weight(q) + weight(l)) - capped;
                    self.usable[j] = true;
                }
            }
        }
        self.columns.clear();
        self.columns.extend((0..n).filter(|&j| self.usable[j]));
        if n <= MAX_EXACT_TOKENS && query.len() <= MAX_EXACT_TOKENS {
            let masks = 1usize << self.columns.len();
            self.best.clear();
            self.best.resize((query.len() + 1) * masks, 0.0);
            exact_assignment(&self.values, query.len(), n, &self.columns, &mut self.best, &self.cells, out);
        } else {
            for (q, l) in greedy_assignment(&self.values, query.len(), n) {
                out.push(Pair { query: q, listed: l, alignment: self.cells[q * n + l] });
            }
        }
    }
}

fn exact_assignment(
    values: &[f64],
    query_count: usize,
    listed_count: usize,
    columns: &[usize],
    best: &mut [f64],
    alignments: &[Alignment],
    out: &mut Vec<Pair>,
) {
    let masks = 1usize << columns.len();
    for i in (0..query_count).rev() {
        for mask in 0..masks {
            let mut value = best[(i + 1) * masks + mask];
            for (c, &column) in columns.iter().enumerate() {
                let v = values[i * listed_count + column];
                if mask & (1 << c) == 0 && v != f64::NEG_INFINITY {
                    value = value.max(v + best[(i + 1) * masks + (mask | 1 << c)]);
                }
            }
            best[i * masks + mask] = value;
        }
    }
    let mut used = 0usize;
    for i in 0..query_count {
        for (c, &column) in columns.iter().enumerate() {
            let v = values[i * listed_count + column];
            if used & (1 << c) == 0
                && v != f64::NEG_INFINITY
                && (v + best[(i + 1) * masks + (used | 1 << c)] - best[i * masks + used]).abs() < 1e-12
            {
                out.push(Pair { query: i, listed: column, alignment: alignments[i * listed_count + column] });
                used |= 1 << c;
                break;
            }
        }
    }
}

fn greedy_assignment(values: &[f64], query_count: usize, listed_count: usize) -> Vec<(usize, usize)> {
    let mut candidates: Vec<(usize, usize, f64)> = Vec::new();
    for i in 0..query_count {
        for j in 0..listed_count {
            let v = values[i * listed_count + j];
            if v != f64::NEG_INFINITY {
                candidates.push((i, j, v));
            }
        }
    }
    // Stable, highest first.
    candidates.sort_by(|a, b| b.2.partial_cmp(&a.2).unwrap_or(std::cmp::Ordering::Equal));
    let mut used_query = vec![false; query_count];
    let mut used_listed = vec![false; listed_count];
    let mut pairs = Vec::new();
    for (q, l, _) in candidates {
        if !used_query[q] && !used_listed[l] {
            used_query[q] = true;
            used_listed[l] = true;
            pairs.push((q, l));
        }
    }
    pairs
}

/// The tokens of one side (the query's, or the listed name's) that no pair holds, in order.
fn unpaired(tokens: &[TokenRef], pairs: &[Pair], query_side: bool) -> Vec<TokenRef> {
    tokens
        .iter()
        .enumerate()
        .filter(|(i, _)| !pairs.iter().any(|p| if query_side { p.query == *i } else { p.listed == *i }))
        .map(|(_, t)| t.clone())
        .collect()
}

/// Which caps lowered percent, lowest first (a stable sort: equal caps keep their order).
fn cap_notes(percent: i32, caps: &[ScoreCap]) -> Vec<String> {
    let mut applied: Vec<&ScoreCap> = caps.iter().filter(|c| c.cap < percent).collect();
    applied.sort_by_key(|c| c.cap);
    applied.iter().map(|c| format!("Capped at {}%: {}", c.cap, c.reason)).collect()
}

pub fn describe(query: &Token, listed: &Token, a: Alignment) -> String {
    let (q, l) = (&query.text, &listed.text);
    match a.method {
        AlignMethod::Identical => format!("{q} = {l}"),
        AlignMethod::Transliteration => format!("{q} read as {} = {l}", common(query, listed)),
        AlignMethod::SpellingPair => format!("{q} ~ {l} (spelling variant)"),
        AlignMethod::FoldKey => format!("{q} ~ {l} (romanisation)"),
        AlignMethod::FoldKeyLatin => format!("{q} ~ {l} (romanisation, both Latin)"),
        AlignMethod::Cognate => format!("{q} ~ {l} (Russian and Ukrainian forms of one name)"),
        AlignMethod::OneEdit | AlignMethod::OneEditShort => format!("{q} ~ {l} (one letter differs)"),
        AlignMethod::TwoEdits => format!("{q} ~ {l} (two letters differ)"),
        AlignMethod::None => format!("{q} ≠ {l}"),
    }
}

pub fn how(method: AlignMethod) -> &'static str {
    match method {
        AlignMethod::Identical => "Same",
        AlignMethod::Transliteration => "Transliterated",
        AlignMethod::SpellingPair => "SpellingVariant",
        AlignMethod::FoldKey | AlignMethod::FoldKeyLatin => "Romanisation",
        AlignMethod::Cognate => "Cognate",
        AlignMethod::OneEdit | AlignMethod::OneEditShort => "OneLetterDiffers",
        AlignMethod::TwoEdits => "TwoLettersDiffer",
        AlignMethod::None => "Different",
    }
}

fn common<'t>(query: &'t Token, listed: &Token) -> &'t str {
    query.forms.iter().find(|form| listed.forms.contains(form)).or(query.forms.first()).map(String::as_str).unwrap_or(&query.text)
}

fn intersects(a: &[String], b: &[String]) -> bool {
    a.iter().any(|x| b.contains(x))
}

fn union(a: &[String], b: &[String]) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    for x in a.iter().chain(b) {
        if !out.contains(x) {
            out.push(x.clone());
        }
    }
    out
}
