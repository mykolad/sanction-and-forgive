package api

import (
	"fmt"
	"log/slog"
	"regexp"
	"sanctionandforgive/lists"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode"
	"unicode/utf16"

	"sanctionandforgive/matching"
)

// DisclaimerText is on every result.
const DisclaimerText = "This is a demo list check, not a legal determination. It doesn't cover ownership or control by listed parties, " +
	"or embargoes. A person must review possible matches. The percentage is an estimate that this entry is the " +
	"listed party, based on the data entered; it isn't the chance that a shipment is lawful."

const maxFieldLength = 300

// ScreeningRequestBody is the request: the caller's reference and at least one field.
type ScreeningRequestBody struct {
	Reference    *string `json:"reference"`
	ReceiverName *string `json:"receiverName"`
	CompanyName  *string `json:"companyName"`
	Address      *string `json:"address"`
}

func (b *ScreeningRequestBody) ref() string { return deref(b.Reference) }

func deref(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}

var referencePattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._:/-]{0,63}$`)

// utf16Len counts as .NET's string.Length does.
func utf16Len(s string) int {
	n := 0
	for _, r := range s {
		n += utf16.RuneLen(r)
		if utf16.RuneLen(r) < 0 {
			n += 2
		}
	}
	return n
}

func isBlank(s *string) bool { return s == nil || strings.TrimFunc(*s, unicode.IsSpace) == "" }

// Validate says what's wrong with a request, by field (empty when valid). Never echoes a value.
func Validate(body *ScreeningRequestBody) map[string][]string {
	errors := map[string][]string{}
	if body.Reference == nil || *body.Reference == "" {
		errors["Reference"] = []string{"A reference is required: your own id for this screening (a transaction id, say), to find it in the logs."}
	} else if !referencePattern.MatchString(*body.Reference) {
		errors["Reference"] = []string{"A reference is 1-64 characters: letters, digits and . _ : / -, starting with a letter or digit. Never put personal data in it."}
	}
	for _, field := range []struct {
		name  string
		value *string
	}{{"ReceiverName", body.ReceiverName}, {"CompanyName", body.CompanyName}, {"Address", body.Address}} {
		if field.value != nil && utf16Len(*field.value) > maxFieldLength {
			errors[field.name] = []string{fmt.Sprintf("At most %d characters.", maxFieldLength)}
		}
	}
	if isBlank(body.ReceiverName) && isBlank(body.CompanyName) && isBlank(body.Address) {
		errors[""] = []string{"Enter a receiver, a company or an address."}
	}
	return errors
}

// --- Responses (ApiResponses.cs) ---

type ListResponse struct {
	Code        string     `json:"code"`
	Name        string     `json:"name"`
	AsOf        *time.Time `json:"asOf"`
	State       string     `json:"state"`
	Version     *int       `json:"version"`
	RecordCount int        `json:"recordCount"`
	URL         *string    `json:"url"`
}

func listResponse(list *LoadedList, now time.Time) ListResponse {
	var version *int
	if list.Index != nil {
		version = list.ActiveImportID
	}
	url := list.URL
	if source := lists.FindSource(list.Code); source != nil {
		url = source.PublicURL()
	}
	return ListResponse{list.Code, list.Name, list.LastCheckedAt, list.State(now), version, list.RecordCount, url}
}

type CandidateResponse struct {
	ListedName      string   `json:"listedName"`
	Alias           string   `json:"alias"`
	Programme       *string  `json:"programme"`
	SourceURL       *string  `json:"sourceUrl"`
	ExternalID      string   `json:"externalId"`
	Type            string   `json:"type"`
	Percent         int      `json:"percent"`
	Outcome         string   `json:"outcome"`
	Alignment       []string `json:"alignment"`
	AddressState    string   `json:"addressState"`
	ListedAddresses []string `json:"listedAddresses"`
}

var kindNames = map[matching.SubjectKind]string{matching.Individual: "Individual", matching.Entity: "Entity", matching.Unknown: "Unknown"}

func candidateResponse(m *matching.SubjectMatch) CandidateResponse {
	addresses := []string{}
	for _, a := range m.Subject.Addresses {
		if a.CountryCode != "" && !strings.Contains(a.Raw, a.CountryCode) {
			addresses = append(addresses, a.Raw+" ("+a.CountryCode+")")
		} else {
			addresses = append(addresses, a.Raw)
		}
	}
	alignment := m.Alignment
	if alignment == nil {
		alignment = []string{}
	}
	return CandidateResponse{m.Subject.PrimaryName, m.Alias, m.Subject.Programme, m.Subject.SourceURL, m.Subject.ExternalID,
		kindNames[m.Subject.Kind], m.Percent, m.Outcome.String(), alignment, m.AddressState.String(), addresses}
}

type CellResponse struct {
	List          string              `json:"list"`
	State         string              `json:"state"`
	Percent       *int                `json:"percent"`
	Outcome       *string             `json:"outcome"`
	BestCandidate *CandidateResponse  `json:"bestCandidate"`
	OtherHits     []CandidateResponse `json:"otherHits"`
	Allowed       bool                `json:"allowed"`
}

func stateOf(cell *matching.Cell) string {
	if cell.Checked {
		return "Checked"
	}
	return "NotChecked"
}

func outcomeOf(cell *matching.Cell) *string {
	if cell.Outcome == nil {
		return nil
	}
	s := cell.Outcome.String()
	return &s
}

func cellResponse(cell *matching.Cell, o *matching.Options) CellResponse {
	var best *CandidateResponse
	if cell.Best != nil && cell.Best.Percent >= o.BestCandidateMinPercent {
		c := candidateResponse(cell.Best)
		best = &c
	}
	others := []CandidateResponse{}
	for i := range cell.Others {
		others = append(others, candidateResponse(&cell.Others[i]))
	}
	return CellResponse{cell.List, stateOf(cell), cell.Percent, outcomeOf(cell), best, others, cell.Allowed}
}

type AllowedByResponse struct {
	ID     int    `json:"id"`
	Name   string `json:"name"`
	Reason string `json:"reason"`
}

type EntryResponse struct {
	Kind      string             `json:"kind"`
	Input     string             `json:"input"`
	Compared  string             `json:"compared"`
	Cells     []CellResponse     `json:"cells"`
	AllowedBy *AllowedByResponse `json:"allowedBy"`
}

func entryResponse(entry *matching.Entry, o *matching.Options) EntryResponse {
	cells := []CellResponse{}
	for i := range entry.Cells {
		cells = append(cells, cellResponse(&entry.Cells[i], o))
	}
	var allowed *AllowedByResponse
	if entry.AllowedBy != nil {
		allowed = &AllowedByResponse{entry.AllowedBy.ID, entry.AllowedBy.Name, entry.AllowedBy.Reason}
	}
	return EntryResponse{entry.Kind.String(), entry.Input, entry.Compared, cells, allowed}
}

type ScreeningResponse struct {
	Reference        string          `json:"reference"`
	Outcome          string          `json:"outcome"`
	Summary          string          `json:"summary"`
	Disclaimer       string          `json:"disclaimer"`
	MatcherVersion   string          `json:"matcherVersion"`
	Lists            []ListResponse  `json:"lists"`
	Entries          []EntryResponse `json:"entries"`
	AllowListVersion string          `json:"allowListVersion"`
}

// --- Stream lines (StreamResponses.cs) ---

type StreamHeaderLine struct {
	Type             string         `json:"type"`
	MatcherVersion   string         `json:"matcherVersion"`
	Disclaimer       string         `json:"disclaimer"`
	Lists            []ListResponse `json:"lists"`
	MaxRecords       int            `json:"maxRecords"`
	MaxSeconds       int            `json:"maxSeconds"`
	AllowListVersion string         `json:"allowListVersion"`
}

type StreamCandidate struct {
	ListedName   string                   `json:"listedName"`
	Alias        string                   `json:"alias"`
	ExternalID   string                   `json:"externalId"`
	SourceURL    *string                  `json:"sourceUrl"`
	Percent      int                      `json:"percent"`
	Words        []matching.WordAlignment `json:"words"`
	Notes        []string                 `json:"notes"`
	AddressState string                   `json:"addressState"`
}

type StreamCell struct {
	List    string           `json:"list"`
	State   string           `json:"state"`
	Percent *int             `json:"percent"`
	Outcome *string          `json:"outcome"`
	Best    *StreamCandidate `json:"best"`
	Allowed bool             `json:"allowed"`
}

type StreamEntry struct {
	Kind             string       `json:"kind"`
	Cells            []StreamCell `json:"cells"`
	AllowListEntryID *int         `json:"allowListEntryId"`
}

type StreamResultLine struct {
	Type      string        `json:"type"`
	Line      int           `json:"line"`
	Reference string        `json:"reference"`
	Outcome   string        `json:"outcome"`
	Entries   []StreamEntry `json:"entries"`
}

type StreamErrorLine struct {
	Type      string              `json:"type"`
	Line      int                 `json:"line"`
	Reference *string             `json:"reference"`
	Errors    map[string][]string `json:"errors"`
}

type StreamEndLine struct {
	Type      string `json:"type"`
	Screened  int    `json:"screened"`
	Errors    int    `json:"errors"`
	Truncated bool   `json:"truncated"`
	NextLine  *int   `json:"nextLine"`
}

func nonNil[T any](list []T) []T {
	if list == nil {
		return []T{}
	}
	return list
}

func streamEntry(entry *matching.Entry, o *matching.Options) StreamEntry {
	cells := []StreamCell{}
	for i := range entry.Cells {
		cell := &entry.Cells[i]
		var best *StreamCandidate
		if b := cell.Best; b != nil && b.Percent >= o.BestCandidateMinPercent {
			best = &StreamCandidate{b.Subject.PrimaryName, b.Alias, b.Subject.ExternalID, b.Subject.SourceURL, b.Percent, nonNil(b.Words), nonNil(b.Notes), b.AddressState.String()}
		}
		cells = append(cells, StreamCell{cell.List, stateOf(cell), cell.Percent, outcomeOf(cell), best, cell.Allowed})
	}
	var id *int
	if entry.AllowedBy != nil {
		id = &entry.AllowedBy.ID
	}
	return StreamEntry{entry.Kind.String(), cells, id}
}

// ScreeningService runs one screening: matching against the lists in memory (never the database), the response,
// and the audit line, which carries the reference and what each list answered, never what was entered.
type ScreeningService struct {
	Catalog  *Catalog
	Screener *matching.Screener
	Logger   *slog.Logger
	Now      func() time.Time
}

func (s *ScreeningService) Options() *matching.Options { return s.Screener.Options() }

func (s *ScreeningService) run(body *ScreeningRequestBody, lists []*LoadedList, allowList *matching.AllowList) (*matching.ScreeningResult, []ListResponse, time.Time) {
	now := s.Now()
	var screened []matching.ScreenedList
	for _, list := range lists {
		if list.SwitchedOff == nil {
			screened = append(screened, matching.ScreenedList{Code: list.Code, Name: list.Name, Index: list.Index})
		}
	}
	result, _ := s.Screener.Screen(matching.ScreeningRequest{ReceiverName: deref(body.ReceiverName), CompanyName: deref(body.CompanyName), Address: deref(body.Address)}, screened, allowList)

	best := map[string]int{}
	for _, entry := range result.Entries {
		for _, cell := range entry.Cells {
			percent := -1
			if cell.Percent != nil {
				percent = *cell.Percent
			}
			if current, ok := best[cell.List]; !ok || percent > current {
				best[cell.List] = percent
			}
		}
	}
	rows := make([]ListResponse, len(lists))
	for i, list := range lists {
		rows[i] = listResponse(list, now)
	}
	key := func(row ListResponse) int {
		if b, ok := best[row.Code]; ok && b >= s.Options().ReviewMinPercent {
			return b
		}
		return -1
	}
	sort.SliceStable(rows, func(a, b int) bool { return key(rows[a]) > key(rows[b]) })
	s.audit(body.ref(), result, rows)
	return result, rows, now
}

// Screen answers the single endpoint.
func (s *ScreeningService) Screen(body *ScreeningRequestBody) ScreeningResponse {
	lists, allowList := s.Catalog.Lists(), s.Catalog.AllowList()
	result, rows, now := s.run(body, lists, allowList)
	entries := []EntryResponse{}
	for i := range result.Entries {
		entries = append(entries, entryResponse(&result.Entries[i], s.Options()))
	}
	return ScreeningResponse{body.ref(), result.Outcome.String(), s.summary(result, lists, now), DisclaimerText, result.MatcherVersion, rows, entries, result.AllowListVersion}
}

// ScreenForStream screens one stream record against the stream's snapshot.
func (s *ScreeningService) ScreenForStream(body *ScreeningRequestBody, lists []*LoadedList, allowList *matching.AllowList) *matching.ScreeningResult {
	result, _, _ := s.run(body, lists, allowList)
	return result
}

func (s *ScreeningService) audit(reference string, result *matching.ScreeningResult, rows []ListResponse) {
	versions := make([]string, len(rows))
	for i, row := range rows {
		v := "-"
		if row.Version != nil {
			v = strconv.Itoa(*row.Version)
		}
		suffix := ""
		switch row.State {
		case "Overdue":
			suffix = " overdue"
		case "SwitchedOff":
			suffix = " off"
		}
		versions[i] = row.Code + "@" + v + suffix
	}
	var results strings.Builder
	for _, entry := range result.Entries {
		results.WriteString(entry.Kind.String())
		if entry.AllowedBy != nil {
			fmt.Fprintf(&results, " (allow-list entry %d)", entry.AllowedBy.ID)
		}
		results.WriteString(": ")
		cells := make([]string, len(entry.Cells))
		for i, cell := range entry.Cells {
			if !cell.Checked {
				cells[i] = cell.List + " NotChecked"
				continue
			}
			allowed := ""
			if cell.Allowed {
				allowed = " allowed"
			}
			best := ""
			if cell.Best != nil && cell.Best.Percent >= s.Options().BestCandidateMinPercent {
				best = " [" + cell.Best.Subject.ExternalID + "]"
			}
			cells[i] = fmt.Sprintf("%s %s%s %d%%%s", cell.List, cell.Outcome.String(), allowed, *cell.Percent, best)
		}
		results.WriteString(strings.Join(cells, "; "))
		results.WriteString(" | ")
	}
	message := fmt.Sprintf("Screening %s: %s; matcher %s; lists %s; allow-list %s; %s", reference, result.Outcome.String(), result.MatcherVersion,
		strings.Join(versions, ", "), result.AllowListVersion, strings.TrimRight(results.String(), " |"))
	s.Logger.Info(message, "Reference", reference)
}

func asOf(value *time.Time) string {
	if value == nil {
		return "unknown"
	}
	return value.UTC().Format("2 Jan 2006 15:04 UTC")
}

// summary is the summary line, worded precisely: never "not sanctioned" or "cleared", always which lists, as of when.
func (s *ScreeningService) summary(result *matching.ScreeningResult, lists []*LoadedList, now time.Time) string {
	var checked, notChecked, switchedOff []string
	byCode := map[string]*LoadedList{}
	for _, list := range lists {
		byCode[list.Code] = list
		if list.IsSearched() {
			checked = append(checked, list.Name+", as of "+asOf(list.LastCheckedAt))
		}
		if list.SwitchedOff == nil && !list.IsSearched() {
			notChecked = append(notChecked, list.Name)
		}
		if list.SwitchedOff != nil {
			switchedOff = append(switchedOff, list.Name+" (switched off by an administrator: "+strings.TrimRight(list.SwitchedOff.Reason, ".")+")")
		}
	}
	// One sentence per allowed age, naming each list that couldn't be updated in it, in order of first appearance.
	var ages []int
	overdue := map[int][]string{}
	for _, list := range lists {
		if list.IsOverdue(now) {
			age := *list.MaxAgeHours
			if _, seen := overdue[age]; !seen {
				ages = append(ages, age)
			}
			overdue[age] = append(overdue[age], list.Name+", as of "+asOf(list.LastCheckedAt))
		}
	}
	gap := ""
	if len(notChecked) > 0 {
		gap += " Not checked: " + strings.Join(notChecked, ", ") + "."
	}
	if len(switchedOff) > 0 {
		gap += " Not searched: " + strings.Join(switchedOff, "; ") + "."
	}
	for _, age := range ages {
		gap += fmt.Sprintf(" Warning: couldn't be updated within %d hours, so searched as last downloaded: %s.", age, strings.Join(overdue[age], "; "))
	}

	var hits, allowedOn []string
	for _, entry := range result.Entries {
		for _, cell := range entry.Cells {
			name := byCode[cell.List].Name
			if cell.Outcome != nil && (*cell.Outcome == matching.Match || *cell.Outcome == matching.Review) && !cell.Allowed && !contains(hits, name) {
				hits = append(hits, name)
			}
			if cell.Allowed && !contains(allowedOn, name) {
				allowedOn = append(allowedOn, name)
			}
		}
	}
	allowed := ""
	if len(allowedOn) > 0 {
		var reasons []string
		for _, entry := range result.Entries {
			if entry.AllowedBy == nil {
				continue
			}
			for _, cell := range entry.Cells {
				if cell.Allowed {
					reasons = append(reasons, entry.AllowedBy.Name+": "+entry.AllowedBy.Reason)
					break
				}
			}
		}
		allowed = fmt.Sprintf(" A possible match on %s is allowed by the allow-list (%s); a person can still review it.", strings.Join(allowedOn, ", "), strings.Join(reasons, "; "))
	}

	switch result.Outcome {
	case matching.OverallMatch:
		return "Possible match on " + strings.Join(hits, ", ") + ". A person must review it before anything else happens." + gap + allowed
	case matching.OverallReview:
		return "Review needed: a similar listed party on " + strings.Join(hits, ", ") + "." + gap + allowed
	case matching.OverallIncomplete:
		if len(checked) == 0 {
			return "Incomplete: no list could be checked." + gap
		}
		return "Incomplete. No match found in: " + strings.Join(checked, "; ") + "." + gap + allowed
	}
	if allowed != "" {
		return "No match found in: " + strings.Join(checked, "; ") + ", apart from what the allow-list allows." + gap + allowed
	}
	return "No match found in: " + strings.Join(checked, "; ") + "." + gap
}

func contains(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}
