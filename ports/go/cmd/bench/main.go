// Bench: loads the lists from the database as the API does, then screens the load test's inputs (tests/load/inputs.csv,
// written by the .NET CLI's load-inputs) and reports the time per screening and the memory. A development tool, as
// the reference's evaluate: it never runs in production.
//
//	go run ./cmd/bench -inputs ../../tests/load/inputs.csv -rounds 5 -cpuprofile cpu.pprof -dump results.json
package main

import (
	"context"
	"encoding/csv"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log/slog"
	"os"
	"runtime"
	"runtime/pprof"
	"slices"
	"strings"
	"time"

	"sanctionandforgive/api"
	"sanctionandforgive/matching"
	"sanctionandforgive/store"
)

func main() {
	inputs := flag.String("inputs", "../../tests/load/inputs.csv", "the inputs (receiverName,companyName,address,kind)")
	rounds := flag.Int("rounds", 5, "how many times to screen every input")
	cpuProfile := flag.String("cpuprofile", "", "write a CPU profile of the screenings here")
	memProfile := flag.String("memprofile", "", "write an allocation profile of the screenings here")
	heapProfile := flag.String("heapprofile", "", "write a profile of the memory the lists hold here, once loaded")
	only := flag.String("lists", "", "only these lists (codes separated by commas), loaded straight from the database; all by default")
	dump := flag.String("dump", "", "write every result here (JSON lines), to compare before and after a change")
	canonicalDump := flag.String("canonical", "", "write every result here in the ports' common form (ports/rust/src/canonical.rs)")
	export := flag.String("export", "", "write the loaded lists here (JSON lines), for the Rust port's bench")
	flag.Parse()

	db, err := store.Open(os.Getenv("ConnectionStrings__Lists"))
	check(err)
	started := time.Now()
	var lists []matching.ScreenedList
	if *only == "" {
		catalog := api.NewCatalog()
		loader := api.NewLoader(db, catalog, slog.New(slog.NewTextHandler(io.Discard, nil)))
		check(loader.Refresh(context.Background()))
		for _, list := range catalog.Lists() {
			if list.IsSearched() {
				lists = append(lists, matching.ScreenedList{Code: list.Code, Name: list.Name, Index: list.Index})
			}
		}
	} else {
		sources, err := store.Sources(context.Background(), db)
		check(err)
		for _, source := range sources {
			if source.ActiveImportID == nil || !slices.Contains(strings.Split(*only, ","), source.Code) {
				continue
			}
			subjects, err := store.ActiveSubjects(context.Background(), db, source.Code, *source.ActiveImportID)
			check(err)
			listed := make([]*matching.ListedSubject, len(subjects))
			for i, subject := range subjects {
				listed[i] = api.ToListed(subject)
			}
			lists = append(lists, matching.ScreenedList{Code: source.Code, Name: source.Name, Index: matching.Build(listed)})
		}
	}
	loaded := time.Since(started)
	aliases := 0
	for _, list := range lists {
		aliases += list.Index.AliasCount()
	}
	runtime.GC()
	var memory runtime.MemStats
	runtime.ReadMemStats(&memory)
	fmt.Printf("%d lists, %d aliases, loaded in %.1f s; heap %d MiB in use, %d MiB from the OS\n",
		len(lists), aliases, loaded.Seconds(), memory.HeapInuse>>20, memory.Sys>>20)

	if *heapProfile != "" {
		file, err := os.Create(*heapProfile)
		check(err)
		check(pprof.Lookup("heap").WriteTo(file, 0))
		check(file.Close())
	}

	requests := read(*inputs)
	screener := matching.NewScreener(matching.DefaultOptions())

	if *export != "" {
		file, err := os.Create(*export)
		check(err)
		encoder := json.NewEncoder(file)
		for _, list := range lists {
			check(encoder.Encode(exported(list)))
		}
		check(file.Close())
	}
	if *canonicalDump != "" {
		file, err := os.Create(*canonicalDump)
		check(err)
		encoder := json.NewEncoder(file)
		encoder.SetEscapeHTML(false)
		for _, request := range requests {
			result, err := screener.Screen(request, lists, matching.EmptyAllowList)
			check(err)
			check(encoder.Encode(canonical(result)))
		}
		check(file.Close())
	}

	if *dump != "" {
		file, err := os.Create(*dump)
		check(err)
		encoder := json.NewEncoder(file)
		for _, request := range requests {
			result, err := screener.Screen(request, lists, matching.EmptyAllowList)
			check(err)
			check(encoder.Encode(result))
		}
		check(file.Close())
	}

	if *cpuProfile != "" {
		file, err := os.Create(*cpuProfile)
		check(err)
		check(pprof.StartCPUProfile(file))
		defer pprof.StopCPUProfile()
	}
	runtime.ReadMemStats(&memory)
	allocated, collections := memory.TotalAlloc, memory.NumGC
	times := make([]time.Duration, 0, len(requests)**rounds)
	started = time.Now()
	for range *rounds {
		for _, request := range requests {
			clock := time.Now()
			_, err := screener.Screen(request, lists, matching.EmptyAllowList)
			check(err)
			times = append(times, time.Since(clock))
		}
	}
	total := time.Since(started)
	runtime.ReadMemStats(&memory)
	count := len(times)
	if count == 0 {
		return
	}
	slices.Sort(times)
	fmt.Printf("%d screenings: %.2f ms mean, %.2f ms p50, %.2f ms p95; %.0f KiB allocated and %.2f collections per screening\n",
		count, float64(total.Microseconds())/1000/float64(count), ms(times[count/2]), ms(times[count*95/100]),
		float64(memory.TotalAlloc-allocated)/1024/float64(count), float64(memory.NumGC-collections)/float64(count))

	if *memProfile != "" {
		file, err := os.Create(*memProfile)
		check(err)
		check(pprof.Lookup("allocs").WriteTo(file, 0))
		check(file.Close())
	}
}

