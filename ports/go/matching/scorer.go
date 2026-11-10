package matching

import (
	"fmt"
	"math"
	"sort"
	"strings"
)

// WordAlignment is one word of a name alignment, without the entered text.
type WordAlignment struct {
	Entered *int    `json:"entered"`
	Listed  *string `json:"listed"`
	How     string  `json:"how"`
}

func intPtr(v int) *int       { return &v }
func strPtr(v string) *string { return &v }

type nameScoreDetails struct {
	Explanation []string
	Words       []WordAlignment
	Notes       []string
}

// nameScore is a name's percentage against one listed alias, with its explanation (built the first time it's read).
type nameScore struct {
	Percent int
	Ceiling int
	explain func() nameScoreDetails
	details *nameScoreDetails
}

func (s *nameScore) Details() nameScoreDetails {
	if s.details == nil {
		d := s.explain()
		s.details = &d
	}
	return *s.details
}

// scoreValue is a name's percentage against one listed alias and the lowest cap on it: all a search needs to rank
// the aliases. Explain gives the explanation of the few it reports.
type scoreValue struct {
	Percent int
	Ceiling int
}

// evaluation is what scoring a name decided, kept to explain it: Score reuses the scorer's, Explain makes its own.
type evaluation struct {
	zero      string     // nothing to compare, and why
	initials  *nameScore // the initials reading won
	pairs     []pair
	caps      []scoreCap
	percent   int // before the caps
	contained bool
}

type scoreCap struct {
	Cap    int
	Reason string
}

type pair struct {
	Query, Listed int
	Alignment     alignment
}

// nameScorer scores names for one search, on one goroutine: its buffers are reused from one alias to the next.
type nameScorer struct {
	comparer *tokenComparer
	options  *Options
	scratch  evaluation
	cells    []alignment
	values   []float64
	usable   []bool
	columns  []int
	best     []float64
}

// Score scores query against one listed alias. Nothing it computes outlives the call: it's the inner loop.
func (s *nameScorer) Score(query, listed *nameForm, weakAlias bool, weight func(*Token) float64) scoreValue {
	e := &s.scratch
	e.zero, e.initials, e.pairs, e.caps, e.percent, e.contained = "", nil, e.pairs[:0], e.caps[:0], 0, false
	return s.evaluate(query, listed, weakAlias, weight, e, false)
}

// Explain scores query against the alias again, the same way, and explains the result.
func (s *nameScorer) Explain(query, listed *nameForm, weakAlias bool, weight func(*Token) float64) nameScoreDetails {
	e := &evaluation{}
	s.evaluate(query, listed, weakAlias, weight, e, true)
	if e.zero != "" {
		return nameScoreDetails{[]string{e.zero}, []WordAlignment{}, []string{e.zero}}
	}
	if e.initials != nil {
		return e.initials.Details()
	}

	pairs := e.pairs
	pairedQuery := make([]bool, len(query.Core))
	pairedListed := make([]bool, len(listed.Core))
	for _, p := range pairs {
		pairedQuery[p.Query] = true
		pairedListed[p.Listed] = true
	}
	explanation := []string{}
	words := []WordAlignment{}
	for _, p := range pairs {
		explanation = append(explanation, describe(query.Core[p.Query], listed.Core[p.Listed], p.Alignment))
		words = append(words, WordAlignment{intPtr(p.Query + 1), strPtr(listed.Core[p.Listed].Text), how(p.Alignment.Method)})
	}
	for i, t := range query.Core {
		if !pairedQuery[i] {
			explanation = append(explanation, t.Text+": not in the listed name")
		}
	}
	for j, t := range listed.Core {
		if !pairedListed[j] {
			explanation = append(explanation, t.Text+": listed, not entered")
		}
	}
	for i := range query.Core {
		if !pairedQuery[i] {
			words = append(words, WordAlignment{intPtr(i + 1), nil, "NotListed"})
		}
	}
	for j, t := range listed.Core {
		if !pairedListed[j] {
			words = append(words, WordAlignment{nil, strPtr(t.Text), "NotEntered"})
		}
	}
	if e.contained {
		explanation = append(explanation, "One name contains the other")
	}
	for _, legal := range unionStrings(query.LegalClasses, listed.LegalClasses) {
		explanation = append(explanation, "Legal form "+legal+" not compared")
	}
	notes := capNotes(e.percent, e.caps)
	return nameScoreDetails{append(explanation, notes...), words, notes}
}

