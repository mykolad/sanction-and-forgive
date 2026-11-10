package matching

import (
	"cmp"
	"math"
	"slices"
	"sort"
	"strconv"
	"strings"
	"sync"
)

// SubjectKind says what a list says a subject is; Unknown subjects are matched both ways.
type SubjectKind int

const (
	Individual SubjectKind = iota
	Entity
	Unknown
)

type ListedName struct {
	FullName string
	IsWeak   bool
}

type ListedAddress struct {
	Raw         string
	CountryCode string
}

// ListedSubject is a listed party as the matcher sees it; Names includes the primary name.
type ListedSubject struct {
	ExternalID  string
	Kind        SubjectKind
	PrimaryName string
	Names       []ListedName
	Addresses   []ListedAddress
	Programme   *string
	SourceURL   *string
}

// SubjectMatch is one listed subject's match for one entry: the best of its names.
type SubjectMatch struct {
	Subject      *ListedSubject
	Percent      int
	Outcome      Outcome
	Alias        string
	Alignment    []string
	AddressState AddressState
	Words        []WordAlignment
	Notes        []string
}

type aliasEntry struct {
	Subject int
	Name    string
	IsWeak  bool
	Form    *nameForm
}

// ListIndex is one list's in-memory index (section 5.5). Immutable.
type ListIndex struct {
	data                 *MatcherData
	subjects             []*ListedSubject
	aliases              []aliasEntry
	addresses            [][]*addressForm
	idf                  map[string]float64 // by token key: ln(aliases / aliases with the key), the weight before its floor
	aliasesByFold        map[string][]int32
	nameVocabulary       *fuzzyVocabulary
	subjectsByAddressKey map[string][]int32
	addressVocabulary    *fuzzyVocabulary
	memo                 sync.Map
	states               sync.Pool // *searchState
}

// searchState is one name search's working memory, kept between searches of the index: votes by alias and the best
// alias by subject, in arrays the size of the index, reset after use through what was touched.
type searchState struct {
	votes []int32 // by alias
	voted []int   // the aliases voted for, in the order of their first vote
	slot  []int32 // by subject: 1 + its place in bests, 0 for none yet
	bests []bestAlias
}

type bestAlias struct {
	subject int
	alias   int
	score   scoreValue
}

func (x *ListIndex) borrowState() *searchState {
	if state, ok := x.states.Get().(*searchState); ok {
		return state
	}
	return &searchState{votes: make([]int32, len(x.aliases)), slot: make([]int32, len(x.subjects))}
}

// releaseState returns a state whose votes and slots are all back to zero.
func (x *ListIndex) releaseState(state *searchState) {
	for _, b := range state.bests {
		state.slot[b.subject] = 0
	}
	state.bests = state.bests[:0]
	x.states.Put(state)
}

// Memo returns what build makes for key, built once per index: a cache that goes when the index goes (the API's
// browse order, as the reference's ConditionalWeakTable keyed by the index).
func (x *ListIndex) Memo(key string, build func() any) any {
	if value, ok := x.memo.Load(key); ok {
		return value
	}
	value, _ := x.memo.LoadOrStore(key, build())
	return value
}

func (x *ListIndex) Subjects() []*ListedSubject { return x.subjects }
func (x *ListIndex) AliasCount() int            { return len(x.aliases) }

// orderedPostings is a map that remembers the order its keys were first added: the vocabularies are built in the
// reference's dictionary order, so the fuzzy lookups enumerate candidates in the same order.
type orderedPostings struct {
	keys []string
	ids  map[string][]int32
}

func newPostings() *orderedPostings { return &orderedPostings{ids: map[string][]int32{}} }

func (p *orderedPostings) post(key string, id int) {
	list, ok := p.ids[key]
	if !ok {
		p.keys = append(p.keys, key)
	}
	if len(list) == 0 || list[len(list)-1] != int32(id) {
		p.ids[key] = append(list, int32(id))
	}
}

