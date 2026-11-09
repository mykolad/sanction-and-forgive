package matching

import (
	"errors"
	"sort"
	"strconv"
	"strings"
	"unicode"
)

// ScreeningRequest is what the user entered; every field optional, at least one required.
type ScreeningRequest struct {
	ReceiverName string
	CompanyName  string
	Address      string
}

// ScreenedList is a list as the screener sees it; a nil Index means Not checked.
type ScreenedList struct {
	Code  string
	Name  string
	Index *ListIndex
}

type EntryKind int

const (
	KindReceiver EntryKind = iota
	KindCompany
	KindAddress
)

func (k EntryKind) String() string {
	switch k {
	case KindCompany:
		return "Company"
	case KindAddress:
		return "Address"
	}
	return "Receiver"
}

type OverallOutcome int

const (
	OverallMatch OverallOutcome = iota
	OverallReview
	OverallIncomplete
	OverallNoMatch
)

func (o OverallOutcome) String() string {
	return [...]string{"Match", "Review", "Incomplete", "NoMatch"}[o]
}

// Cell is one list's answer for one entry. Not checked: no percentage, no outcome.
type Cell struct {
	List    string
	Checked bool
	Percent *int
	Outcome *Outcome
	Best    *SubjectMatch
	Others  []SubjectMatch
	Allowed bool
}

type Entry struct {
	Kind      EntryKind
	Input     string
	Compared  string
	Cells     []Cell
	AllowedBy *AllowListEntry
}

type ScreeningResult struct {
	Outcome          OverallOutcome
	MatcherVersion   string
	Entries          []Entry
	AllowListVersion string
}

// ListedHit is one listed subject found for a name on one list.
type ListedHit struct {
	List  string
	Match SubjectMatch
}

// Screener screens each entry against each list separately (section 5.7). Deterministic; stores and logs nothing.
type Screener struct {
	options        *Options
	data           *MatcherData
	MatcherVersion string
}

func NewScreener(options *Options) *Screener {
	data := Data()
	return &Screener{options: options, data: data, MatcherVersion: options.version(data)}
}

func (s *Screener) Options() *Options { return s.options }

var ErrNothingEntered = errors.New("enter a receiver, a company or an address")

func (s *Screener) Screen(request ScreeningRequest, lists []ScreenedList, allowList *AllowList) (*ScreeningResult, error) {
	receiver, company, address := clean(request.ReceiverName), clean(request.CompanyName), clean(request.Address)
	if receiver == "" && company == "" && address == "" {
		return nil, ErrNothingEntered
	}
	tok := &tokenizer{data: s.data}
	parser := &nameParser{data: s.data}
	var addressForm *addressForm
	if address != "" {
		addressForm = (&addressParser{data: s.data, tokenizer: tok}).Parse(address, "")
	}

	var entries []Entry
	for _, item := range []struct {
		kind EntryKind
		name string
	}{{KindReceiver, receiver}, {KindCompany, company}} {
		if item.name == "" {
			continue
		}
		tokens := tok.Tokenize(item.name, true)
		prepared := &preparedName{parser.Entity(tokens), parser.Person(tokens), addressForm}
		compared := "Name only: no address entered"
		if addressForm != nil {
			compared = "Name, with the address where the list has one"
		}
		allowedBy := allowList.findTokens(tokens)
		var cells []Cell
		for _, list := range lists {
			cell := s.toCell(list, func(index *ListIndex) []SubjectMatch { return index.searchName(prepared, s.options) })
			if allowedBy != nil && cell.Outcome != nil && *cell.Outcome == Review {
				cell.Allowed = true
			}
			cells = append(cells, cell)
		}
		entries = append(entries, Entry{item.kind, item.name, compared, cells, allowedBy})
	}
	if addressForm != nil {
		var cells []Cell
		for _, list := range lists {
			cells = append(cells, s.toCell(list, func(index *ListIndex) []SubjectMatch { return index.searchAddress(addressForm, s.options) }))
		}
		entries = append(entries, Entry{KindAddress, address, "Address only", cells, nil})
	}
	return &ScreeningResult{overall(entries), s.MatcherVersion, entries, allowList.Version}, nil
}

// NameHits returns every listed subject at Review or above for name, on each list that can be searched.
func (s *Screener) NameHits(name string, lists []ScreenedList) ([]ListedHit, error) {
	cleaned := clean(name)
	if cleaned == "" {
		return nil, errors.New("enter a name")
	}
	tokens := (&tokenizer{data: s.data}).Tokenize(cleaned, true)
	parser := &nameParser{data: s.data}
	prepared := &preparedName{parser.Entity(tokens), parser.Person(tokens), nil}
	var hits []ListedHit
	for _, list := range lists {
		if list.Index == nil {
			continue
		}
		for _, match := range list.Index.searchName(prepared, s.options) {
			if match.Outcome != NoMatch {
				hits = append(hits, ListedHit{list.Code, match})
			}
		}
	}
	return hits, nil
}