func (s *nameScorer) evaluate(query, listed *nameForm, weakAlias bool, weight func(*Token) float64, e *evaluation, explain bool) scoreValue {
	o := s.options
	if query.LegalOnly {
		e.zero = "Legal form only: nothing distinctive to compare"
		return scoreValue{0, 0}
	}
	if query.IsEmpty() || listed.IsEmpty() {
		e.zero = "Nothing distinctive to compare"
		return scoreValue{0, 0}
	}

	hasInitials := query.IsPerson && listed.IsPerson && (len(query.Initials) > 0 || len(listed.Initials) > 0)
	if hasInitials {
		// The two readings in order; the first of equals wins.
		var best *nameScore
		if len(query.Initials) > 0 {
			if reading := s.scoreInitials(query, listed, weakAlias, true, explain); reading != nil {
				best = reading
			}
		}
		if len(listed.Initials) > 0 {
			if reading := s.scoreInitials(listed, query, weakAlias, false, explain); reading != nil && (best == nil || reading.Percent > best.Percent) {
				best = reading
			}
		}
		if best != nil {
			e.initials = best
			return scoreValue{best.Percent, best.Ceiling}
		}
	}

	e.pairs = s.align(query.Core, listed.Core, weight, e.pairs[:0])
	pairs := e.pairs
	aligned := 0.0
	for _, p := range pairs {
		aligned += p.Alignment.Similarity * (weight(query.Core[p.Query]) + weight(listed.Core[p.Listed]))
	}
	total := 0.0
	for _, t := range query.Core {
		total += weight(t)
	}
	listedTotal := 0.0
	for _, t := range listed.Core {
		listedTotal += weight(t)
	}
	total += listedTotal
	percent := toPercent(aligned/total, o)

	// Every pair holds one query word and one listed word, each at most once: a side is all aligned when it has as
	// many words as there are pairs.
	unaligned := len(pairs) < len(query.Core) || len(pairs) < len(listed.Core)
	shorter := min(len(query.Core), len(listed.Core))
	contained := shorter >= 2 && len(pairs) == shorter && len(query.Core) != len(listed.Core)
	if contained {
		for _, p := range pairs {
			m := p.Alignment.Method
			if m != MethodIdentical && m != MethodTransliteration && m != MethodSpellingPair && m != MethodFoldKey {
				contained = false
				break
			}
		}
	}
	caps := e.caps
	if contained {
		percent = o.ContainedNamePercent
	} else if unaligned {
		caps = append(caps, scoreCap{o.NotAllAlignedCap, "Not every distinctive word aligned"})
	}

	if !query.IsPerson && !listed.IsPerson && len(query.Core) == 1 && len(listed.Core) == 1 && len(pairs) == 1 &&
		query.Core[0].Len() < 4 && !intersects(query.LegalClasses, listed.LegalClasses) {
		reason := "Short name with a different legal form"
		if len(query.LegalClasses) == 0 {
			reason = "Short name with no legal form"
		}
		caps = append(caps, scoreCap{o.ThinCoreCap, reason})
	}
	if !query.IsPerson && !listed.IsPerson && !equalStrings(query.Generic, listed.Generic) {
		caps = append(caps, scoreCap{o.GenericCap, "Generic words differ"})
	}
	if weakAlias {
		caps = append(caps, scoreCap{o.WeakAliasCap, "The list marks this alias as low quality"})
	}
	for _, p := range pairs {
		if p.Alignment.Capped() {
			caps = append(caps, scoreCap{o.CappedMethodCap, "A word aligned only approximately"})
			break
		}
	}
	if hasInitials {
		caps = append(caps, scoreCap{o.NotAllAlignedCap, "Initials couldn't be checked against given names"})
		unalignedQuery, unalignedListed := unpaired(query.Core, pairs, true), unpaired(listed.Core, pairs, false)
		if s.contradicts(query.Initials, concatTokens(unalignedListed, listed.Initials)) || s.contradicts(listed.Initials, concatTokens(unalignedQuery, query.Initials)) {
			caps = append(caps, scoreCap{o.ContradictingInitialsPercent, "An initial contradicts a given name or initial"})
		}
	}
	e.caps, e.percent, e.contained = caps, percent, contained

	ceiling := math.MaxInt32
	for _, c := range caps {
		ceiling = min(ceiling, c.Cap)
	}
	return scoreValue{min(percent, ceiling), ceiling}
}

