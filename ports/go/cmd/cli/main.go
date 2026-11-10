// The CLI: the Go port of SanctionAndForgive.Cli (imports, manual records, review, status, history, probe). The same
// commands, options, output and exit codes: 0 success, 1 a source failed, was rejected or held, 2 bad arguments.
package main

import (
	"context"
	"errors"
	"fmt"
	"math"
	"net/http"
	"os"
	"os/signal"
	"regexp"
	"strconv"
	"strings"
	"time"

	"sanctionandforgive/lists"
	"sanctionandforgive/memlimit"
	"sanctionandforgive/store"
)

const usage = `Usage (tools/lists.ps1 wraps these):
  probe        [--source <code>|all] [--file <path>] [--keep <directory>]
  import       --source <code>|all [--file <path>] [--force]
  status
  history      [--source <code>]
  review                         (updates held for review: a drop of more than 20%)
  confirm      --import <id>     (make a held update active)
  reject       --import <id>     (discard a held update)
  source-add   --code <code> --name <name>
  add          --source <code> --file <records.csv>
  remove       --source <code> --external-id <id>

Feeds: eu, ofac-sdn, ofac-cons, un, uk, us-export, ch-seco, ca-sema, au-dfat, ua-nsdc, ua-sfms.
Configuration (environment variables): ConnectionStrings__Lists, and Lists__EuToken
(or LISTS_EU_TOKEN; default: the public token).`

var known = map[string]bool{"source": true, "file": true, "keep": true, "force": true, "code": true, "name": true, "external-id": true, "count": true, "import": true}

func parse(args []string) (map[string]*string, error) {
	values := map[string]*string{}
	for i := 0; i < len(args); i++ {
		if !strings.HasPrefix(args[i], "--") || !known[args[i][2:]] {
			return nil, fmt.Errorf("Unknown option %s.", args[i])
		}
		name := args[i][2:]
		if name == "force" {
			values[name] = nil
			continue
		}
		if i+1 >= len(args) || strings.HasPrefix(args[i+1], "--") {
			return nil, fmt.Errorf("%s needs a value.", args[i])
		}
		i++
		value := args[i]
		values[name] = &value
	}
	return values, nil
}

func main() {
	memlimit.Apply()
	os.Exit(run())
}

func run() int {
	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, usage)
		return 2
	}
	options, err := parse(os.Args[2:])
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error()+"\n"+usage)
		return 2
	}
	get := func(name string) string {
		if v := options[name]; v != nil {
			return *v
		}
		return ""
	}
	_, force := options["force"]
	euToken := os.Getenv("Lists__EuToken")
	if euToken == "" {
		euToken = os.Getenv("LISTS_EU_TOKEN")
	}
	if euToken == "" {
		euToken = lists.EuPublicToken
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	downloader := &lists.Downloader{Client: &http.Client{}, Timeout: 10 * time.Minute, Retry: lists.DefaultRetry, Version: version(), Log: os.Stdout}

	if os.Args[1] == "probe" {
		return probe(ctx, downloader, euToken, get("source"), get("file"))
	}
	connection := os.Getenv("ConnectionStrings__Lists")
	if connection == "" {
		fmt.Fprintln(os.Stderr, "No connection string: set ConnectionStrings__Lists (tools/lists.ps1 does).")
		return 2
	}
	db, err := store.Open(connection)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return 2
	}
	defer db.Close()
	c := &commands{imports: &store.Imports{DB: db, Now: time.Now}, db: db, downloader: downloader, euToken: euToken}
	importID := func() (int, bool) { id, err := strconv.Atoi(get("import")); return id, err == nil }

	switch command := os.Args[1]; {
	case command == "import" && get("source") != "":
		return c.importFeeds(ctx, get("source"), get("file"), force)
	case command == "status":
		return c.status(ctx)
	case command == "history":
		return c.history(ctx, get("source"))
	case command == "review":
		return c.reviewList(ctx)
	case command == "confirm" || command == "reject":
		if id, ok := importID(); ok {
			return c.review(ctx, id, command == "confirm")
		}
	case command == "source-add" && get("code") != "" && get("name") != "":
		return c.sourceAdd(ctx, get("code"), get("name"))
	case command == "add" && get("source") != "" && get("file") != "":
		return c.add(ctx, get("source"), get("file"))
	case command == "remove" && get("source") != "" && get("external-id") != "":
		return c.remove(ctx, get("source"), get("external-id"))
	}
	fmt.Fprintf(os.Stderr, "Missing or unknown arguments for %s.\n%s\n", os.Args[1], usage)
	return 2
}

