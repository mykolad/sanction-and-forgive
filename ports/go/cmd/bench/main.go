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