// unpaired is the tokens of one side (the query's, or the listed name's) that no pair holds, in order.
func unpaired(tokens []*Token, pairs []pair, querySide bool) []*Token {
	var out []*Token
	for i, t := range tokens {
		found := false
		for _, p := range pairs {
			if (querySide && p.Query == i) || (!querySide && p.Listed == i) {
				found = true
				break
			}
		}
		if !found {
			out = append(out, t)
		}
	}
	return out
}

// capNotes says which caps lowered percent, lowest first (a stable sort: equal caps keep their order).
func capNotes(percent int, caps []scoreCap) []string {
	var applied []scoreCap
	for _, c := range caps {
		if c.Cap < percent {
			applied = append(applied, c)
		}
	}
	sort.SliceStable(applied, func(i, j int) bool { return applied[i].Cap < applied[j].Cap })
	notes := []string{}
	for _, c := range applied {
		notes = append(notes, fmt.Sprintf("Capped at %d%%: %s", c.Cap, c.Reason))
	}
	return notes
}

func concatTokens(a, b []*Token) []*Token {
	out := make([]*Token, 0, len(a)+len(b))
	return append(append(out, a...), b...)
}

func (s *nameScorer) contradicts(initials []*Token, candidates []*Token) bool {
	matches := s.matchInitials(initials, candidates)
	return len(matches) < len(initials) && len(matches) < len(candidates)
}

// matchInitials is the largest one-to-one assignment of initials to candidates (Kuhn's augmenting paths): initial
// index → candidate index.
func (s *nameScorer) matchInitials(initials []*Token, candidates []*Token) map[int]int {
	fits := make([][]bool, len(initials))
	for i, initial := range initials {
		fits[i] = make([]bool, len(candidates))
		for c, candidate := range candidates {
			fits[i][c] = s.initialMatches(initial, candidate)
		}
	}
	owner := make([]int, len(candidates))
	for i := range owner {
		owner[i] = -1
	}
	var augment func(initial int, seen []bool) bool
	augment = func(initial int, seen []bool) bool {
		for c := range candidates {
			if fits[initial][c] && !seen[c] {
				seen[c] = true
				if owner[c] < 0 || augment(owner[c], seen) {
					owner[c] = initial
					return true
				}
			}
		}
		return false
	}
	for i := range initials {
		augment(i, make([]bool, len(candidates)))
	}
	matches := map[int]int{}
	for c := range candidates {
		if owner[c] >= 0 {
			matches[owner[c]] = c
		}
	}
	return matches
}