func version() string {
	if v := os.Getenv("App__Version"); v != "" {
		return v
	}
	return "dev"
}

type commands struct {
	imports    *store.Imports
	db         interface{ Close() error }
	downloader *lists.Downloader
	euToken    string
}

func (c *commands) importFeeds(ctx context.Context, code, file string, force bool) int {
	var sources []*lists.ListSource
	if code == "all" {
		if file != "" {
			fmt.Println("--file needs a single --source: one file is one feed.")
			return 2
		}
		for i := range lists.Sources {
			if lists.Sources[i].Status == lists.Active {
				sources = append(sources, &lists.Sources[i])
			}
		}
	} else if source := lists.FindSource(code); source != nil {
		sources = []*lists.ListSource{source}
	} else {
		fmt.Printf("%s is not a feed. Manual sources change with add and remove.\n", code)
		return 2
	}
	failed := false
	for _, source := range sources {
		failed = !c.importOne(ctx, source, file, force) || failed
	}
	if failed {
		return 1
	}
	return 0
}

func toStored(parsed []lists.ParsedSubject) []store.Subject {
	out := make([]store.Subject, len(parsed))
	for i, p := range parsed {
		s := store.Subject{ExternalID: p.ExternalID, Type: int(p.Type), PrimaryName: p.PrimaryName, Programmes: p.Programmes, ListedOn: p.ListedOn, Remarks: p.Remarks, SourceURL: p.SourceURL}
		for _, n := range p.Names {
			s.Names = append(s.Names, store.SubjectName{FullName: n.FullName, IsWeak: n.IsWeak, Script: int(n.Script)})
		}
		for _, a := range p.Addresses {
			s.Addresses = append(s.Addresses, store.SubjectAddress{Raw: a.Raw, CountryCode: a.CountryCode})
		}
		out[i] = s
	}
	return out
}

func (c *commands) importOne(ctx context.Context, source *lists.ListSource, file string, force bool) bool {
	parser := lists.ParserFor(source.Code)
	if parser == nil {
		fmt.Printf("%s: no parser yet (%s); see docs/sources.md.\n", source.Code, source.Status)
		return false
	}
	path := file
	var record *store.SourceRecord
	fail := func(err error) bool {
		fmt.Printf("%s: FAILED: %s: %s\n", source.Code, kind(err), err.Error())
		if record != nil {
			if stored, findErr := c.imports.FindSource(context.Background(), source.Code); findErr == nil && stored != nil {
				if recordErr := c.imports.RecordFailure(context.Background(), stored, kind(err)+": "+err.Error()); recordErr != nil {
					fmt.Printf("%s: the failure couldn't be recorded either (%s).\n", source.Code, kind(recordErr))
				}
			}
		}
		return false
	}
	var url *string
	if source.URL != "" {
		masked := strings.ReplaceAll(source.URL, "{token}", "…")
		url = &masked
	}
	age := source.MaxAgeHours
	record, err := c.imports.EnsureSource(ctx, source.Code, source.Name, url, &age)
	if err != nil {
		return fail(err)
	}
	if path == "" {
		path, err = c.downloader.Download(ctx, source.Code, strings.ReplaceAll(source.URL, "{token}", c.euToken))
		if err != nil {
			return fail(err)
		}
		defer os.Remove(path)
	}
	reader, err := os.Open(path)
	if err != nil {
		return fail(err)
	}
	parsed, err := parser(reader)
	reader.Close()
	if err != nil {
		return fail(err)
	}
	outcome, err := c.imports.Import(ctx, record, toStored(parsed), force)
	if err != nil {
		return fail(err)
	}
	was := "none"
	if outcome.PreviousCount != nil {
		was = strconv.Itoa(*outcome.PreviousCount)
	}
	// "Rejected" and "HeldForReview" in this line are what the import alert looks for (infra/platform.bicep).
	fmt.Printf("%s: %s, %d records (was %s). %s\n", source.Code, outcome.Result, outcome.RecordCount, was, outcome.Message)
	if outcome.Result == store.HeldForReview {
		fmt.Printf("%s: check the publisher's site, then confirm -Import %d or reject -Import %d.\n", source.Code, *outcome.ImportID, *outcome.ImportID)
	}
	return outcome.Result != store.Rejected && outcome.Result != store.HeldForReview
}

