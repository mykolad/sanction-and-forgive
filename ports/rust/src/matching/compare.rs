//! Comparing two words (spec section 5.4), and finding the words of a vocabulary within two edits of one.

use std::cell::RefCell;
use std::collections::HashMap;
use std::sync::Mutex;

use super::text::Token;

/// How two tokens aligned, most certain first.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Default)]
pub enum AlignMethod {
    #[default]
    None,
    Identical,
    Transliteration,
    SpellingPair,
    FoldKey,
    FoldKeyLatin,
    Cognate,
    OneEdit,
    OneEditShort,
    TwoEdits,
}

#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct Alignment {
    pub similarity: f64,
    pub method: AlignMethod,
}

impl Alignment {
    const fn new(similarity: f64, method: AlignMethod) -> Self {
        Alignment { similarity, method }
    }

    pub fn aligned(&self) -> bool {
        self.method != AlignMethod::None
    }

    /// Aligned by a method that caps the match (fold key with both sides Latin, cognate, short edits).
    pub fn capped(&self) -> bool {
        matches!(self.method, AlignMethod::FoldKeyLatin | AlignMethod::Cognate | AlignMethod::OneEditShort | AlignMethod::TwoEdits)
    }
}

const PAIR_BITS: u32 = 12;
const PAIR_SLOTS: usize = 1 << PAIR_BITS;

#[derive(Clone, Copy)]
struct ComparedPair {
    query: *const Token,
    listed: *const Token,
    alignment: Alignment,
}

const EMPTY_PAIR: ComparedPair =
    ComparedPair { query: std::ptr::null(), listed: std::ptr::null(), alignment: Alignment::new(0.0, AlignMethod::None) };

/// Compares two tokens; the first rule that applies wins. One serves one search, on one thread, and remembers the
/// pairs it compared (by pointer: tokens are interned) in a direct-mapped cache: a pair that lands on another's slot
/// is compared again, which gives the same answer. Every slot a search wrote is emptied when it ends: no pointer
/// outlives the tokens it names, and nothing entered stays behind.
pub struct TokenComparer {
    pairs: Vec<ComparedPair>,
    written: Vec<u16>,
}

thread_local! {
    static COMPARER: RefCell<Option<TokenComparer>> = const { RefCell::new(None) };
}

impl TokenComparer {
    fn new() -> Self {
        TokenComparer { pairs: vec![EMPTY_PAIR; PAIR_SLOTS], written: Vec::new() }
    }

    /// Runs search with this thread's comparer (made once, emptied after every search).
    pub fn with<R>(search: impl FnOnce(&mut TokenComparer) -> R) -> R {
        let mut comparer = COMPARER.with(|cell| cell.borrow_mut().take()).unwrap_or_else(TokenComparer::new);
        let result = search(&mut comparer);
        comparer.forget();
        COMPARER.with(|cell| *cell.borrow_mut() = Some(comparer));
        result
    }

    fn forget(&mut self) {
        for &slot in &self.written {
            self.pairs[slot as usize] = EMPTY_PAIR;
        }
        self.written.clear();
    }

    pub fn compare(&mut self, query: &Token, listed: &Token) -> Alignment {
        let (q, l) = (query as *const Token, listed as *const Token);
        let mut h = (q as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15) ^ (l as u64);
        h ^= h >> 31;
        h = h.wrapping_mul(0xBF58_476D_1CE4_E5B9);
        let index = (h >> (64 - PAIR_BITS)) as usize;
        let slot = self.pairs[index];
        if slot.query == q && slot.listed == l {
            return slot.alignment;
        }
        if slot.query.is_null() {
            self.written.push(index as u16);
        }
        let alignment = compare_once(query, listed);
        self.pairs[index] = ComparedPair { query: q, listed: l, alignment };
        alignment
    }
}

fn overlaps(a: &[usize], b: &[usize]) -> bool {
    a.iter().any(|x| b.contains(x))
}