// scoreInitials reads one side's initials against the other's given names; the explanation only when explain.
func (s *nameScorer) scoreInitials(initialsSide, otherSide *nameForm, weakAlias bool, queryHasInitials bool, explain bool) *nameScore {
	o := s.options
	if len(initialsSide.Core) == 0 {
		return nil
	}
	pairs := s.align(initialsSide.Core, otherSide.Core, unitWeight, nil)
	anyLong := false
	for _, p := range pairs {
		if initialsSide.Core[p.Query].Len() >= 4 {
			anyLong = true
		}
	}
	if len(pairs) != len(initialsSide.Core) || !anyLong {
		return nil
	}

	explanation := []string{}
	words := []WordAlignment{}
	pairedOther := map[int]bool{}
	for _, p := range pairs {
		pairedOther[p.Listed] = true
		if !explain {
			continue
		}
		if queryHasInitials {
			explanation = append(explanation, describe(initialsSide.Core[p.Query], otherSide.Core[p.Listed], p.Alignment))
			words = append(words, WordAlignment{intPtr(p.Query + 1), strPtr(otherSide.Core[p.Listed].Text), how(p.Alignment.Method)})
		} else {
			explanation = append(explanation, describe(otherSide.Core[p.Listed], initialsSide.Core[p.Query], p.Alignment))
			words = append(words, WordAlignment{intPtr(p.Listed + 1), strPtr(initialsSide.Core[p.Query].Text), how(p.Alignment.Method)})
		}
	}
	var unpaired []int
	for i := range otherSide.Core {
		if !pairedOther[i] {
			unpaired = append(unpaired, i)
		}
	}
	var candidates []*Token
	for _, i := range unpaired {
		candidates = append(candidates, otherSide.Core[i])
	}
	candidates = append(candidates, otherSide.Initials...)
	candidatePosition := func(candidate int) *int {
		if candidate < len(unpaired) {
			return intPtr(unpaired[candidate] + 1)
		}
		return nil
	}
	matches := s.matchInitials(initialsSide.Initials, candidates)
	matchedCandidates := map[int]bool{}
	for _, c := range matches {
		matchedCandidates[c] = true
	}
	var leftOver []*Token
	for c, candidate := range candidates {
		if !matchedCandidates[c] {
			leftOver = append(leftOver, candidate)
		}
	}
	matched, contradicted := len(matches), 0
	for i, initialToken := range initialsSide.Initials {
		c, ok := matches[i]
		if !ok && len(leftOver) > 0 {
			contradicted++
		}
		if !explain {
			continue
		}
		initial := toUpperInvariant(initialToken.Text)
		if ok {
			explanation = append(explanation, fmt.Sprintf("Initial %s matches %s", initial, candidates[c].Text))
			if queryHasInitials {
				words = append(words, WordAlignment{nil, strPtr(candidates[c].Text), "Initial"})
			} else {
				words = append(words, WordAlignment{candidatePosition(c), strPtr(initialToken.Text), "Initial"})
			}
		} else if len(leftOver) > 0 {
			names := make([]string, len(leftOver))
			for k, t := range leftOver {
				names[k] = t.Text
			}
			explanation = append(explanation, fmt.Sprintf("Initial %s doesn't match %s", initial, strings.Join(names, ", ")))
			var listedText *string
			if !queryHasInitials {
				listedText = strPtr(initialToken.Text)
			}
			words = append(words, WordAlignment{nil, listedText, "InitialContradicts"})
		} else {
			explanation = append(explanation, fmt.Sprintf("Initial %s: nothing to compare with", initial))
			var listedText *string
			if !queryHasInitials {
				listedText = strPtr(initialToken.Text)
			}
			words = append(words, WordAlignment{nil, listedText, "InitialUnchecked"})
		}
	}

	percent := o.UncheckedInitialsPercent
	switch {
	case contradicted > 0:
		percent = o.ContradictingInitialsPercent
	case matched >= 2:
		percent = o.TwoInitialsPercent
	case matched == 1:
		percent = o.OneInitialPercent
	}
	var caps []scoreCap
	if weakAlias {
		caps = append(caps, scoreCap{o.WeakAliasCap, "The list marks this alias as low quality"})
	}
	for _, p := range pairs {
		if p.Alignment.Capped() {
			caps = append(caps, scoreCap{o.CappedMethodCap, "A word aligned only approximately"})
			break
		}
	}
	return finish(percent, caps, func() ([]string, []WordAlignment) { return explanation, words })
}