// kind names an error's kind, as the reference prints an exception's type.
func kind(err error) string {
	var format *lists.FormatError
	switch {
	case errors.As(err, &format):
		return "ListFormatException"
	case errors.Is(err, context.DeadlineExceeded):
		return "TimeoutException"
	}
	// The innermost error's type (a wrapped error is named by what it wraps).
	for inner := errors.Unwrap(err); inner != nil; inner = errors.Unwrap(err) {
		err = inner
	}
	return strings.TrimPrefix(fmt.Sprintf("%T", err), "*")
}

func n0(v int) string {
	s := strconv.Itoa(abs(v))
	var b strings.Builder
	for i, c := range s {
		if i > 0 && (len(s)-i)%3 == 0 {
			b.WriteByte(',')
		}
		b.WriteRune(c)
	}
	if v < 0 {
		return "-" + b.String()
	}
	return b.String()
}

func abs(v int) int {
	if v < 0 {
		return -v
	}
	return v
}

func when(t *time.Time) string {
	if t == nil {
		return ""
	}
	return t.UTC().Format("2006-01-02 15:04")
}

func (c *commands) status(ctx context.Context) int {
	statuses, err := c.imports.Status(ctx)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return 1
	}
	now := time.Now()
	fmt.Println("source       records  imported (UTC)     last checked (UTC)  age     max   state         last import")
	knownCodes := map[string]bool{}
	for _, s := range statuses {
		knownCodes[s.Code] = true
		age, current := "     -", false
		if s.LastCheckedAt != nil {
			hours := now.Sub(*s.LastCheckedAt).Hours()
			age = fmt.Sprintf("%5.1fh", math.Round(hours*10)/10)
			current = s.RecordCount != nil && (s.MaxAgeHours == nil || hours <= float64(*s.MaxAgeHours))
		} else {
			current = s.RecordCount != nil && s.MaxAgeHours == nil
		}
		maxAge := "-"
		if s.MaxAgeHours != nil {
			maxAge = strconv.Itoa(*s.MaxAgeHours) + "h"
		}
		state := "Not checked"
		switch {
		case current:
			state = "Current"
		case s.RecordCount == nil:
			state = "Not imported"
		}
		last := "-"
		if s.LastImportStatus != nil {
			last = *s.LastImportStatus
			if s.LastImportMessage != nil {
				last += ": " + *s.LastImportMessage
			}
		}
		records := ""
		if s.RecordCount != nil {
			records = n0(*s.RecordCount)
		}
		fmt.Printf("%-10s %9s  %-18s  %-18s  %s  %4s  %-12s  %s\n", s.Code, records, when(s.ImportedAt), when(s.LastCheckedAt), age, maxAge, state, last)
	}
	for _, feed := range lists.Sources {
		if !knownCodes[feed.Code] {
			fmt.Printf("%-10s         -  never imported (%s)\n", feed.Code, feed.Status)
		}
	}
	return 0
}

func count(v *int) string {
	if v == nil {
		return "-"
	}
	return n0(*v)
}

func (c *commands) history(ctx context.Context, code string) int {
	entries, err := store.HistoryPerSource(ctx, c.imports.DB, code, 20)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return 1
	}
	fmt.Println("source     import  started (UTC)     status         records    added  removed  changed  note")
	for _, e := range entries {
		status := e.Status
		if e.Active {
			status = "Active"
		}
		reviewed := ""
		if e.ReviewedBy != nil {
			reviewed = fmt.Sprintf(" (reviewed by %s, %s)", *e.ReviewedBy, when(e.ReviewedAt))
		}
		message := ""
		if e.Message != nil {
			message = *e.Message
		}
		fmt.Printf("%-10s %6d  %s  %-13s %8s %8s %8s %8s  %s%s\n", e.SourceCode, e.ID, when(&e.StartedAt), status, n0(e.RecordCount), count(e.Added), count(e.Removed), count(e.Changed), message, reviewed)
	}
	return 0
}

