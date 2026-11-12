package api

import (
	"context"
	"database/sql"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"sanctionandforgive/matching"
	"sanctionandforgive/store"
)

// SwitchedOff says when an administrator switched a list off, and why.
type SwitchedOff struct {
	At     time.Time
	Reason string
}

// LoadedList is one list as the API holds it in memory (LoadedList in the reference).
type LoadedList struct {
	Code           string
	Name           string
	URL            *string
	MaxAgeHours    *int
	ActiveImportID *int
	LastCheckedAt  *time.Time
	UpdatedAt      *time.Time
	RecordCount    int
	Index          *matching.ListIndex
	SwitchedOff    *SwitchedOff
}

// IsSearched: switched on, with an index. Fail closed.
func (l *LoadedList) IsSearched() bool { return l.SwitchedOff == nil && l.Index != nil }

// IsOverdue: searched, but a feed whose last successful check is older than its allowed age.
func (l *LoadedList) IsOverdue(now time.Time) bool {
	return l.IsSearched() && l.MaxAgeHours != nil && (l.LastCheckedAt == nil || now.Sub(*l.LastCheckedAt) > time.Duration(*l.MaxAgeHours)*time.Hour)
}

// State is Current, Overdue, NotChecked or SwitchedOff.
func (l *LoadedList) State(now time.Time) string {
	switch {
	case l.SwitchedOff != nil:
		return "SwitchedOff"
	case !l.IsSearched():
		return "NotChecked"
	case l.IsOverdue(now):
		return "Overdue"
	}
	return "Current"
}

type snapshot struct {
	lists []*LoadedList
	ready bool
}

// Catalog holds one snapshot of the lists, swapped whole, plus the history, held updates and the allow-list.
type Catalog struct {
	snapshot  atomic.Pointer[snapshot]
	history   atomic.Pointer[map[string][]store.HistoryEntry]
	held      atomic.Pointer[map[string]store.HistoryEntry]
	allowList atomic.Pointer[matching.AllowList]
}

func NewCatalog() *Catalog {
	c := &Catalog{}
	c.snapshot.Store(&snapshot{})
	c.history.Store(&map[string][]store.HistoryEntry{})
	c.held.Store(&map[string]store.HistoryEntry{})
	c.allowList.Store(matching.EmptyAllowList)
	return c
}

func (c *Catalog) IsReady() bool                             { return c.snapshot.Load().ready }
func (c *Catalog) Lists() []*LoadedList                      { return c.snapshot.Load().lists }
func (c *Catalog) Replace(lists []*LoadedList)               { c.snapshot.Store(&snapshot{lists, true}) }
func (c *Catalog) History() map[string][]store.HistoryEntry  { return *c.history.Load() }
func (c *Catalog) Held() map[string]store.HistoryEntry       { return *c.held.Load() }
func (c *Catalog) AllowList() *matching.AllowList            { return c.allowList.Load() }
func (c *Catalog) ReplaceAllowList(list *matching.AllowList) { c.allowList.Store(list) }

// Find returns the loaded list with this code (ignoring case), or nil.
func (c *Catalog) Find(code string) *LoadedList {
	for _, list := range c.Lists() {
		if strings.EqualFold(list.Code, code) {
			return list
		}
	}
	return nil
}

// HistoryShown is how many updates GET /api/v1/sources shows per list.
const HistoryShown = 10

// Loader loads the lists from SQL into the catalog: only lists whose active import changed are rebuilt, one at a time.
type Loader struct {
	db      *sql.DB
	catalog *Catalog
	logger  *slog.Logger
	gate    sync.Mutex
}

func NewLoader(db *sql.DB, catalog *Catalog, logger *slog.Logger) *Loader {
	return &Loader{db: db, catalog: catalog, logger: logger}
}

// Refresh reloads the lists; one refresh at a time.
func (l *Loader) Refresh(ctx context.Context) error {
	l.gate.Lock()
	defer l.gate.Unlock()
	return l.refreshOnce(ctx)
}

// ReloadAllowList reloads the allow-list only (after an edit), under the refresh gate. With withoutID (a removal),
// that entry stops applying first.
func (l *Loader) ReloadAllowList(ctx context.Context, withoutID *int) bool {
	l.gate.Lock()
	defer l.gate.Unlock()
	if withoutID != nil {
		l.catalog.ReplaceAllowList(l.catalog.AllowList().Without(*withoutID))
	}
	return l.tryLoadAllowList(ctx)
}