// pack copies postings into one array, each list exactly its length: an index keeps them for its lifetime, without
// the room appending left.
func pack(postings map[string][]int32) map[string][]int32 {
	total := 0
	for _, list := range postings {
		total += len(list)
	}
	arena := make([]int32, 0, total)
	packed := make(map[string][]int32, len(postings))
	for k, list := range postings {
		start := len(arena)
		arena = append(arena, list...)
		packed[k] = arena[start:len(arena):len(arena)]
	}
	return packed
}

// Build indexes subjects.
func Build(subjects []*ListedSubject) *ListIndex {
	data := Data()
	tok := &tokenizer{data: data, cache: map[string]*Token{}}
	parser := &nameParser{data: data}
	addressParser := &addressParser{data: data, tokenizer: tok}

	var aliases []aliasEntry
	addresses := make([][]*addressForm, len(subjects))
	for s, subject := range subjects {
		names := append(append([]ListedName{}, subject.Names...), ListedName{subject.PrimaryName, false})
		seen := map[string]bool{}
		for _, name := range names {
			if seen[name.FullName] {
				continue
			}
			seen[name.FullName] = true
			tokens := tok.Tokenize(name.FullName, true)
			if subject.Kind != Individual {
				if form := parser.Entity(tokens); !form.IsEmpty() {
					aliases = append(aliases, aliasEntry{s, name.FullName, name.IsWeak, form})
				}
			}
			if subject.Kind != Entity {
				if form := parser.Person(tokens); !form.IsEmpty() {
					aliases = append(aliases, aliasEntry{s, name.FullName, name.IsWeak, form})
				}
			}
		}
		for _, address := range subject.Addresses {
			addresses[s] = append(addresses[s], addressParser.Parse(address.Raw, address.CountryCode))
		}
	}

	documentFrequency := map[string]int{}
	byFold := newPostings()
	for a, alias := range aliases {
		for _, token := range distinctTokens(alias.Form.Core) {
			documentFrequency[token.Key()]++
			for _, k := range lookupKeys(token) {
				byFold.post(k, a)
			}
		}
	}
	byAddress := newPostings()
	for s := range subjects {
		for _, address := range addresses[s] {
			for _, k := range addressKeys(address) {
				byAddress.post(k, s)
			}
		}
	}
	// The logarithm once per key here, not once per search.
	idf := make(map[string]float64, len(documentFrequency))
	for k, frequency := range documentFrequency {
		idf[k] = ln(float64(max(len(aliases), 1)) / float64(max(frequency, 1)))
	}
	var addressVocabulary []string
	for _, k := range byAddress.keys {
		if !strings.HasPrefix(k, "#") {
			addressVocabulary = append(addressVocabulary, k)
		}
	}
	return &ListIndex{
		data: data, subjects: subjects, aliases: aliases, addresses: addresses, idf: idf,
		aliasesByFold: pack(byFold.ids), nameVocabulary: newFuzzyVocabulary(byFold.keys),
		subjectsByAddressKey: pack(byAddress.ids), addressVocabulary: newFuzzyVocabulary(addressVocabulary),
	}
}

