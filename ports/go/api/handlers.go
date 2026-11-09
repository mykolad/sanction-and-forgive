package api

import (
	"encoding/json"
	"io"
	"net/http"
	"sanctionandforgive/lists"
	"sort"
	"strings"
	"sync"
	"time"
	"unicode"

	"golang.org/x/text/unicode/norm"

	"sanctionandforgive/matching"
	"sanctionandforgive/store"
)

// --- GET /api/v1/sources and /api/v1/sources/{code}/history (SourcesController.cs) ---

type HeldUpdateResponse struct {
	ImportID     int       `json:"importId"`
	RecordCount  int       `json:"recordCount"`
	DownloadedAt time.Time `json:"downloadedAt"`
	Reason       *string   `json:"reason"`
	Added        *int      `json:"added"`
	Removed      *int      `json:"removed"`
	Changed      *int      `json:"changed"`
}

type HistoryEntryResponse struct {
	ImportID    int        `json:"importId"`
	Result      string     `json:"result"`
	StartedAt   time.Time  `json:"startedAt"`
	CompletedAt *time.Time `json:"completedAt"`
	RecordCount int        `json:"recordCount"`
	Note        *string    `json:"note"`
	Reviewed    bool       `json:"reviewed"`
	Added       *int       `json:"added"`
	Removed     *int       `json:"removed"`
	Changed     *int       `json:"changed"`
}

func historyEntry(e store.HistoryEntry) HistoryEntryResponse {
	result := e.Status
	switch {
	case e.Active:
		result = "Active"
	case e.Status == "Completed":
		result = "Replaced"
	case e.Status == "PendingReview":
		result = "HeldForReview"
	}
	note := e.Message
	if e.Status == "Failed" {
		failed := "The download or the parse failed."
		note = &failed
	}
	return HistoryEntryResponse{e.ID, result, e.StartedAt, e.CompletedAt, e.RecordCount, note, e.ReviewedAt != nil, e.Added, e.Removed, e.Changed}
}

type SourceResponse struct {
	Code          string                 `json:"code"`
	Name          string                 `json:"name"`
	State         string                 `json:"state"`
	Status        string                 `json:"status"`
	Reason        *string                `json:"reason"`
	RecordCount   *int                   `json:"recordCount"`
	Version       *int                   `json:"version"`
	LastCheckedAt *time.Time             `json:"lastCheckedAt"`
	UpdatedAt     *time.Time             `json:"updatedAt"`
	MaxAgeHours   *int                   `json:"maxAgeHours"`
	Homepage      *string                `json:"homepage"`
	FileURL       *string                `json:"fileUrl"`
	TermsURL      *string                `json:"termsUrl"`
	Held          *HeldUpdateResponse    `json:"held"`
	History       []HistoryEntryResponse `json:"history"`
	HistoryCount  int                    `json:"historyCount"`
}

func sourceResponse(list *LoadedList, source *lists.ListSource, now time.Time) SourceResponse {
	state := list.State(now)
	var reason *string
	switch {
	case state == "Current":
	case list.SwitchedOff != nil:
		reason = lists.Optional("Switched off by an administrator on " + list.SwitchedOff.At.UTC().Format("2 Jan 2006 15:04 UTC") + ": " + list.SwitchedOff.Reason + " It isn't searched until it's switched on again, and it's still updated daily.")
	case list.ActiveImportID == nil:
		reason = lists.Optional("No completed import yet.")
	case list.Index == nil:
		reason = lists.Optional("Loading, or its last load failed. Its cells say Not checked until it loads.")
	default:
		reason = lists.Optional("Couldn't be updated within " + itoa(*list.MaxAgeHours) + " hours. It's still searched as last downloaded, and every result warns about it until a check succeeds.")
	}
	status := "Manual"
	var homepage, fileURL, terms *string
	if source != nil {
		status = string(source.Status)
		homepage, fileURL, terms = lists.Optional(source.Homepage), source.PublicURL(), lists.Optional(source.TermsURL)
	}
	var version *int
	if list.Index != nil {
		version = list.ActiveImportID
	}
	count := list.RecordCount
	return SourceResponse{Code: list.Code, Name: list.Name, State: state, Status: status, Reason: reason, RecordCount: &count, Version: version,
		LastCheckedAt: list.LastCheckedAt, UpdatedAt: list.UpdatedAt, MaxAgeHours: list.MaxAgeHours, Homepage: homepage, FileURL: fileURL, TermsURL: terms,
		History: []HistoryEntryResponse{}}
}