// UnderGate runs fn under the refresh gate (an allow-list addition: the lists can't change between check and insert).
func (l *Loader) UnderGate(fn func()) {
	l.gate.Lock()
	defer l.gate.Unlock()
	fn()
}

func (l *Loader) tryLoadAllowList(ctx context.Context) bool {
	if err := l.loadAllowList(ctx); err != nil {
		l.logger.Warn("Reloading the allow-list after an edit failed; the next refresh retries", "error", err.Error())
		return false
	}
	return true
}

func (l *Loader) loadAllowList(ctx context.Context) error {
	entries, version, err := store.AllowListCurrent(ctx, l.db)
	if err != nil {
		return err
	}
	list := make([]matching.AllowListEntry, len(entries))
	for i, e := range entries {
		list[i] = matching.AllowListEntry{ID: e.ID, Name: e.Name, Reason: e.Reason}
	}
	l.catalog.ReplaceAllowList(matching.BuildAllowList(list, version))
	return nil
}

func (l *Loader) refreshOnce(ctx context.Context) error {
	sources, err := store.Sources(ctx, l.db)
	if err != nil {
		return err
	}
	if err := l.loadAllowList(ctx); err != nil {
		return err
	}
	history, err := store.History(ctx, l.db, "")
	if err != nil {
		return err
	}
	heldEntries, err := store.Held(ctx, l.db)
	if err != nil {
		return err
	}
	held := map[string]store.HistoryEntry{}
	for _, h := range heldEntries {
		if _, seen := held[h.SourceCode]; !seen {
			held[h.SourceCode] = h
		}
	}
	l.catalog.held.Store(&held)
	byCode := map[string][]store.HistoryEntry{}
	for _, h := range history {
		byCode[h.SourceCode] = append(byCode[h.SourceCode], h)
	}
	l.catalog.history.Store(&byCode)

	current := map[string]*LoadedList{}
	for _, list := range l.catalog.Lists() {
		current[list.Code] = list
	}
	lists := make([]*LoadedList, len(sources))
	type pendingBuild struct {
		position int
		target   *LoadedList
	}
	var pending []pendingBuild
	for i, source := range sources {
		var switchedOff *SwitchedOff
		if source.SwitchedOffAt != nil {
			reason := ""
			if source.SwitchedOffReason != nil {
				reason = *source.SwitchedOffReason
			}
			switchedOff = &SwitchedOff{*source.SwitchedOffAt, reason}
		}
		target := &LoadedList{Code: source.Code, Name: source.Name, URL: source.URL, MaxAgeHours: source.MaxAgeHours, ActiveImportID: source.ActiveImportID,
			LastCheckedAt: source.LastCheckedAt, UpdatedAt: source.UpdatedAt, RecordCount: source.RecordCount, SwitchedOff: switchedOff}
		existing := current[source.Code]
		if existing != nil && equalIDs(existing.ActiveImportID, source.ActiveImportID) && (existing.Index != nil || source.ActiveImportID == nil) {
			target.Index = existing.Index
			lists[i] = target
			continue
		}
		if existing == nil {
			lists[i] = target
		} else {
			copy := *existing
			copy.SwitchedOff = switchedOff
			lists[i] = &copy
		}
		if source.ActiveImportID != nil {
			pending = append(pending, pendingBuild{i, target})
		}
	}
	// Only lists holds the old indexes now: each swap below can release the one it replaces (Copilot's point on #37,
	// the same pattern there): never two whole generations at once.
	current = nil
	// A build that fails keeps whatever served before, and the refresh reports it once everything else is published:
	// the change isn't applied yet, so RunRefresh tries again shortly, never waiting for refreshEvery.
	var notBuilt []string
	for _, p := range pending {
		if index := l.build(ctx, p.target.Code, *p.target.ActiveImportID, p.target.RecordCount); index != nil {
			built := *p.target
			built.Index = index
			lists[p.position] = &built
		} else {
			notBuilt = append(notBuilt, p.target.Code)
		}
		if l.catalog.IsReady() {
			l.catalog.Replace(append([]*LoadedList{}, lists...))
		}
	}
	l.catalog.Replace(lists)
	if len(notBuilt) > 0 {
		return fmt.Errorf("not built: %s; everything else is loaded, and the list's previous version serves until it's built", strings.Join(notBuilt, ", "))
	}
	return nil
}

func equalIDs(a, b *int) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	return *a == *b
}

