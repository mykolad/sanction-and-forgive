package matching

import (
	"math"
	"sort"
)

// AlignMethod says how two tokens aligned (section 5.4), most certain first.
type AlignMethod int

const (
	MethodNone AlignMethod = iota
	MethodIdentical
	MethodTransliteration
	MethodSpellingPair
	MethodFoldKey
	MethodFoldKeyLatin
	MethodCognate
	MethodOneEdit
	MethodOneEditShort
	MethodTwoEdits
)

type alignment struct {
	Similarity float64
	Method     AlignMethod
}

func (a alignment) Aligned() bool { return a.Method != MethodNone }

// Capped: aligned by a method that caps the match (fold key with both sides Latin, cognate, short edits).
func (a alignment) Capped() bool {
	return a.Method == MethodFoldKeyLatin || a.Method == MethodCognate || a.Method == MethodOneEditShort || a.Method == MethodTwoEdits
}

type tokenPair struct{ query, listed *Token }

// tokenComparer compares two tokens; the first rule that applies wins. One serves one search, on one goroutine: each
// pair is compared once and each token's groups looked up once (by pointer: tokens are interned).
type tokenComparer struct {
	data           *MatcherData
	compared       map[tokenPair]alignment
	spellingGroups map[*Token][]int
	cognateGroups  map[*Token][]int
}

func newTokenComparer(data *MatcherData) *tokenComparer {
	return &tokenComparer{data: data, compared: map[tokenPair]alignment{}, spellingGroups: map[*Token][]int{}, cognateGroups: map[*Token][]int{}}
}

func (c *tokenComparer) Compare(query, listed *Token) alignment {
	pair := tokenPair{query, listed}
	if a, ok := c.compared[pair]; ok {
		return a
	}
	a := c.compareOnce(query, listed)
	c.compared[pair] = a
	return a
}

func (c *tokenComparer) compareOnce(query, listed *Token) alignment {
	if query.Text == listed.Text {
		return alignment{1.0, MethodIdentical}
	}
	if query.IsNumber || listed.IsNumber {
		return alignment{}
	}
	for _, form := range query.Forms {
		if contains(listed.Forms, form) {
			return alignment{0.98, MethodTransliteration}
		}
	}
	if overlaps(c.spelling(query), c.spelling(listed)) {
		return alignment{0.98, MethodSpellingPair}
	}
	for _, fold := range query.Folds {
		if contains(listed.Folds, fold) {
			if query.IsCyrillic || listed.IsCyrillic {
				return alignment{0.96, MethodFoldKey}
			}
			return alignment{0.96, MethodFoldKeyLatin}
		}
	}
	if overlaps(c.CognateGroups(query), c.CognateGroups(listed)) {
		return alignment{0.96, MethodCognate}
	}
	return compareEdits(query, listed)
}

// CognateGroups returns the cognate groups a token belongs to, through any of its fold keys, in the order the
// reference's set enumerates them (insertion order).
func (c *tokenComparer) CognateGroups(token *Token) []int {
	if groups, ok := c.cognateGroups[token]; ok {
		return groups
	}
	groups := []int{}
	for _, fold := range token.Folds {
		for _, group := range c.data.CognateGroups[fold] {
			if !containsInt(groups, group) {
				groups = append(groups, group)
			}
		}
	}
	c.cognateGroups[token] = groups
	return groups
}

func (c *tokenComparer) CognateFoldsOf(group int) []string { return c.data.CognateFolds[group] }

func (c *tokenComparer) spelling(token *Token) []int {
	if groups, ok := c.spellingGroups[token]; ok {
		return groups
	}
	groups := []int{}
	for _, form := range token.Forms {
		if group, ok := c.data.SpellingGroups[form]; ok && !containsInt(groups, group) {
			groups = append(groups, group)
		}
	}
	c.spellingGroups[token] = groups
	return groups
}

func overlaps(a, b []int) bool {
	for _, x := range a {
		if containsInt(b, x) {
			return true
		}
	}
	return false
}

func compareEdits(query, listed *Token) alignment {
	best := alignment{}
	rank := func(a alignment) int { return int(a.Method) }
	for _, qf := range query.Forms {
		q := []rune(qf)
		for _, lf := range listed.Forms {
			l := []rune(lf)
			shorter := min(len(q), len(l))
			if shorter < 4 {
				continue
			}
			edits := damerauLevenshtein(q, l, 3)
			if q[0] != l[0] {
				edits++
			}
			if edits != 1 && edits != 2 {
				continue
			}
			jw := jaroWinkler(q, l)
			var a alignment
			switch {
			case edits == 1 && shorter >= 8:
				a = alignment{jw, MethodOneEdit}
			case edits == 1:
				a = alignment{jw, MethodOneEditShort}
			case edits == 2 && shorter >= 8 && jw >= 0.90:
				a = alignment{jw, MethodTwoEdits}
			}
			if a.Aligned() && (!best.Aligned() || rank(a) < rank(best) || (rank(a) == rank(best) && a.Similarity > best.Similarity)) {
				best = a
			}
		}
	}
	return best
}

