# Matching calibration: findings and the decision for the owner

The measurements are in `docs/matching-evaluation.md`, written by `dotnet SanctionAndForgive.Cli.dll evaluate` from
the active lists. **This is synthetic calibration**: perturbed list names and made-up names, no reviewed real cases.
This file is the reading of those numbers, written by hand. Run of 2026-11-06, all 10 fetched lists (locally,
including the three whose terms wait for a decision).

## Where the matcher stands

| Measure | Result | Spec target |
|---|---|---|
| Perturbed names recalled at Review or above | **97.4%** of 4,988 | at least 95% |
| Cyrillic aliases found in Latin-only lists | **79.7%** of 1,068 | reported |
| Made-up names reaching Review | 6.7% (people), 7.7% (companies) | |
| Made-up names reaching Match | **0%** | |

By perturbation: other romanisation 100%, diacritics dropped 100%, words swapped 99.0%, legal form changed 98.1%,
typo 95.5%, surname and initials 95.2%. A trial counts only when it really changes the name. Surname and initials are
tried only where the surname is certain (one word in capitals, as OFAC, the EU and the UK write surnames). With a
common surname, many listed people score 60-70% and a cell shows the top 5, so the right person is sometimes sixth or
lower. That's the rule working as designed (section 5.6), and why lowering those percentages for very common
surnames is one of the options below.

**Cyrillic misses are mostly translations, not transliterations**: `Объединенная Двигателестроительная Корпорация` is
listed in Latin only as `United Engine Corporation`, `Управление Делами Президента` as a translated English title.
Transliteration can't connect those, by the decision not to translate. The rest are short or packed names.

### What the evaluation changed

Fixed because the evaluation found them (golden tests unchanged, all passing, regression tests added):

- **Initials by their Latin letter:** `Y.` didn't match `Yuryevich` and `K.` didn't match `Khasan`, because initials were
  compared only by fold key (which turns `y` into `i` and `kh` into `h`). They now also match the start of the name's
  own Latin form, and initials are assigned to given names one-to-one at best (a greedy pass could let `K` take
  `Khasan` and leave `H` with nothing, reading it as a contradiction).
- **Typos in long generic words:** `Shippig`, `Partnes` and `Sceintific` became distinctive words. A token one edit
  from a generic word of 7+ letters (same first letter) is now that generic word.
- **`liability` is a legal word,** so a mangled "Limited Liability Company" doesn't make it distinctive.
- **Packed aliases:** Canada separates aliases with semicolons as well as commas (221 records), and the UK joins two
  Cyrillic spellings with ` / `. Both are now split into separate names.

## Calibration: what a percentage meant on this test set

Each query's best candidate, by its percentage band, and how often it was the right subject:

| Percent | Best candidates | Right |
|---|---|---|
| 0-19 | 80 | 7.5% |
| 20-49 | 812 | 21.1% |
| 50-59 | 7 | 57.1% |
| 60-69 | 118 | 71.2% |
| 70-79 | 1,679 | 95.0% |
| 80-89 | 8 | 100.0% |
| 90-94 | 459 | 98.5% |
| 95-99 | 3,493 | 98.1% |

- **Match (90-99) is trustworthy**: right about 98% of the time.
- **60-69 is about right**: 71%.
- **70-79 is understated** on this set: right 95% of the time. Those are mostly the fixed 70% for contained names and
  two initials, and the 75% caps.
- But the set is about 90% positives, because it's built from list names. Real screening is the other way round:
  most shipments involve no listed party. With a realistic share of unlisted inputs, the same 70% results would be
  right far less often. So "about 70%" can't be calibrated from this set alone without choosing a base rate.

## The decision (owner)

The mapping is **unchanged**. Options:

1. **Keep it** (recommended for the demo). Review percentages are conservative on list-derived inputs, never
   overstated, and every golden test holds. The label already says it's an estimate, not a probability.
2. **Raise the 70-79 values to match this set** (contained names and two initials 70% → 85%, capped methods
   75% → 85%). Outcomes don't change (all stay in Review), but on real traffic, mostly unlisted names, they would
   overstate.
3. **Lower common-surname initials** (spec 5.6's open question): for a surname shared by more than N listed people,
   one initial 60% → 50%. That reduces Review noise for `Ivanov И.`-style inputs; the right person still shows, at a
   lower percentage.

Calibrating must not break a golden test without your approval: none of these does, since every option keeps each
golden row's outcome.
