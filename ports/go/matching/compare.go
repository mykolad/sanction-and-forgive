package matching

import (
	"math"
	"slices"
	"sort"
	"sync"
	"unicode/utf8"
	"unsafe"
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

// tokenComparer compares two tokens; the first rule that applies wins. One serves one search, on one goroutine, and
// remembers the pairs it compared (by pointer: tokens are interned) in a direct-mapped cache: a pair that lands on
// another's slot is compared again, which gives the same answer.
type tokenComparer struct {
	data    *MatcherData
	pairs   []comparedPair
	written []uint16           // the slots this search wrote, emptied when it ends
	weights map[*Token]float64 // the search's word weights, kept with it (the same index throughout)
}

type comparedPair struct {
	query, listed *Token
	alignment     alignment
}

const pairBits = 12 // 4,096 slots: 2^12 to 2^15 measured the same on the real lists

const pairSlots = 1 << pairBits

func newTokenComparer(data *MatcherData) *tokenComparer {
	return &tokenComparer{data: data, pairs: make([]comparedPair, pairSlots), weights: map[*Token]float64{}}
}

// comparers keeps comparers between searches, so a search starts with its caches' room already made.
var comparers = sync.Pool{New: func() any { return newTokenComparer(nil) }}

func borrowComparer(data *MatcherData) *tokenComparer {
	c := comparers.Get().(*tokenComparer)
	c.data = data
	return c
}

// release forgets the search and returns the comparer to the pool. Every slot it wrote is emptied and the weights
// with it: a pooled comparer holds no token, so nothing entered outlives its screening (input is never stored).
func (c *tokenComparer) release() {
	for _, slot := range c.written {
		c.pairs[slot] = comparedPair{}
	}
	c.written = c.written[:0]
	if len(c.weights) > 1<<12 {
		c.weights = map[*Token]float64{}
	} else {
		clear(c.weights)
	}
	c.data = nil
	comparers.Put(c)
}

func (c *tokenComparer) Compare(query, listed *Token) alignment {
	h := uint64(uintptr(unsafe.Pointer(query)))*0x9E3779B97F4A7C15 ^ uint64(uintptr(unsafe.Pointer(listed)))
	h ^= h >> 31
	h *= 0xBF58476D1CE4E5B9
	index := h >> (64 - pairBits)
	slot := &c.pairs[index]
	if slot.query == query && slot.listed == listed {
		return slot.alignment
	}
	if slot.query == nil {
		c.written = append(c.written, uint16(index))
	}
	a := c.compareOnce(query, listed)
	*slot = comparedPair{query, listed, a}
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
	if overlaps(spellingGroups(query), spellingGroups(listed)) {
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
	if token.groups == nil {
		return nil
	}
	return token.groups.cognates
}

func (c *tokenComparer) CognateFoldsOf(group int) []string { return c.data.CognateFolds[group] }

func spellingGroups(token *Token) []int {
	if token.groups == nil {
		return nil
	}
	return token.groups.spelling
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
	var qBuffer, lBuffer [runeBuffer]rune
	for _, qf := range query.Forms {
		for _, lf := range listed.Forms {
			var a alignment
			if isASCII(qf) && isASCII(lf) {
				// Letters are bytes: the same distances, without decoding.
				a = editAlignment(asBytes(qf), asBytes(lf))
			} else {
				a = editAlignment(appendRunes(qBuffer[:0], qf), appendRunes(lBuffer[:0], lf))
			}
			if a.Aligned() && (!best.Aligned() || a.Method < best.Method || (a.Method == best.Method && a.Similarity > best.Similarity)) {
				best = a
			}
		}
	}
	return best
}

// editAlignment aligns two forms by edits (one or two letters differ), or not at all.
func editAlignment[T byte | rune](q, l []T) alignment {
	shorter := min(len(q), len(l))
	if shorter < 4 {
		return alignment{}
	}
	// A different first letter counts as one more edit, so then a distance of 2 or more already rules it out.
	var edits int
	if q[0] != l[0] {
		edits = damerauLevenshtein(q, l, 2) + 1
	} else {
		edits = damerauLevenshtein(q, l, 3)
	}
	if edits != 1 && edits != 2 {
		return alignment{}
	}
	jw := jaroWinkler(q, l)
	switch {
	case edits == 1 && shorter >= 8:
		return alignment{jw, MethodOneEdit}
	case edits == 1:
		return alignment{jw, MethodOneEditShort}
	case edits == 2 && shorter >= 8 && jw >= 0.90:
		return alignment{jw, MethodTwoEdits}
	}
	return alignment{}
}

func isASCII(value string) bool {
	for i := 0; i < len(value); i++ {
		if value[i] >= utf8.RuneSelf {
			return false
		}
	}
	return true
}

// asBytes is the string's bytes, shared, never written.
func asBytes(value string) []byte { return unsafe.Slice(unsafe.StringData(value), len(value)) }

// runeBuffer is the size of the buffers on the stack that hold a word's letters while it's compared; a longer word
// takes the heap.
const runeBuffer = 48

// appendRunes is append(buffer, []rune(value)...) without the conversion's allocation.
func appendRunes(buffer []rune, value string) []rune {
	for _, c := range value {
		buffer = append(buffer, c)
	}
	return buffer
}

// damerauLevenshtein is the optimal string alignment distance, or limit as soon as it can't be lower. Only the band of
// cells within limit - 1 of the diagonal is computed: a cell further out is at least limit away, so the cells just
// outside the band hold limit, which gives every value under limit exactly and the rest as limit or more.
func damerauLevenshtein[T byte | rune](a, b []T, limit int) int {
	if abs(len(a)-len(b)) >= limit {
		return limit
	}
	width := len(b) + 1
	var rows [3 * (runeBuffer + 1)]int
	var previous2, previous, current []int
	if width <= runeBuffer+1 {
		previous2, previous, current = rows[:width], rows[width:2*width], rows[2*width:3*width]
	} else {
		previous2, previous, current = make([]int, width), make([]int, width), make([]int, width)
	}
	for j := 0; j <= len(b); j++ {
		previous[j] = j
	}
	for i := 1; i <= len(a); i++ {
		low, high := max(1, i-limit+1), min(len(b), i+limit-1)
		current[0] = i
		if low > 1 {
			current[low-1] = limit
		}
		if high < len(b) {
			current[high+1] = limit
		}
		rowMinimum := current[0]
		for j := low; j <= high; j++ {
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

func jaroWinkler[T byte | rune](a, b []T) float64 {
	if len(a) == 0 || len(b) == 0 {
		return 0
	}
	window := max(0, max(len(a), len(b))/2-1)
	var aBuffer, bBuffer [runeBuffer]bool
	var aMatched, bMatched []bool
	if len(a) <= runeBuffer && len(b) <= runeBuffer {
		aMatched, bMatched = aBuffer[:len(a)], bBuffer[:len(b)]
	} else {
		aMatched, bMatched = make([]bool, len(a)), make([]bool, len(b))
	}
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
	byTrigram  map[string][]int32
	seen       sync.Pool // *[]uint64: a bit per fold, all clear between lookups
}

const shortMax = 7

func newFuzzyVocabulary(vocabulary []string) *fuzzyVocabulary {
	v := &fuzzyVocabulary{}
	byTrigram := map[string][]int32{}
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
			byTrigram[trigram] = append(byTrigram[trigram], int32(id))
		}
	}
	v.byTrigram = pack(byTrigram)
	v.deleteKeys = slices.Clone(v.deleteKeys)
	slices.Sort(v.deleteKeys)
	return v
}

// Near returns the vocabulary keys other than fold within two edits of it, in the reference's order.
func (v *fuzzyVocabulary) Near(fold string) []string {
	var buffer, otherBuffer [runeBuffer]rune
	runes := appendRunes(buffer[:0], fold)
	if len(runes) < 4 {
		return nil
	}
	var ids []int
	seen, _ := v.seen.Get().(*[]uint64)
	if seen == nil {
		bits := make([]uint64, (len(v.folds)+63)/64)
		seen = &bits
	}
	defer func() {
		for _, id := range ids {
			(*seen)[id/64] = 0
		}
		v.seen.Put(seen)
	}()
	add := func(id int) {
		if word, bit := &(*seen)[id/64], uint64(1)<<(id%64); *word&bit == 0 {
			*word |= bit
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
			add(int(id))
		}
	}
	var out []string
	ascii := isASCII(fold)
	for _, id := range ids {
		other := v.folds[id]
		if other == fold {
			continue
		}
		var distance int
		if ascii && isASCII(other) {
			distance = damerauLevenshtein(asBytes(fold), asBytes(other), 3)
		} else {
			distance = damerauLevenshtein(runes, appendRunes(otherBuffer[:0], other), 3)
		}
		if distance <= 2 {
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