func distinctTokens(tokens []*Token) []*Token {
	var out []*Token
	for _, t := range tokens {
		found := false
		for _, o := range out {
			if o == t {
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

func lookupKeys(token *Token) []string {
	if len(token.Folds) > 0 {
		return token.Folds
	}
	return []string{token.Text}
}

func addressKeys(address *addressForm) []string {
	var out []string
	for _, word := range address.Words {
		out = append(out, lookupKeys(word)...)
	}
	for _, p := range address.Places {
		out = append(out, "#place:"+strconv.Itoa(p))
	}
	return out
}

// preparedName is an entry parsed once per request, then searched in every list.
type preparedName struct {
	Entity  *nameForm
	Person  *nameForm
	Address *addressForm
}

// orderedVotes counts votes per id, remembering the order ids were first voted for (the reference's dictionary
// order): ties in the stable sort that follows keep that order.
type orderedVotes struct {
	order []int
	count map[int]int
}

func newVotes() *orderedVotes { return &orderedVotes{count: map[int]int{}} }

func (v *orderedVotes) add(id, weight int) {
	if _, ok := v.count[id]; !ok {
		v.order = append(v.order, id)
	}
	v.count[id] += weight
}

// top returns the ids with the most votes first (stable), at most limit of them.
func (v *orderedVotes) top(limit int) []int {
	ids := append([]int{}, v.order...)
	sort.SliceStable(ids, func(a, b int) bool { return v.count[ids[a]] > v.count[ids[b]] })
	if len(ids) > limit {
		ids = ids[:limit]
	}
	return ids
}

func (x *ListIndex) weight(token *Token, o *Options) float64 {
	idf, ok := x.idf[token.Key()]
	if !ok {
		idf = ln(float64(max(len(x.aliases), 1))) // in no alias: as in one
	}
	return math.Max(o.MinTokenWeight, idf)
}

// candidates returns the aliases with the most votes first (a stable sort: ties in the order of their first vote, the
// reference's dictionary order), at most CandidateLimit.
func (x *ListIndex) candidates(query *preparedName, comparer *tokenComparer, state *searchState, o *Options) []int {
	votes := state.votes
	vote := func(k string, weight int) {
		for _, alias := range x.aliasesByFold[k] {
			if votes[alias] == 0 {
				state.voted = append(state.voted, int(alias))
			}
			votes[alias] += int32(weight)
		}
	}
	for _, token := range distinctTokens(concatTokens(query.Entity.Core, query.Person.Core)) {
		for _, k := range lookupKeys(token) {
			vote(k, 3)
		}
		for _, group := range comparer.CognateGroups(token) {
			for _, fold := range comparer.CognateFoldsOf(group) {
				vote(fold, 2)
			}
		}
		for _, form := range token.Forms {
			for _, spelling := range x.data.SpellingMembers(form) {
				vote(FoldKey(spelling), 2)
			}
		}
		var near []string
		for _, fold := range token.Folds {
			for _, other := range x.nameVocabulary.Near(fold) {
				if !contains(near, other) {
					near = append(near, other)
				}
			}
		}
		for _, other := range near {
			vote(other, 1)
		}
	}
	ids := slices.Clone(state.voted)
	slices.SortStableFunc(ids, func(a, b int) int { return cmp.Compare(votes[b], votes[a]) })
	for _, alias := range state.voted {
		votes[alias] = 0
	}
	state.voted = state.voted[:0]
	if len(ids) > o.CandidateLimit {
		ids = ids[:o.CandidateLimit]
	}
	return ids
}

type ranked struct {
	subject int
	percent int
	alias   int
	address addressComparison
}

// SearchName returns the subjects a name entry finds, best first (then by name): the best one, whatever its
// percentage, and every other at Review or above.
func (x *ListIndex) searchName(query *preparedName, o *Options) []SubjectMatch {
	comparer := borrowComparer(x.data)
	defer comparer.release()
	scorer := &nameScorer{comparer: comparer, options: o}
	addresses := &addressComparer{comparer: comparer, data: x.data}
	state := x.borrowState()
	defer x.releaseState(state)
	weights := comparer.weights
	weightOf := func(token *Token) float64 {
		w, ok := weights[token]
		if !ok {
			w = x.weight(token, o)
			weights[token] = w
		}
		return w
	}
	// The best alias of each subject (the first of equals), subjects in the order they were first found.
	for _, aliasID := range x.candidates(query, comparer, state, o) {
		alias := &x.aliases[aliasID]
		form := query.Entity
		if alias.Form.IsPerson {
			form = query.Person
		}
		score := scorer.Score(form, alias.Form, alias.IsWeak, weightOf)
		if slot := state.slot[alias.Subject]; slot == 0 {
			state.bests = append(state.bests, bestAlias{alias.Subject, aliasID, score})
			state.slot[alias.Subject] = int32(len(state.bests))
		} else if b := &state.bests[slot-1]; score.Percent > b.score.Percent {
			b.alias, b.score = aliasID, score
		}
	}

	list := make([]ranked, 0, len(state.bests))
	for _, b := range state.bests {
		subjectIndex := b.subject
		address := addresses.Compare(query.Address, x.addresses[subjectIndex])
		percent := b.score.Percent
		if address.State == Conflicting {
			percent = min(percent, o.ConflictingAddressCap)
		} else if address.State == Corroborated {
			percent = min(percent+o.CorroboratedBonus, min(bandMax(percent, o), b.score.Ceiling))
		}
		list = append(list, ranked{subjectIndex, percent, b.alias, address})
	}

	// Best first, then by name (a stable sort). What's reported is every subject at Review or above, or when there's
	// none, the best one: so only those are sorted, and the best is the first of equals.
	order := func(a, b ranked) int {
		if a.percent != b.percent {
			return cmp.Compare(b.percent, a.percent)
		}
		return strings.Compare(x.subjects[a.subject].PrimaryName, x.subjects[b.subject].PrimaryName)
	}
	var reported []ranked
	for _, r := range list {
		if toOutcome(r.percent, o) != NoMatch {
			reported = append(reported, r)
		}
	}
	if len(reported) == 0 && len(list) > 0 {
		best := 0
		for k := 1; k < len(list); k++ {
			if order(list[k], list[best]) < 0 {
				best = k
			}
		}
		reported = list[best : best+1]
	}
	slices.SortStableFunc(reported, order)

	matches := make([]SubjectMatch, 0, len(reported))
	for _, r := range reported {
		alias := &x.aliases[r.alias]
		form := query.Entity
		if alias.Form.IsPerson {
			form = query.Person
		}
		details := scorer.Explain(form, alias.Form, alias.IsWeak, weightOf)
		alignment := append(append([]string{}, details.Explanation...), r.address.Explanation)
		matches = append(matches, SubjectMatch{x.subjects[r.subject], r.percent, toOutcome(r.percent, o), alias.Name, alignment, r.address.State, details.Words, details.Notes})
	}
	return matches
}

// searchAddress is address-only screening: subjects whose address shares at least 3 parts (a number and a word).
func (x *ListIndex) searchAddress(entered *addressForm, o *Options) []SubjectMatch {
	tokens := borrowComparer(x.data)
	defer tokens.release()
	comparer := &addressComparer{comparer: tokens, data: x.data}
	keyList := addressKeys(entered)
	for _, word := range entered.Words {
		for _, fold := range word.Folds {
			keyList = append(keyList, x.addressVocabulary.Near(fold)...)
		}
	}
	votes := newVotes()
	for _, k := range distinctStrings(keyList) {
		for _, subject := range x.subjectsByAddressKey[k] {
			votes.add(int(subject), 1)
		}
	}
	var matches []SubjectMatch
	for _, subject := range votes.top(o.CandidateLimit) {
		var bestAddress *addressForm
		var bestResult addressOnly
		for _, address := range x.addresses[subject] {
			result := comparer.CompareAddressOnly(entered, address)
			if bestAddress == nil || better(result, bestResult) {
				bestAddress, bestResult = address, result
			}
		}
		percent := o.AddressOnlyPercent
		if !bestResult.Matches {
			percent = min(o.NoMatchMaxPercent, bestResult.Aligned*15)
		}
		matches = append(matches, SubjectMatch{x.subjects[subject], percent, toOutcome(percent, o), bestAddress.Raw, []string{bestResult.Explanation}, NotCompared, bestResult.Words, []string{bestResult.Explanation}})
	}
	sort.SliceStable(matches, func(a, b int) bool {
		if matches[a].Percent != matches[b].Percent {
			return matches[a].Percent > matches[b].Percent
		}
		return matches[a].Subject.PrimaryName < matches[b].Subject.PrimaryName
	})
	return matches
}

// better orders (Matches, Aligned) as the reference's tuple comparison does; MaxBy keeps the first of equals.
func better(a, b addressOnly) bool {
	if a.Matches != b.Matches {
		return a.Matches
	}
	return a.Aligned > b.Aligned
}