func (c *commands) reviewList(ctx context.Context) int {
	held, err := store.Held(ctx, c.imports.DB)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return 1
	}
	if len(held) == 0 {
		fmt.Println("No updates are held for review.")
		return 0
	}
	statuses, err := c.imports.Status(ctx)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return 1
	}
	for _, h := range held {
		active := ""
		for _, s := range statuses {
			if s.Code == h.SourceCode && s.RecordCount != nil {
				active = n0(*s.RecordCount)
			}
		}
		message := ""
		if h.Message != nil {
			message = *h.Message
		}
		fmt.Printf("%s: import %d, downloaded %s UTC, %s records; active list %s records. %s\n", h.SourceCode, h.ID, when(&h.StartedAt), n0(h.RecordCount), active, message)
	}
	fmt.Println("Check the publisher's site, then: confirm -Import <id>, or reject -Import <id>.")
	return 0
}

func (c *commands) review(ctx context.Context, id int, confirm bool) int {
	result, err := c.imports.Review(ctx, id, confirm, c.imports.CurrentLogin(ctx))
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return 1
	}
	switch result {
	case store.ReviewConfirmed:
		fmt.Printf("Import %d confirmed: it's the active list now. The API picks it up within its refresh interval.\n", id)
	case store.ReviewRejected:
		fmt.Printf("Import %d rejected: its records are deleted; the active list is unchanged.\n", id)
	case store.ReviewNotFound:
		fmt.Printf("There's no import %d.\n", id)
	default:
		fmt.Printf("Import %d isn't held for review (see history).\n", id)
	}
	if result == store.ReviewConfirmed || result == store.ReviewRejected {
		return 0
	}
	return 1
}

var codePattern = regexp.MustCompile(`^[a-z0-9][a-z0-9-]{1,31}$`)

func (c *commands) sourceAdd(ctx context.Context, code, name string) int {
	code = strings.TrimSpace(code)
	if !codePattern.MatchString(code) {
		fmt.Printf("'%s' isn't a valid code: use 2-32 lower-case letters, digits and hyphens.\n", code)
		return 2
	}
	existing, err := c.imports.FindSource(ctx, code)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return 1
	}
	if lists.FindSource(code) != nil || (existing != nil && existing.URL != nil) {
		fmt.Printf("%s is a feed's code; pick another for a manual source.\n", code)
		return 2
	}
	if _, err := c.imports.EnsureSource(ctx, code, name, nil, nil); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return 1
	}
	fmt.Printf("Manual source %s (%s) is ready. Add records with: add -Source %s -File records.csv\n", code, name, code)
	return 0
}

func (c *commands) manualSource(ctx context.Context, code string) *store.SourceRecord {
	source, err := c.imports.FindSource(ctx, code)
	if err != nil || source == nil || source.URL != nil || lists.FindSource(code) != nil {
		fmt.Printf("%s is not a manual source. Create one with: source-add -Code %s -Name \"…\"\n", code, code)
		return nil
	}
	return source
}

func (c *commands) add(ctx context.Context, code, file string) int {
	source := c.manualSource(ctx, code)
	if source == nil {
		return 2
	}
	reader, err := os.Open(file)
	if err != nil {
		fmt.Printf("%s: FAILED: %s\n", code, err.Error())
		return 1
	}
	parsed, err := lists.ReadManualCsv(reader)
	reader.Close()
	if err != nil {
		fmt.Printf("%s: FAILED: %s: %s\n", code, kind(err), err.Error())
		return 1
	}
	records := toStored(parsed)
	outcome, err := c.imports.Change(ctx, source, func(current []store.Subject) []store.Subject {
		// Upsert by external id: a replaced record keeps its place, a new one goes at the end.
		position := map[string]int{}
		for i, s := range current {
			position[s.ExternalID] = i
		}
		for _, r := range records {
			if i, ok := position[r.ExternalID]; ok {
				current[i] = r
			} else {
				position[r.ExternalID] = len(current)
				current = append(current, r)
			}
		}
		return current
	})
	if err != nil {
		fmt.Printf("%s: FAILED: %s\n", code, err.Error())
		return 1
	}
	return report(code, fmt.Sprintf("%d records upserted", len(records)), outcome)
}