func notImported(source *lists.ListSource) SourceResponse {
	reason := source.Note
	if reason == "" {
		reason = "Not imported yet."
	}
	age := source.MaxAgeHours
	return SourceResponse{Code: source.Code, Name: source.Name, State: "NotImported", Status: string(source.Status), Reason: &reason, MaxAgeHours: &age,
		Homepage: lists.Optional(source.Homepage), TermsURL: lists.Optional(source.TermsURL), History: []HistoryEntryResponse{}}
}

type sourcesResponse struct {
	Now   time.Time        `json:"now"`
	Lists []SourceResponse `json:"lists"`
}

func (s *Server) sources(w http.ResponseWriter, _ *http.Request) {
	if !s.Catalog.IsReady() {
		writeJSON(w, http.StatusServiceUnavailable, warmingUp{"warmingUp"})
		return
	}
	now := s.Screenings.Now()
	history, heldUpdates := s.Catalog.History(), s.Catalog.Held()
	rows := []SourceResponse{}
	for _, list := range s.Catalog.Lists() {
		response := sourceResponse(list, lists.FindSource(list.Code), now)
		if held, ok := heldUpdates[list.Code]; ok {
			response.Held = &HeldUpdateResponse{held.ID, held.RecordCount, held.StartedAt, held.Message, held.Added, held.Removed, held.Changed}
		}
		entries := history[list.Code]
		for i, entry := range entries {
			if i == HistoryShown {
				break
			}
			response.History = append(response.History, historyEntry(entry))
		}
		response.HistoryCount = len(entries)
		rows = append(rows, response)
	}
	for i := range lists.Sources {
		if s.Catalog.Find(lists.Sources[i].Code) == nil {
			rows = append(rows, notImported(&lists.Sources[i]))
		}
	}
	writeJSON(w, http.StatusOK, sourcesResponse{now, rows})
}

type sourceHistoryResponse struct {
	Code    string                 `json:"code"`
	Name    string                 `json:"name"`
	History []HistoryEntryResponse `json:"history"`
}

func (s *Server) history(w http.ResponseWriter, r *http.Request) {
	if !s.Catalog.IsReady() {
		writeJSON(w, http.StatusServiceUnavailable, warmingUp{"warmingUp"})
		return
	}
	list := s.Catalog.Find(r.PathValue("code"))
	if list == nil {
		http.NotFound(w, r)
		return
	}
	entries := []HistoryEntryResponse{}
	for _, entry := range s.Catalog.History()[list.Code] {
		entries = append(entries, historyEntry(entry))
	}
	writeJSON(w, http.StatusOK, sourceHistoryResponse{list.Code, list.Name, entries})
}

// --- GET /api/v1/examples (ExamplesController.cs) ---

type exampleResponse struct {
	Label        string  `json:"label"`
	ReceiverName *string `json:"receiverName"`
	CompanyName  *string `json:"companyName"`
	Address      *string `json:"address"`
	List         string  `json:"list"`
}