fn compare_once(query: &Token, listed: &Token) -> Alignment {
    if query.text == listed.text {
        return Alignment::new(1.0, AlignMethod::Identical);
    }
    if query.is_number || listed.is_number {
        return Alignment::default();
    }
    if query.forms.iter().any(|form| listed.forms.contains(form)) {
        return Alignment::new(0.98, AlignMethod::Transliteration);
    }
    if overlaps(query.spelling_groups(), listed.spelling_groups()) {
        return Alignment::new(0.98, AlignMethod::SpellingPair);
    }
    if query.folds.iter().any(|fold| listed.folds.contains(fold)) {
        let method = if query.is_cyrillic || listed.is_cyrillic { AlignMethod::FoldKey } else { AlignMethod::FoldKeyLatin };
        return Alignment::new(0.96, method);
    }
    if overlaps(query.cognate_groups(), listed.cognate_groups()) {
        return Alignment::new(0.96, AlignMethod::Cognate);
    }
    compare_edits(query, listed)
}

/// The size of the buffers on the stack that hold a word's letters while it's compared; a longer word takes the heap.
const RUNE_BUFFER: usize = 48;

fn compare_edits(query: &Token, listed: &Token) -> Alignment {
    let mut best = Alignment::default();
    for qf in &query.forms {
        for lf in &listed.forms {
            let a = if qf.is_ascii() && lf.is_ascii() {
                // Letters are bytes: the same distances, without decoding.
                edit_alignment(qf.as_bytes(), lf.as_bytes())
            } else {
                let q: Vec<char> = qf.chars().collect();
                let l: Vec<char> = lf.chars().collect();
                edit_alignment(&q, &l)
            };
            if a.aligned() && (!best.aligned() || a.method < best.method || (a.method == best.method && a.similarity > best.similarity)) {
                best = a;
            }
        }
    }
    best
}

/// Aligns two forms by edits (one or two letters differ), or not at all.
fn edit_alignment<T: PartialEq + Copy>(q: &[T], l: &[T]) -> Alignment {
    let shorter = q.len().min(l.len());
    if shorter < 4 {
        return Alignment::default();
    }
    // A different first letter counts as one more edit, so then a distance of 2 or more already rules it out.
    let edits = if q[0] != l[0] { damerau_levenshtein(q, l, 2) + 1 } else { damerau_levenshtein(q, l, 3) };
    if edits != 1 && edits != 2 {
        return Alignment::default();
    }
    let jw = jaro_winkler(q, l);
    match edits {
        1 if shorter >= 8 => Alignment::new(jw, AlignMethod::OneEdit),
        1 => Alignment::new(jw, AlignMethod::OneEditShort),
        2 if shorter >= 8 && jw >= 0.90 => Alignment::new(jw, AlignMethod::TwoEdits),
        _ => Alignment::default(),
    }
}

/// The optimal string alignment distance, or limit as soon as it can't be lower. Only the band of cells within
/// limit - 1 of the diagonal is computed: a cell further out is at least limit away, so the cells just outside the
/// band hold limit, which gives every value under limit exactly and the rest as limit or more.
pub fn damerau_levenshtein<T: PartialEq + Copy>(a: &[T], b: &[T], limit: usize) -> usize {
    if a.len().abs_diff(b.len()) >= limit {
        return limit;
    }
    let width = b.len() + 1;
    let mut stack = [0usize; 3 * (RUNE_BUFFER + 1)];
    let mut heap;
    let rows: &mut [usize] = if width <= RUNE_BUFFER + 1 {
        &mut stack[..3 * width]
    } else {
        heap = vec![0usize; 3 * width];
        &mut heap
    };
    // Three rows, rotated by index: previous2, previous, current.
    let (mut p2, mut p, mut c) = (0usize, width, 2 * width);
    for j in 0..=b.len() {
        rows[p + j] = j;
    }
    for i in 1..=a.len() {
        let low = 1.max(i.saturating_sub(limit - 1));
        let high = b.len().min(i + limit - 1);
        rows[c] = i;
        if low > 1 {
            rows[c + low - 1] = limit;
        }
        if high < b.len() {
            rows[c + high + 1] = limit;
        }
        let mut row_minimum = rows[c];
        for j in low..=high {
            let cost = usize::from(a[i - 1] != b[j - 1]);
            let mut value = (rows[p + j] + 1).min(rows[c + j - 1] + 1).min(rows[p + j - 1] + cost);
            if i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1] {
                value = value.min(rows[p2 + j - 2] + 1);
            }
            rows[c + j] = value;
            row_minimum = row_minimum.min(value);
        }
        if row_minimum >= limit {
            return limit;
        }
        (p2, p, c) = (p, c, p2);
    }
    rows[p + b.len()].min(limit)
}