func (s *Screener) toCell(list ScreenedList, search func(*ListIndex) []SubjectMatch) Cell {
	if list.Index == nil {
		return Cell{List: list.Code, Others: []SubjectMatch{}}
	}
	matches := search(list.Index)
	var best *SubjectMatch
	if len(matches) > 0 {
		best = &matches[0]
	}
	others := []SubjectMatch{}
	for _, match := range matches[min(1, len(matches)):] {
		if match.Outcome != NoMatch && len(others) < s.options.HitsPerCell-1 {
			others = append(others, match)
		}
	}
	percent := 0
	if best != nil {
		percent = best.Percent
	}
	outcome := toOutcome(percent, s.options)
	return Cell{List: list.Code, Checked: true, Percent: &percent, Outcome: &outcome, Best: best, Others: others}
}

func overall(entries []Entry) OverallOutcome {
	var cells []Cell
	for _, e := range entries {
		cells = append(cells, e.Cells...)
	}
	has := func(f func(Cell) bool) bool {
		for _, c := range cells {
			if f(c) {
				return true
			}
		}
		return false
	}
	switch {
	case has(func(c Cell) bool { return c.Outcome != nil && *c.Outcome == Match }):
		return OverallMatch
	case has(func(c Cell) bool { return c.Outcome != nil && *c.Outcome == Review && !c.Allowed }):
		return OverallReview
	case len(cells) == 0 || has(func(c Cell) bool { return !c.Checked }):
		return OverallIncomplete
	}
	return OverallNoMatch
}

// clean is the reference's IsNullOrWhiteSpace check and Trim.
func clean(value string) string { return strings.TrimFunc(value, unicode.IsSpace) }

// AllowListEntry is an allow-list entry: a name known to be a legitimate party, and why.
type AllowListEntry struct {
	ID     int
	Name   string
	Reason string
}

// AllowList holds names whose possible matches (Review) are shown as allowed. Immutable, with a version.
type AllowList struct {
	byKey   map[string]*AllowListEntry
	Version string
}

// EmptyAllowList has no entries.
var EmptyAllowList = &AllowList{byKey: map[string]*AllowListEntry{}, Version: "none"}

// BuildAllowList builds the list; of two entries for the same name, the older (lower id) wins.
func BuildAllowList(entries []AllowListEntry, version string) *AllowList {
	sorted := append([]AllowListEntry{}, entries...)
	sort.SliceStable(sorted, func(a, b int) bool { return sorted[a].ID < sorted[b].ID })
	tok := &tokenizer{data: Data()}
	byKey := map[string]*AllowListEntry{}
	for i := range sorted {
		k := allowListKey(tok.Tokenize(sorted[i].Name, true))
		if _, seen := byKey[k]; k != "" && !seen {
			byKey[k] = &sorted[i]
		}
	}
	return &AllowList{byKey, version}
}

func (a *AllowList) Count() int { return len(a.byKey) }

// Without is this list without entry id ("12-5": version 12 without entry 5).
func (a *AllowList) Without(id int) *AllowList {
	byKey := map[string]*AllowListEntry{}
	for k, e := range a.byKey {
		if e.ID != id {
			byKey[k] = e
		}
	}
	return &AllowList{byKey, a.Version + "-" + strconv.Itoa(id)}
}

// Find returns the entry for exactly this name, if there is one.
func (a *AllowList) Find(name string) *AllowListEntry {
	if len(a.byKey) == 0 {
		return nil
	}
	return a.byKey[allowListKey((&tokenizer{data: Data()}).Tokenize(name, true))]
}

func (a *AllowList) findTokens(tokens []RawToken) *AllowListEntry {
	if a == nil || len(a.byKey) == 0 {
		return nil
	}
	return a.byKey[allowListKey(tokens)]
}

// AllowListKey is what the matcher reads from a name: its tokens' text, initials runs marked "a.b.".
func AllowListKey(name string) string {
	return allowListKey((&tokenizer{data: Data()}).Tokenize(name, true))
}

func allowListKey(tokens []RawToken) string {
	parts := make([]string, len(tokens))
	for i, t := range tokens {
		if t.Initials != nil {
			letters := make([]string, len(t.Initials))
			for j, l := range t.Initials {
				letters[j] = l.Text
			}
			parts[i] = strings.Join(letters, ".") + "."
		} else {
			parts[i] = t.Token.Text
		}
	}
	return strings.Join(parts, " ")
}