func (s *Server) examples(w http.ResponseWriter, _ *http.Request) {
	type pair struct {
		list    string
		subject *matching.ListedSubject
	}
	var entities []pair
	for _, list := range s.Catalog.Lists() {
		if !list.IsSearched() {
			continue
		}
		for _, subject := range list.Index.Subjects() {
			if subject.Kind == matching.Entity {
				entities = append(entities, pair{list.Code, subject})
			}
		}
	}
	sort.SliceStable(entities, func(a, b int) bool { return entities[a].subject.ExternalID < entities[b].subject.ExternalID })
	isLatin := func(v string) bool {
		for _, c := range v {
			if c >= 0x250 {
				return false
			}
		}
		return true
	}
	hasCyrillic := func(v string) bool {
		return strings.ContainsFunc(v, func(c rune) bool { return c >= 'Ѐ' && c <= 'ӿ' })
	}
	wordCount := func(v string) int { return len(strings.FieldsFunc(v, func(c rune) bool { return c == ' ' })) }
	goodAddress := func(a matching.ListedAddress) bool { n := utf16Len(a.Raw); return n > 5 && n < 80 }

	examples := []exampleResponse{}
	var latin, withAddress *matching.ListedSubject
	for _, p := range entities {
		if n := wordCount(p.subject.PrimaryName); isLatin(p.subject.PrimaryName) && n >= 2 && n <= 5 {
			latin = p.subject
			examples = append(examples, exampleResponse{"A listed company", nil, lists.Optional(p.subject.PrimaryName), nil, p.list})
			break
		}
	}
	for _, p := range entities {
		found := ""
		for _, name := range p.subject.Names {
			if hasCyrillic(name.FullName) && wordCount(name.FullName) <= 6 {
				found = name.FullName
				break
			}
		}
		if found != "" {
			examples = append(examples, exampleResponse{"In Cyrillic", nil, lists.Optional(found), nil, p.list})
			break
		}
	}
	for _, p := range entities {
		if !isLatin(p.subject.PrimaryName) || p.subject == latin {
			continue
		}
		for _, a := range p.subject.Addresses {
			if goodAddress(a) {
				withAddress = p.subject
				examples = append(examples, exampleResponse{"With an address", nil, lists.Optional(p.subject.PrimaryName), lists.Optional(a.Raw), p.list})
				break
			}
		}
		if withAddress != nil {
			break
		}
	}
	for _, p := range entities {
		if !isLatin(p.subject.PrimaryName) || p.subject == latin || p.subject == withAddress {
			continue
		}
		words := strings.Split(p.subject.PrimaryName, " ")
		long := false
		for _, word := range words {
			long = long || utf16Len(word) >= 8
		}
		if long {
			examples = append(examples, exampleResponse{"With a typo", nil, lists.Optional(withTypo(words)), nil, p.list})
			break
		}
	}
	writeJSON(w, http.StatusOK, examples)
}

// withTypo drops one letter from the longest word (after its first letter): a near miss.
func withTypo(words []string) string {
	longest := 0
	for i, word := range words {
		if utf16Len(word) > utf16Len(words[longest]) {
			longest = i
		}
	}
	for i, word := range words {
		if word == words[longest] {
			longest = i
			break
		}
	}
	runes := []rune(words[longest])
	cut := len(runes) / 2
	words[longest] = string(runes[:cut]) + string(runes[cut+1:])
	return strings.Join(words, " ")
}

// --- POST /api/v1/lists/{code}/browse (BrowseController.cs) ---

type browseRequest struct {
	Query  *string `json:"query"`
	Type   *string `json:"type"`
	Offset int     `json:"offset"`
	Limit  int     `json:"limit"`
}

type browseName struct {
	Name   string `json:"name"`
	IsWeak bool   `json:"isWeak"`
}

type browseAddress struct {
	Raw         string  `json:"raw"`
	CountryCode *string `json:"countryCode"`
}

type browseEntry struct {
	ExternalID  string          `json:"externalId"`
	Type        string          `json:"type"`
	PrimaryName string          `json:"primaryName"`
	Names       []browseName    `json:"names"`
	Addresses   []browseAddress `json:"addresses"`
	Programme   *string         `json:"programme"`
	SourceURL   *string         `json:"sourceUrl"`
}

type browseResponse struct {
	List    ListResponse  `json:"list"`
	Total   int           `json:"total"`
	Offset  int           `json:"offset"`
	Limit   int           `json:"limit"`
	Entries []browseEntry `json:"entries"`
}

type browsable struct {
	subject *matching.ListedSubject
	folded  []string
}

var sortedCache sync.Map // *matching.ListIndex → []browsable