// initialMatches: an initial matches a given name whose fold key starts with the initial's, directly or through a
// cognate, or the same initial on the other side.
func (s *nameScorer) initialMatches(initial, candidate *Token) bool {
	starts := func(names []string, prefixes []string) bool {
		for _, name := range names {
			for _, prefix := range prefixes {
				if prefix != "" && strings.HasPrefix(name, prefix) {
					return true
				}
			}
		}
		return false
	}
	if candidate.Len() == 1 {
		return intersects(initial.Folds, candidate.Folds) || intersects(initial.Forms, candidate.Forms)
	}
	names := append([]string{}, candidate.Folds...)
	for _, group := range s.comparer.CognateGroups(candidate) {
		names = append(names, s.comparer.CognateFoldsOf(group)...)
	}
	return starts(candidate.Forms, initial.Forms) || starts(names, initial.Folds)
}

func finish(percent int, caps []scoreCap, explain func() ([]string, []WordAlignment)) *nameScore {
	ceiling := math.MaxInt32
	for _, c := range caps {
		ceiling = min(ceiling, c.Cap)
	}
	return &nameScore{Percent: min(percent, ceiling), Ceiling: ceiling, explain: func() nameScoreDetails {
		explanation, words := explain()
		notes := capNotes(percent, caps)
		return nameScoreDetails{append(explanation, notes...), words, notes}
	}}
}

func unitWeight(*Token) float64 { return 1.0 }

const maxExactTokens = 12

// align appends the best one-to-one pairs to out: the highest total weighted similarity, and among equal totals the
// fewest capped alignments (exact up to 12 tokens a side, greedy beyond). Its buffers are the scorer's.
func (s *nameScorer) align(query, listed []*Token, weight func(*Token) float64, out []pair) []pair {
	n := len(listed)
	s.cells = grow(s.cells, len(query)*n)
	s.values = grow(s.values, len(query)*n)
	s.usable = grow(s.usable, n)
	alignments, values, usable := s.cells, s.values, s.usable
	clear(usable)
	for i := range query {
		for j := range listed {
			cell := i*n + j
			alignments[cell] = s.comparer.Compare(query[i], listed[j])
			if alignments[cell].Aligned() {
				capped := 0.0
				if alignments[cell].Capped() {
					capped = 1e-9
				}
				values[cell] = alignments[cell].Similarity*(weight(query[i])+weight(listed[j])) - capped
			} else {
				values[cell] = negativeInfinity
			}
			usable[j] = usable[j] || alignments[cell].Aligned()
		}
	}
	columns := s.columns[:0]
	for j := range listed {
		if usable[j] {
			columns = append(columns, j)
		}
	}
	s.columns = columns
	if n <= maxExactTokens && len(query) <= maxExactTokens {
		masks := 1 << len(columns)
		s.best = grow(s.best, (len(query)+1)*masks)
		return exactAssignment(values, len(query), n, columns, s.best, alignments, out)
	}
	for _, a := range greedyAssignment(values, len(query), n) {
		out = append(out, pair{a[0], a[1], alignments[a[0]*n+a[1]]})
	}
	return out
}

// grow returns buffer with length n, reallocated only when it's too small; the caller sets what it reads.
func grow[T any](buffer []T, n int) []T {
	if cap(buffer) < n {
		return make([]T, n, max(n, 2*cap(buffer)))
	}
	return buffer[:n]
}