func (l *Loader) build(ctx context.Context, code string, importID, expected int) *matching.ListIndex {
	subjects, err := store.ActiveSubjects(ctx, l.db, code, importID)
	if err != nil {
		l.logger.Error("Building the index failed", "code", code, "importId", importID, "error", err.Error())
		return nil
	}
	if subjects == nil || len(subjects) != expected {
		l.logger.Warn("The import changed while loading; it stays Not checked until the next refresh", "code", code, "importId", importID, "loaded", len(subjects), "expected", expected)
		return nil
	}
	listed := make([]*matching.ListedSubject, len(subjects))
	for i, s := range subjects {
		listed[i] = ToListed(s)
	}
	index := matching.Build(listed)
	l.logger.Info("Built the index", "code", code, "importId", importID, "subjects", len(subjects), "aliases", index.AliasCount())
	return index
}

// ToListed is a subject from the database as the matcher reads it.
func ToListed(s store.Subject) *matching.ListedSubject {
	kind := matching.Unknown
	switch s.Type {
	case 0:
		kind = matching.Individual
	case 1:
		kind = matching.Entity
	}
	names := make([]matching.ListedName, len(s.Names))
	for i, n := range s.Names {
		names[i] = matching.ListedName{FullName: n.FullName, IsWeak: n.IsWeak}
	}
	addresses := make([]matching.ListedAddress, len(s.Addresses))
	for i, a := range s.Addresses {
		code := ""
		if a.CountryCode != nil {
			code = strings.TrimSpace(*a.CountryCode)
		}
		addresses[i] = matching.ListedAddress{Raw: a.Raw, CountryCode: code}
	}
	return &matching.ListedSubject{ExternalID: s.ExternalID, Kind: kind, PrimaryName: s.PrimaryName, Names: names, Addresses: addresses, Programme: s.Programmes, SourceURL: s.SourceURL}
}

// nextRetryWait is the wait before trying a failed refresh again: 5 s, then twice the last, at most a minute.
func nextRetryWait(last time.Duration) time.Duration {
	if last <= 0 {
		return 5 * time.Second
	}
	return min(2*last, time.Minute)
}

// changeCheckTimeout bounds one check for changes; one that takes longer counts as failed.
const changeCheckTimeout = 10 * time.Second

// RunRefresh loads the lists at startup, then refreshes every refreshEvery, and sooner whenever store.ChangesSQL moved:
// it's read every checkEvery (0: no check), so an administrator's change made on another replica applies here within
// seconds. A check that fails is logged once, and once again when the database answers again.
func (l *Loader) RunRefresh(ctx context.Context, refreshEvery, checkEvery time.Duration) {
	wait := checkEvery
	if wait <= 0 {
		wait = refreshEvery
	}
	var seen string
	var refreshedAt, retryAt time.Time
	var retryWait time.Duration
	checkFailing := false
	for {
		due := refreshedAt.IsZero() || time.Since(refreshedAt) >= refreshEvery
		changes, read := "", false
		if checkEvery > 0 {
			// Bounded: a database that takes the query and never answers must not stop the checks or the refresh.
			checkCtx, cancel := context.WithTimeout(ctx, changeCheckTimeout)
			value, err := store.Changes(checkCtx, l.db)
			cancel()
			switch {
			case err == nil:
				changes, read = value, true
				if checkFailing {
					checkFailing = false
					l.logger.Info("Checking the database for changes works again")
				}
			case ctx.Err() == nil && !checkFailing:
				checkFailing = true
				l.logger.Warn("Checking the database for changes failed; serving what was loaded, and checking again", "error", err.Error())
			}
		}
		// Read before the refresh: a change made while it runs moves the value again, and the next check sees it.
		// A refresh that failed stays pending: tried again after retryWait (5 s, doubling up to a minute), never at
		// every check, and the change counts as seen only once a refresh has worked (an allow-list removal seen while
		// the database stumbled still applies as soon as it answers).
		retry := !retryAt.IsZero() && !time.Now().Before(retryAt)
		if due || retry || (retryAt.IsZero() && read && changes != seen) {
			if err := l.Refresh(ctx); err != nil {
				if ctx.Err() != nil {
					return
				}
				retryWait = nextRetryWait(retryWait)
				retryAt = time.Now().Add(retryWait)
				l.logger.Error("Refreshing the lists failed; the catalog keeps serving what it has, and tries again", "error", err.Error(), "seconds", retryWait.Seconds())
			} else {
				if read {
					seen = changes
				}
				retryAt, retryWait = time.Time{}, 0
			}
			refreshedAt = time.Now()
		}
		next := wait
		if !retryAt.IsZero() {
			next = min(wait, retryWait)
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(next):
		}
	}
}