pub fn jaro_winkler<T: PartialEq + Copy>(a: &[T], b: &[T]) -> f64 {
    if a.is_empty() || b.is_empty() {
        return 0.0;
    }
    let window = (a.len().max(b.len()) / 2).saturating_sub(1);
    let mut a_stack = [false; RUNE_BUFFER];
    let mut b_stack = [false; RUNE_BUFFER];
    let (mut a_heap, mut b_heap);
    let (a_matched, b_matched): (&mut [bool], &mut [bool]) = if a.len() <= RUNE_BUFFER && b.len() <= RUNE_BUFFER {
        (&mut a_stack[..a.len()], &mut b_stack[..b.len()])
    } else {
        a_heap = vec![false; a.len()];
        b_heap = vec![false; b.len()];
        (&mut a_heap, &mut b_heap)
    };
    let mut matches = 0usize;
    for i in 0..a.len() {
        let start = i.saturating_sub(window);
        let end = (b.len() - 1).min(i + window);
        for j in start..=end {
            if !b_matched[j] && a[i] == b[j] {
                a_matched[i] = true;
                b_matched[j] = true;
                matches += 1;
                break;
            }
        }
    }
    if matches == 0 {
        return 0.0;
    }
    let mut transpositions = 0usize;
    let mut j = 0;
    for i in 0..a.len() {
        if !a_matched[i] {
            continue;
        }
        while !b_matched[j] {
            j += 1;
        }
        if a[i] != b[j] {
            transpositions += 1;
        }
        j += 1;
    }
    let m = matches as f64;
    let jaro = ((m / a.len() as f64) + (m / b.len() as f64) + ((m - transpositions as f64 / 2.0) / m)) / 3.0;
    let mut prefix = 0;
    while prefix < 4.min(a.len().min(b.len())) && a[prefix] == b[prefix] {
        prefix += 1;
    }
    jaro + (prefix as f64 * 0.1 * (1.0 - jaro))
}

/// Finds the fold keys in a vocabulary within two edits of a key: short keys through a delete-one index (hashes in
/// one sorted array), longer ones through trigrams; every candidate checked by edit distance.
pub struct FuzzyVocabulary {
    folds: Vec<String>,
    delete_keys: Vec<i64>,
    by_trigram: HashMap<String, Box<[u32]>>,
    /// A bit per fold, all clear between lookups.
    seen: Mutex<Vec<Vec<u64>>>,
}

const SHORT_MAX: usize = 7;