// exactAssignment appends the optimal pairs to out; best is a buffer of (queryCount+1) << len(columns) values.
func exactAssignment(values []float64, queryCount, listedCount int, columns []int, best []float64, alignments []alignment, out []pair) []pair {
	masks := 1 << len(columns)
	clear(best[queryCount*masks:])
	for i := queryCount - 1; i >= 0; i-- {
		for mask := 0; mask < masks; mask++ {
			value := best[(i+1)*masks+mask]
			for c, column := range columns {
				v := values[i*listedCount+column]
				if mask&(1<<c) == 0 && !math.IsInf(v, -1) {
					value = math.Max(value, v+best[(i+1)*masks+(mask|1<<c)])
				}
			}
			best[i*masks+mask] = value
		}
	}
	used := 0
	for i := 0; i < queryCount; i++ {
		for c, column := range columns {
			v := values[i*listedCount+column]
			if used&(1<<c) == 0 && !math.IsInf(v, -1) && math.Abs(v+best[(i+1)*masks+(used|1<<c)]-best[i*masks+used]) < 1e-12 {
				out = append(out, pair{i, column, alignments[i*listedCount+column]})
				used |= 1 << c
				break
			}
		}
	}
	return out
}

func greedyAssignment(values []float64, queryCount, listedCount int) [][2]int {
	type candidate struct {
		q, l  int
		value float64
	}
	var candidates []candidate
	for i := 0; i < queryCount; i++ {
		for j := 0; j < listedCount; j++ {
			if v := values[i*listedCount+j]; !math.IsInf(v, -1) {
				candidates = append(candidates, candidate{i, j, v})
			}
		}
	}
	sort.SliceStable(candidates, func(a, b int) bool { return candidates[a].value > candidates[b].value })
	usedQuery, usedListed := map[int]bool{}, map[int]bool{}
	var pairs [][2]int
	for _, c := range candidates {
		if !usedQuery[c.q] && !usedListed[c.l] {
			usedQuery[c.q], usedListed[c.l] = true, true
			pairs = append(pairs, [2]int{c.q, c.l})
		}
	}
	return pairs
}

func describe(query, listed *Token, a alignment) string {
	q, l := query.Text, listed.Text
	switch a.Method {
	case MethodIdentical:
		return q + " = " + l
	case MethodTransliteration:
		return q + " read as " + common(query, listed) + " = " + l
	case MethodSpellingPair:
		return q + " ~ " + l + " (spelling variant)"
	case MethodFoldKey:
		return q + " ~ " + l + " (romanisation)"
	case MethodFoldKeyLatin:
		return q + " ~ " + l + " (romanisation, both Latin)"
	case MethodCognate:
		return q + " ~ " + l + " (Russian and Ukrainian forms of one name)"
	case MethodOneEdit, MethodOneEditShort:
		return q + " ~ " + l + " (one letter differs)"
	case MethodTwoEdits:
		return q + " ~ " + l + " (two letters differ)"
	}
	return q + " ≠ " + l
}

func how(method AlignMethod) string {
	switch method {
	case MethodIdentical:
		return "Same"
	case MethodTransliteration:
		return "Transliterated"
	case MethodSpellingPair:
		return "SpellingVariant"
	case MethodFoldKey, MethodFoldKeyLatin:
		return "Romanisation"
	case MethodCognate:
		return "Cognate"
	case MethodOneEdit, MethodOneEditShort:
		return "OneLetterDiffers"
	case MethodTwoEdits:
		return "TwoLettersDiffer"
	}
	return "Different"
}

func common(query, listed *Token) string {
	for _, form := range query.Forms {
		if contains(listed.Forms, form) {
			return form
		}
	}
	if len(query.Forms) > 0 {
		return query.Forms[0]
	}
	return query.Text
}

func intersects(a, b []string) bool {
	for _, x := range a {
		if contains(b, x) {
			return true
		}
	}
	return false
}

func equalStrings(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func unionStrings(a, b []string) []string {
	var out []string
	for _, list := range [][]string{a, b} {
		for _, x := range list {
			if !contains(out, x) {
				out = append(out, x)
			}
		}
	}
	return out
}