// damerauLevenshtein is the optimal string alignment distance, or limit as soon as it can't be lower.
func damerauLevenshtein(a, b []rune, limit int) int {
	if abs(len(a)-len(b)) >= limit {
		return limit
	}
	width := len(b) + 1
	previous2 := make([]int, width)
	previous := make([]int, width)
	current := make([]int, width)
	for j := 0; j <= len(b); j++ {
		previous[j] = j
	}
	for i := 1; i <= len(a); i++ {
		current[0] = i
		rowMinimum := current[0]
		for j := 1; j <= len(b); j++ {
			cost := 1
			if a[i-1] == b[j-1] {
				cost = 0
			}
			value := min(previous[j]+1, current[j-1]+1, previous[j-1]+cost)
			if i > 1 && j > 1 && a[i-1] == b[j-2] && a[i-2] == b[j-1] {
				value = min(value, previous2[j-2]+1)
			}
			current[j] = value
			rowMinimum = min(rowMinimum, value)
		}
		if rowMinimum >= limit {
			return limit
		}
		previous2, previous, current = previous, current, previous2
	}
	return min(previous[len(b)], limit)
}

func jaroWinkler(a, b []rune) float64 {
	if len(a) == 0 || len(b) == 0 {
		return 0
	}
	window := max(0, max(len(a), len(b))/2-1)
	aMatched := make([]bool, len(a))
	bMatched := make([]bool, len(b))
	matches := 0
	for i := range a {
		start := max(0, i-window)
		end := min(len(b)-1, i+window)
		for j := start; j <= end; j++ {
			if !bMatched[j] && a[i] == b[j] {
				aMatched[i], bMatched[j] = true, true
				matches++
				break
			}
		}
	}
	if matches == 0 {
		return 0
	}
	transpositions := 0
	for i, j := 0, 0; i < len(a); i++ {
		if !aMatched[i] {
			continue
		}
		for !bMatched[j] {
			j++
		}
		if a[i] != b[j] {
			transpositions++
		}
		j++
	}
	m := float64(matches)
	jaro := ((m / float64(len(a))) + (m / float64(len(b))) + ((m - float64(transpositions)/2.0) / m)) / 3
	prefix := 0
	for prefix < min(4, min(len(a), len(b))) && a[prefix] == b[prefix] {
		prefix++
	}
	return jaro + (float64(prefix) * 0.1 * (1 - jaro))
}

// fuzzyVocabulary finds the fold keys in a vocabulary within two edits of a key: short keys through a delete-one
// index (hashes in one sorted array), longer ones through trigrams; every candidate checked by edit distance.
type fuzzyVocabulary struct {
	folds      []string
	deleteKeys []int64
	byTrigram  map[string][]int
}

const shortMax = 7

func newFuzzyVocabulary(vocabulary []string) *fuzzyVocabulary {
	v := &fuzzyVocabulary{byTrigram: map[string][]int{}}
	seen := map[string]bool{}
	for _, fold := range vocabulary {
		if len([]rune(fold)) >= 4 && !seen[fold] {
			seen[fold] = true
			v.folds = append(v.folds, fold)
		}
	}
	for id, fold := range v.folds {
		if len([]rune(fold)) <= shortMax+1 {
			for _, variant := range deleteVariants(fold) {
				v.deleteKeys = append(v.deleteKeys, int64(fnv(variant))<<32|int64(uint32(id)))
			}
		}
		for _, trigram := range distinctStrings(trigrams(fold)) {
			v.byTrigram[trigram] = append(v.byTrigram[trigram], id)
		}
	}
	sort.Slice(v.deleteKeys, func(i, j int) bool { return v.deleteKeys[i] < v.deleteKeys[j] })
	return v
}

// Near returns the vocabulary keys other than fold within two edits of it, in the reference's order.
func (v *fuzzyVocabulary) Near(fold string) []string {
	runes := []rune(fold)
	if len(runes) < 4 {
		return nil
	}
	var ids []int
	seen := map[int]bool{}
	add := func(id int) {
		if !seen[id] {
			seen[id] = true
			ids = append(ids, id)
		}
	}
	if len(runes) <= shortMax+1 {
		for _, variant := range deleteVariants(fold) {
			hash := int64(fnv(variant)) << 32
			index := sort.Search(len(v.deleteKeys), func(i int) bool { return v.deleteKeys[i] >= hash })
			for ; index < len(v.deleteKeys) && v.deleteKeys[index]&^0xFFFFFFFF == hash; index++ {
				add(int(v.deleteKeys[index] & 0xFFFFFFFF))
			}
		}
	}
	for _, trigram := range distinctStrings(trigrams(fold)) {
		for _, id := range v.byTrigram[trigram] {
			add(id)
		}
	}
	var out []string
	for _, id := range ids {
		other := v.folds[id]
		if other != fold && damerauLevenshtein(runes, []rune(other), 3) <= 2 {
			out = append(out, other)
		}
	}
	return out
}

func deleteVariants(fold string) []string {
	runes := []rune(fold)
	out := []string{fold}
	for i := range runes {
		out = append(out, string(runes[:i])+string(runes[i+1:]))
	}
	return out
}

// fnv is FNV-1a over UTF-16 code units, as the reference computes it (32-bit, wrapping).
func fnv(value string) int32 {
	hash := int32(-2128831035) // 2166136261 as a signed 32-bit integer
	for _, c := range value {
		hash = (hash ^ int32(c)) * 16777619
	}
	return hash
}

func trigrams(value string) []string {
	runes := []rune(value)
	var out []string
	for i := 0; i+3 <= len(runes); i++ {
		out = append(out, string(runes[i:i+3]))
	}
	return out
}

func distinctStrings(values []string) []string {
	var out []string
	for _, v := range values {
		if !contains(out, v) {
			out = append(out, v)
		}
	}
	return out
}

var negativeInfinity = math.Inf(-1)