func read(path string) []matching.ScreeningRequest {
	file, err := os.Open(path)
	check(err)
	defer file.Close()
	records, err := csv.NewReader(file).ReadAll()
	check(err)
	var requests []matching.ScreeningRequest
	for _, record := range records[1:] {
		requests = append(requests, matching.ScreeningRequest{ReceiverName: record[0], CompanyName: record[1], Address: record[2]})
	}
	return requests
}

func ms(d time.Duration) float64 { return float64(d.Microseconds()) / 1000 }

func check(err error) {
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

// exported is a list as the Rust bench reads it: each subject as an array.
func exported(list matching.ScreenedList) map[string]any {
	subjects := []any{}
	for _, s := range list.Index.Subjects() {
		names := []any{}
		for _, n := range s.Names {
			names = append(names, []any{n.FullName, n.IsWeak})
		}
		addresses := []any{}
		for _, a := range s.Addresses {
			addresses = append(addresses, []any{a.Raw, a.CountryCode})
		}
		subjects = append(subjects, []any{s.ExternalID, int(s.Kind), s.PrimaryName, names, addresses, s.Programme, s.SourceURL})
	}
	return map[string]any{"code": list.Code, "name": list.Name, "subjects": subjects}
}

// canonical is a result in the ports' common form (ports/rust/src/canonical.rs): plain arrays, every field the API
// shows.
func canonical(r *matching.ScreeningResult) []any {
	strs := func(values []string) []any {
		out := []any{}
		for _, v := range values {
			out = append(out, v)
		}
		return out
	}
	subject := func(m *matching.SubjectMatch) any {
		if m == nil {
			return nil
		}
		words := []any{}
		for _, w := range m.Words {
			words = append(words, []any{w.Entered, w.Listed, w.How})
		}
		return []any{m.Subject.ExternalID, m.Percent, m.Outcome.String(), m.Alias, strs(m.Alignment), m.AddressState.String(), words, strs(m.Notes)}
	}
	entries := []any{}
	for _, e := range r.Entries {
		cells := []any{}
		for _, c := range e.Cells {
			others := []any{}
			for i := range c.Others {
				others = append(others, subject(&c.Others[i]))
			}
			var outcome any
			if c.Outcome != nil {
				outcome = c.Outcome.String()
			}
			cells = append(cells, []any{c.List, c.Checked, c.Percent, outcome, subject(c.Best), others, c.Allowed})
		}
		var allowed any
		if e.AllowedBy != nil {
			allowed = e.AllowedBy.ID
		}
		entries = append(entries, []any{e.Kind.String(), e.Input, e.Compared, allowed, cells})
	}
	return []any{r.Outcome.String(), r.MatcherVersion, entries, r.AllowListVersion}
}