func (s *Server) browse(w http.ResponseWriter, r *http.Request) {
	if !s.Catalog.IsReady() {
		writeJSON(w, http.StatusServiceUnavailable, warmingUp{"warmingUp"})
		return
	}
	var body browseRequest
	data, _ := io.ReadAll(io.LimitReader(r.Body, 1<<16))
	if err := json.Unmarshal(data, &body); err != nil {
		badRequest(w, map[string][]string{"$": {"The request body isn't a valid browse request."}})
		return
	}
	errs := map[string][]string{}
	if body.Query != nil && utf16Len(*body.Query) > 100 {
		errs["Query"] = []string{"At most 100 characters."}
	}
	if body.Offset < 0 {
		errs["Offset"] = []string{"Zero or more."}
	}
	if body.Limit < 1 || body.Limit > 100 {
		errs["Limit"] = []string{"From 1 to 100."}
	}
	var kind *matching.SubjectKind
	if body.Type != nil && *body.Type != "" {
		for k, name := range kindNames {
			if strings.EqualFold(name, *body.Type) {
				k := k
				kind = &k
			}
		}
		if kind == nil {
			errs["Type"] = []string{"Individual, Entity or Unknown."}
		}
	}
	if len(errs) > 0 {
		badRequest(w, errs)
		return
	}
	list := s.Catalog.Find(r.PathValue("code"))
	if list == nil {
		http.NotFound(w, r)
		return
	}
	if list.Index == nil {
		writeJSON(w, http.StatusServiceUnavailable, warmingUp{"listNotLoaded"})
		return
	}
	entries := inNameOrder(list.Index)
	id := strings.TrimFunc(deref(body.Query), unicode.IsSpace)
	query := fold(id)
	var matched []*matching.ListedSubject
	for _, entry := range entries {
		if kind != nil && entry.subject.Kind != *kind {
			continue
		}
		if query != "" {
			found := strings.EqualFold(entry.subject.ExternalID, id)
			for _, name := range entry.folded {
				found = found || strings.Contains(name, query)
			}
			if !found {
				continue
			}
		}
		matched = append(matched, entry.subject)
	}
	page := []browseEntry{}
	for i := body.Offset; i < len(matched) && i < body.Offset+body.Limit; i++ {
		subject := matched[i]
		names := []browseName{}
		for _, n := range subject.Names {
			names = append(names, browseName{n.FullName, n.IsWeak})
		}
		addresses := []browseAddress{}
		for _, a := range subject.Addresses {
			addresses = append(addresses, browseAddress{a.Raw, lists.Optional(a.CountryCode)})
		}
		page = append(page, browseEntry{subject.ExternalID, kindNames[subject.Kind], subject.PrimaryName, names, addresses, subject.Programme, subject.SourceURL})
	}
	writeJSON(w, http.StatusOK, browseResponse{listResponse(list, s.Screenings.Now()), len(matched), body.Offset, body.Limit, page})
}

func inNameOrder(index *matching.ListIndex) []browsable {
	if cached, ok := sortedCache.Load(index); ok {
		return cached.([]browsable)
	}
	var entries []browsable
	for _, subject := range index.Subjects() {
		folded := make([]string, len(subject.Names))
		for i, n := range subject.Names {
			folded[i] = fold(n.FullName)
		}
		entries = append(entries, browsable{subject, folded})
	}
	sort.SliceStable(entries, func(a, b int) bool {
		ka, kb := sortKey(entries[a].subject.PrimaryName), sortKey(entries[b].subject.PrimaryName)
		if ka != kb {
			return ka < kb
		}
		return entries[a].subject.ExternalID < entries[b].subject.ExternalID
	})
	sortedCache.Store(index, entries)
	return entries
}

func sortKey(name string) string {
	folded := []rune(fold(name))
	start := 0
	for start < len(folded) && !(unicode.IsLetter(folded[start]) || unicode.Is(unicode.Nd, folded[start])) {
		start++
	}
	return string(folded[start:])
}

// fold is the browse filter's: lower case, no diacritics, spaces collapsed. Cyrillic stays Cyrillic.
func fold(text string) string {
	var b strings.Builder
	lastSpace := false
	for _, c := range norm.NFD.String(text) {
		if unicode.Is(unicode.Mn, c) {
			continue
		}
		if unicode.IsSpace(c) {
			if b.Len() > 0 && !lastSpace {
				b.WriteRune(' ')
				lastSpace = true
			}
			continue
		}
		b.WriteRune(unicode.ToLower(c))
		lastSpace = false
	}
	return strings.TrimRightFunc(b.String(), unicode.IsSpace)
}