impl FuzzyVocabulary {
    pub fn new<'a>(vocabulary: impl IntoIterator<Item = &'a String>) -> Self {
        let mut folds: Vec<String> = Vec::new();
        let mut seen = std::collections::HashSet::new();
        for fold in vocabulary {
            if fold.chars().count() >= 4 && seen.insert(fold.as_str()) {
                folds.push(fold.clone());
            }
        }
        let mut delete_keys = Vec::new();
        let mut by_trigram: HashMap<String, Vec<u32>> = HashMap::new();
        for (id, fold) in folds.iter().enumerate() {
            if fold.chars().count() <= SHORT_MAX + 1 {
                for variant in delete_variants(fold) {
                    delete_keys.push(((fnv(&variant) as i64) << 32) | (id as u32 as i64));
                }
            }
            for trigram in distinct(trigrams(fold)) {
                by_trigram.entry(trigram).or_default().push(id as u32);
            }
        }
        delete_keys.sort_unstable();
        delete_keys.shrink_to_fit();
        let by_trigram = by_trigram.into_iter().map(|(k, v)| (k, v.into_boxed_slice())).collect();
        FuzzyVocabulary { folds, delete_keys, by_trigram, seen: Mutex::new(Vec::new()) }
    }

    /// The vocabulary keys other than fold within two edits of it, in the reference's order.
    pub fn near(&self, fold: &str) -> Vec<&str> {
        let chars: Vec<char> = fold.chars().collect();
        if chars.len() < 4 {
            return Vec::new();
        }
        let mut seen = self.seen.lock().unwrap().pop().unwrap_or_else(|| vec![0u64; self.folds.len().div_ceil(64)]);
        let mut ids: Vec<usize> = Vec::new();
        let mut add = |id: usize, seen: &mut Vec<u64>| {
            let (word, bit) = (id / 64, 1u64 << (id % 64));
            if seen[word] & bit == 0 {
                seen[word] |= bit;
                ids.push(id);
            }
        };
        if chars.len() <= SHORT_MAX + 1 {
            for variant in delete_variants(fold) {
                let hash = (fnv(&variant) as i64) << 32;
                let mut index = self.delete_keys.partition_point(|&k| k < hash);
                while index < self.delete_keys.len() && self.delete_keys[index] & !0xFFFF_FFFF == hash {
                    add((self.delete_keys[index] & 0xFFFF_FFFF) as usize, &mut seen);
                    index += 1;
                }
            }
        }
        for trigram in distinct(trigrams(fold)) {
            if let Some(list) = self.by_trigram.get(&trigram) {
                for &id in list.iter() {
                    add(id as usize, &mut seen);
                }
            }
        }
        for &id in &ids {
            seen[id / 64] = 0;
        }
        self.seen.lock().unwrap().push(seen);

        let ascii = fold.is_ascii();
        let mut out = Vec::new();
        for id in ids {
            let other = self.folds[id].as_str();
            if other == fold {
                continue;
            }
            let distance = if ascii && other.is_ascii() {
                damerau_levenshtein(fold.as_bytes(), other.as_bytes(), 3)
            } else {
                let o: Vec<char> = other.chars().collect();
                damerau_levenshtein(&chars, &o, 3)
            };
            if distance <= 2 {
                out.push(other);
            }
        }
        out
    }
}

fn delete_variants(fold: &str) -> Vec<String> {
    let chars: Vec<char> = fold.chars().collect();
    let mut out = vec![fold.to_string()];
    for i in 0..chars.len() {
        out.push(chars[..i].iter().chain(&chars[i + 1..]).collect());
    }
    out
}

/// FNV-1a over UTF-16 code units, as the reference computes it (32-bit, wrapping).
fn fnv(value: &str) -> i32 {
    let mut hash: i32 = -2128831035; // 2166136261 as a signed 32-bit integer
    for unit in value.encode_utf16() {
        hash = (hash ^ unit as i32).wrapping_mul(16777619);
    }
    hash
}

fn trigrams(value: &str) -> Vec<String> {
    let chars: Vec<char> = value.chars().collect();
    (0..chars.len().saturating_sub(2)).map(|i| chars[i..i + 3].iter().collect()).collect()
}

fn distinct(values: Vec<String>) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    for v in values {
        if !out.contains(&v) {
            out.push(v);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn full_distance(a: &[char], b: &[char]) -> usize {
        let mut d = vec![vec![0usize; b.len() + 1]; a.len() + 1];
        for (i, row) in d.iter_mut().enumerate() {
            row[0] = i;
        }
        for (j, cell) in d[0].iter_mut().enumerate() {
            *cell = j;
        }
        for i in 1..=a.len() {
            for j in 1..=b.len() {
                let cost = usize::from(a[i - 1] != b[j - 1]);
                d[i][j] = (d[i - 1][j] + 1).min(d[i][j - 1] + 1).min(d[i - 1][j - 1] + cost);
                if i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1] {
                    d[i][j] = d[i][j].min(d[i - 2][j - 2] + 1);
                }
            }
        }
        d[a.len()][b.len()]
    }

    #[test]
    fn damerau_levenshtein_is_the_full_distance_capped() {
        // xorshift: four letters, one of them Cyrillic, words up to 12 long.
        let mut state: u64 = 0x2545_F491_4F6C_DD1D;
        let mut next = |n: u64| {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state % n
        };
        let letters = ['a', 'b', 'c', 'ж'];
        for _ in 0..200_000 {
            let a: Vec<char> = (0..next(13)).map(|_| letters[next(4) as usize]).collect();
            let b: Vec<char> = (0..next(13)).map(|_| letters[next(4) as usize]).collect();
            let full = full_distance(&a, &b);
            for limit in 1..=4 {
                assert_eq!(damerau_levenshtein(&a, &b, limit), full.min(limit), "{a:?} {b:?} limit {limit}");
            }
        }
    }
}
