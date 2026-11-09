package matching

import (
	"math"
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
	documentFrequency    map[string]int
	aliasesByFold        map[string][]int
	nameVocabulary       *fuzzyVocabulary
	subjectsByAddressKey map[string][]int
	addressVocabulary    *fuzzyVocabulary
	memo                 sync.Map
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
	ids  map[string][]int
}

func newPostings() *orderedPostings { return &orderedPostings{ids: map[string][]int{}} }

func (p *orderedPostings) post(key string, id int) {
	list, ok := p.ids[key]
	if !ok {
		p.keys = append(p.keys, key)
	}
	if len(list) == 0 || list[len(list)-1] != id {
		p.ids[key] = append(list, id)
	}
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
	var addressVocabulary []string
	for _, k := range byAddress.keys {
		if !strings.HasPrefix(k, "#") {
			addressVocabulary = append(addressVocabulary, k)
		}
	}
	return &ListIndex{
		data: data, subjects: subjects, aliases: aliases, addresses: addresses, documentFrequency: documentFrequency,
		aliasesByFold: byFold.ids, nameVocabulary: newFuzzyVocabulary(byFold.keys),
		subjectsByAddressKey: byAddress.ids, addressVocabulary: newFuzzyVocabulary(addressVocabulary),
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
	return math.Max(o.MinTokenWeight, ln(float64(max(len(x.aliases), 1))/float64(max(x.documentFrequency[token.Key()], 1))))
}

func (x *ListIndex) candidates(query *preparedName, o *Options) []int {
	comparer := newTokenComparer(x.data)
	votes := newVotes()
	vote := func(k string, weight int) {
		for _, alias := range x.aliasesByFold[k] {
			votes.add(alias, weight)
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
	return votes.top(o.CandidateLimit)
}

type ranked struct {
	subject int
	percent int
	score   *nameScore
	alias   aliasEntry
	address addressComparison
}

// SearchName returns the subjects a name entry finds, best first (then by name): the best one, whatever its
// percentage, and every other at Review or above.
func (x *ListIndex) searchName(query *preparedName, o *Options) []SubjectMatch {
	comparer := newTokenComparer(x.data)
	scorer := &nameScorer{comparer: comparer, options: o}
	addresses := &addressComparer{comparer: comparer, data: x.data}
	type best struct {
		score *nameScore
		alias aliasEntry
	}
	bestBySubject := map[int]best{}
	var subjectOrder []int
	weights := map[*Token]float64{}
	weightOf := func(token *Token) float64 {
		w, ok := weights[token]
		if !ok {
			w = x.weight(token, o)
			weights[token] = w
		}
		return w
	}
	for _, aliasID := range x.candidates(query, o) {
		alias := x.aliases[aliasID]
		form := query.Entity
		if alias.Form.IsPerson {
			form = query.Person
		}
		score := scorer.Score(form, alias.Form, alias.IsWeak, weightOf)
		current, ok := bestBySubject[alias.Subject]
		if !ok {
			subjectOrder = append(subjectOrder, alias.Subject)
		}
		if !ok || score.Percent > current.score.Percent {
			bestBySubject[alias.Subject] = best{score, alias}
		}
	}

	list := make([]ranked, 0, len(subjectOrder))
	for _, subjectIndex := range subjectOrder {
		b := bestBySubject[subjectIndex]
		address := addresses.Compare(query.Address, x.addresses[subjectIndex])
		percent := b.score.Percent
		if address.State == Conflicting {
			percent = min(percent, o.ConflictingAddressCap)
		} else if address.State == Corroborated {
			percent = min(percent+o.CorroboratedBonus, min(bandMax(percent, o), b.score.Ceiling))
		}
		list = append(list, ranked{subjectIndex, percent, b.score, b.alias, address})
	}
	sort.SliceStable(list, func(a, b int) bool {
		if list[a].percent != list[b].percent {
			return list[a].percent > list[b].percent
		}
		return x.subjects[list[a].subject].PrimaryName < x.subjects[list[b].subject].PrimaryName
	})

	var matches []SubjectMatch
	for _, r := range list {
		outcome := toOutcome(r.percent, o)
		if len(matches) == 0 || outcome != NoMatch {
			details := r.score.Details()
			alignment := append(append([]string{}, details.Explanation...), r.address.Explanation)
			matches = append(matches, SubjectMatch{x.subjects[r.subject], r.percent, outcome, r.alias.Name, alignment, r.address.State, details.Words, details.Notes})
		}
	}
	return matches
}

// searchAddress is address-only screening: subjects whose address shares at least 3 parts (a number and a word).
func (x *ListIndex) searchAddress(entered *addressForm, o *Options) []SubjectMatch {
	comparer := &addressComparer{comparer: newTokenComparer(x.data), data: x.data}
	keyList := addressKeys(entered)
	for _, word := range entered.Words {
		for _, fold := range word.Folds {
			keyList = append(keyList, x.addressVocabulary.Near(fold)...)
		}
	}
	votes := newVotes()
	for _, k := range distinctStrings(keyList) {
		for _, subject := range x.subjectsByAddressKey[k] {
			votes.add(subject, 1)
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