func (c *commands) remove(ctx context.Context, code, externalID string) int {
	source := c.manualSource(ctx, code)
	if source == nil {
		return 2
	}
	found := false
	outcome, err := c.imports.Change(ctx, source, func(current []store.Subject) []store.Subject {
		kept := current[:0]
		for _, s := range current {
			if s.ExternalID == externalID {
				found = true
			} else {
				kept = append(kept, s)
			}
		}
		return kept
	})
	if err != nil {
		fmt.Printf("%s: FAILED: %s\n", code, err.Error())
		return 1
	}
	if !found {
		fmt.Printf("%s has no record %s; nothing changed.\n", code, externalID)
		return 1
	}
	return report(code, "removed "+externalID, outcome)
}

func report(code, what string, outcome *store.ImportOutcome) int {
	if outcome.Result == store.Rejected {
		fmt.Printf("%s: NOT changed: %s. A list can't be emptied: it keeps at least one record.\n", code, outcome.Message)
		return 1
	}
	fmt.Printf("%s: %s, %d in the list (%s).\n", code, what, outcome.RecordCount, outcome.Result)
	return 0
}

// probe downloads (or reads), parses and counts each feed; it writes nothing else. Exit 1 if an Active feed failed.
func probe(ctx context.Context, downloader *lists.Downloader, euToken, code, file string) int {
	var sources []*lists.ListSource
	if code == "" || code == "all" {
		if file != "" {
			fmt.Fprintln(os.Stderr, "--file needs a single --source.")
			return 2
		}
		for i := range lists.Sources {
			sources = append(sources, &lists.Sources[i])
		}
	} else if source := lists.FindSource(code); source != nil {
		sources = []*lists.ListSource{source}
	} else {
		fmt.Fprintf(os.Stderr, "Unknown source %s.\n%s\n", code, usage)
		return 2
	}
	fmt.Println("source     status        subjects  indiv  entity  unknown   names   weak  cyrillic  other  addresses  seconds  result")
	failed := false
	for _, source := range sources {
		prefix := fmt.Sprintf("%-10s %-12s", source.Code, source.Status)
		parser := lists.ParserFor(source.Code)
		if parser == nil {
			fmt.Printf("%s %8s  no parser yet: needs access (docs/sources.md)\n", prefix, "-")
			continue
		}
		started := time.Now()
		path := file
		if path == "" {
			if source.URL == "" {
				fmt.Printf("%s %8s  no URL: pass --file\n", prefix, "-")
				continue
			}
			downloaded, err := downloader.Download(ctx, source.Code, strings.ReplaceAll(source.URL, "{token}", euToken))
			if err != nil {
				fmt.Printf("%s FAILED: %s\n", prefix, err.Error())
				failed = failed || source.Status == lists.Active
				continue
			}
			defer os.Remove(downloaded)
			path = downloaded
		}
		reader, err := os.Open(path)
		if err != nil {
			fmt.Printf("%s FAILED: %s\n", prefix, err.Error())
			failed = failed || source.Status == lists.Active
			continue
		}
		parsed, err := parser(reader)
		reader.Close()
		if err != nil {
			fmt.Printf("%s FAILED: %s\n", prefix, err.Error())
			failed = failed || source.Status == lists.Active
			continue
		}
		var individuals, entities, unknown, names, weak, cyrillic, other, addresses int
		for _, p := range parsed {
			switch p.Type {
			case lists.Individual:
				individuals++
			case lists.Entity:
				entities++
			default:
				unknown++
			}
			for _, n := range p.Names {
				names++
				if n.IsWeak {
					weak++
				}
				switch n.Script {
				case lists.Cyrillic:
					cyrillic++
				case lists.Other:
					other++
				}
			}
			addresses += len(p.Addresses)
		}
		result := "parsed"
		if len(parsed) == 0 {
			result = "parsed, but no subjects"
			failed = failed || source.Status == lists.Active
		}
		fmt.Printf("%s %9s %6s %7s %8s %7s %6s %9s %6s %10s %8.1f  %s\n", prefix, n0(len(parsed)), n0(individuals), n0(entities), n0(unknown), n0(names), n0(weak), n0(cyrillic), n0(other), n0(addresses), time.Since(started).Seconds(), result)
	}
	if failed {
		return 1
	}
	return 0
}
