package matching

import (
	"bytes"
	"crypto/sha256"
	"embed"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io/fs"
	"sort"
	"strings"
	"sync"
)

// The dictionaries, copied from src/SanctionAndForgive.Matching/Data at build time (build.ps1, the Dockerfile): one
// source of truth for every implementation.
//
//go:embed data
var dataFiles embed.FS

type legalForm struct {
	Class     string
	Ambiguous bool
}

type legalPhrase struct {
	Words []string
	Short string
}

type place struct {
	ID      int
	Country string
	Name    string
}

// MatcherData holds the dictionaries: loaded once, immutable. Every key is normalised like the text it's compared with.
type MatcherData struct {
	Hash       string
	tables     []*TransliterationTable
	tokens     *tokenFactory
	LegalForms map[string]legalForm
	// LegalPhrases: multi-word legal forms by first word, longest first.
	LegalPhrases map[string][]legalPhrase
	// generic: spelling → canonical word, in the reference's insertion order (NearGeneric takes the first that fits).
	genericKeys          []string
	Generic              map[string]string
	StopWords            map[string]bool
	Honorifics           map[string]bool
	AddressWords         map[string]bool
	SpellingGroups       map[string]int
	SpellingGroupMembers [][]string
	CognateGroups        map[string][]int
	CognateFolds         [][]string
	Countries            map[string]string
	CountryCodes         map[string]bool
	MaxCountryWords      int
	Places               map[string]place
	placeNames           map[int]string
	MaxPlaceWords        int
}

var (
	defaultData     *MatcherData
	defaultDataOnce sync.Once
)

// Data returns the dictionaries, loaded on first use.
func Data() *MatcherData {
	defaultDataOnce.Do(func() {
		data, err := loadData()
		if err != nil {
			panic(err)
		}
		defaultData = data
	})
	return defaultData
}

// NearGeneric returns the canonical generic word one edit from form (same first letter, 7 letters and up), or "".
func (d *MatcherData) NearGeneric(form string) string {
	formRunes := []rune(form)
	if len(formRunes) < 7 {
		return ""
	}
	for _, spelling := range d.genericKeys {
		s := []rune(spelling)
		if len(s) >= 7 && s[0] == formRunes[0] && abs(len(s)-len(formRunes)) <= 1 && damerauLevenshtein(s, formRunes, 2) == 1 {
			return d.Generic[spelling]
		}
	}
	return ""
}

// SpellingMembers returns the other spellings of spelling's group, or none.
func (d *MatcherData) SpellingMembers(spelling string) []string {
	if group, ok := d.SpellingGroups[spelling]; ok {
		return d.SpellingGroupMembers[group]
	}
	return nil
}

// --- An ordered JSON reader: objects keep their keys in file order, as .NET's JsonElement enumerates them. ---

type jsonObject struct {
	Keys   []string
	Values map[string]any
}

func parseOrdered(data []byte) (any, error) {
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	return readValue(decoder)
}

func readValue(decoder *json.Decoder) (any, error) {
	token, err := decoder.Token()
	if err != nil {
		return nil, err
	}
	switch delim := token.(type) {
	case json.Delim:
		switch delim {
		case '{':
			obj := &jsonObject{Values: map[string]any{}}
			for decoder.More() {
				key, err := decoder.Token()
				if err != nil {
					return nil, err
				}
				value, err := readValue(decoder)
				if err != nil {
					return nil, err
				}
				name := key.(string)
				if _, seen := obj.Values[name]; !seen {
					obj.Keys = append(obj.Keys, name)
				}
				obj.Values[name] = value
			}
			_, err := decoder.Token()
			return obj, err
		case '[':
			list := []any{}
			for decoder.More() {
				value, err := readValue(decoder)
				if err != nil {
					return nil, err
				}
				list = append(list, value)
			}
			_, err := decoder.Token()
			return list, err
		}
	}
	return token, nil
}

func strs(value any) []string {
	list, _ := value.([]any)
	out := make([]string, 0, len(list))
	for _, item := range list {
		out = append(out, item.(string))
	}
	return out
}

// key normalises a single-word dictionary entry like a token's text.
func key(value string) string { return strings.Join(Words(value), "") }

// keys returns a multi-word name's lookup keys: its normalised words joined by spaces, and for a name without Cyrillic
// also without diacritics.
func keys(name string) []string {
	k := strings.Join(Words(name), " ")
	out := []string{k}
	if !strings.ContainsFunc(k, IsCyrillic) {
		out = append(out, StripDiacritics(k))
	}
	return out
}

const resourcePrefix = "SanctionAndForgive.Matching.Data."

func loadData() (*MatcherData, error) {
	// The reference hashes its embedded resources by manifest name (SanctionAndForgive.Matching.Data.translit.ru.json),
	// in ordinal order: the same bytes in the same order give the same hash.
	type resource struct {
		name  string
		bytes []byte
	}
	var resources []resource
	err := fs.WalkDir(dataFiles, "data", func(path string, entry fs.DirEntry, err error) error {
		if err != nil || entry.IsDir() || !strings.HasSuffix(path, ".json") {
			return err
		}
		content, err := dataFiles.ReadFile(path)
		if err != nil {
			return err
		}
		resources = append(resources, resource{resourcePrefix + strings.ReplaceAll(strings.TrimPrefix(path, "data/"), "/", "."), content})
		return nil
	})
	if err != nil {
		return nil, err
	}
	sort.Slice(resources, func(i, j int) bool { return lessOrdinal(resources[i].name, resources[j].name) })
	hash := sha256.New()
	documents := map[string]any{}
	for _, r := range resources {
		hash.Write([]byte(r.name))
		hash.Write(r.bytes)
		doc, err := parseOrdered(r.bytes)
		if err != nil {
			return nil, fmt.Errorf("%s: %w", r.name, err)
		}
		documents[strings.TrimPrefix(r.name, resourcePrefix)] = doc
	}

	d := &MatcherData{Hash: hex.EncodeToString(hash.Sum(nil))[:16]}
	for _, file := range []string{"translit.ru.json", "translit.uk.json"} {
		d.tables = append(d.tables, readTable(documents[file].(*jsonObject)))
	}
	d.tokens = &tokenFactory{tables: d.tables}

	legal := documents["legal-forms.json"].(*jsonObject)
	d.LegalForms = map[string]legalForm{}
	forms := legal.Values["forms"].(*jsonObject)
	for _, class := range forms.Keys {
		form := forms.Values[class].(*jsonObject)
		for _, kind := range []struct {
			name      string
			ambiguous bool
		}{{"unambiguous", false}, {"ambiguous", true}} {
			if spellings, ok := form.Values[kind.name]; ok {
				for _, spelling := range strs(spellings) {
					d.LegalForms[key(spelling)] = legalForm{class, kind.ambiguous}
				}
			}
		}
	}
	phrases := legal.Values["phrases"].(*jsonObject)
	var phraseList []legalPhrase
	for _, name := range phrases.Keys {
		phraseList = append(phraseList, legalPhrase{Words: Words(name), Short: key(phrases.Values[name].(string))})
	}
	// OrderByDescending(words) is stable, then grouped by first word in order of appearance.
	sort.SliceStable(phraseList, func(i, j int) bool { return len(phraseList[i].Words) > len(phraseList[j].Words) })
	d.LegalPhrases = map[string][]legalPhrase{}
	for _, phrase := range phraseList {
		d.LegalPhrases[phrase.Words[0]] = append(d.LegalPhrases[phrase.Words[0]], phrase)
	}

	generic := documents["generic.json"].(*jsonObject).Values["words"].(*jsonObject)
	d.Generic = map[string]string{}
	for _, word := range generic.Keys {
		for _, spelling := range strs(generic.Values[word]) {
			k := key(spelling)
			if _, seen := d.Generic[k]; !seen {
				d.genericKeys = append(d.genericKeys, k)
			}
			d.Generic[k] = word
		}
	}

	words := documents["words.json"].(*jsonObject)
	set := func(property string) map[string]bool {
		out := map[string]bool{}
		for _, word := range strs(words.Values[property]) {
			for _, w := range Words(word) {
				out[w] = true
			}
		}
		return out
	}
	d.StopWords, d.Honorifics, d.AddressWords = set("stop"), set("honorifics"), set("addressWords")

	d.SpellingGroups = map[string]int{}
	for _, group := range documents["spelling-pairs.json"].(*jsonObject).Values["groups"].([]any) {
		var members []string
		for _, spelling := range strs(group) {
			if s := StripDiacritics(key(spelling)); !contains(members, s) {
				members = append(members, s)
			}
		}
		d.SpellingGroupMembers = append(d.SpellingGroupMembers, members)
	}
	for group, members := range d.SpellingGroupMembers {
		for _, spelling := range members {
			d.SpellingGroups[spelling] = group
		}
	}

	d.CognateGroups = map[string][]int{}
	for _, group := range documents["cognates.json"].(*jsonObject).Values["groups"].([]any) {
		var folds []string
		for _, name := range strs(group) {
			for _, fold := range d.tokens.Create(key(name)).Folds {
				if !contains(folds, fold) {
					folds = append(folds, fold)
				}
			}
		}
		d.CognateFolds = append(d.CognateFolds, folds)
	}
	for group, folds := range d.CognateFolds {
		for _, fold := range folds {
			if !containsInt(d.CognateGroups[fold], group) {
				d.CognateGroups[fold] = append(d.CognateGroups[fold], group)
			}
		}
	}

	d.Countries, d.CountryCodes = map[string]string{}, map[string]bool{}
	for _, item := range documents["countries.json"].([]any) {
		country := item.(*jsonObject)
		code := country.Values["code"].(string)
		d.CountryCodes[code] = true
		for _, name := range strs(country.Values["names"]) {
			for _, k := range keys(name) {
				if _, seen := d.Countries[k]; !seen {
					d.Countries[k] = code
				}
			}
		}
	}

	d.Places, d.placeNames = map[string]place{}, map[int]string{}
	for id, item := range documents["places.json"].([]any) {
		p := item.(*jsonObject)
		country := p.Values["country"].(string)
		names := strs(p.Values["names"])
		d.placeNames[id] = names[0]
		for _, name := range names {
			for _, k := range keys(name) {
				if _, seen := d.Places[k]; !seen {
					d.Places[k] = place{id, country, names[0]}
				}
			}
		}
	}
	for k := range d.Countries {
		d.MaxCountryWords = max(d.MaxCountryWords, strings.Count(k, " ")+1)
	}
	for k := range d.Places {
		d.MaxPlaceWords = max(d.MaxPlaceWords, strings.Count(k, " ")+1)
	}
	return d, nil
}

func readTable(table *jsonObject) *TransliterationTable {
	letters := func(value any) map[rune]string {
		out := map[rune]string{}
		obj := value.(*jsonObject)
		for _, k := range obj.Keys {
			out[[]rune(k)[0]] = obj.Values[k].(string)
		}
		return out
	}
	return &TransliterationTable{Language: table.Values["language"].(string), Letters: letters(table.Values["letters"]), Initial: letters(table.Values["initial"])}
}

func containsInt(list []int, value int) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}

func abs(v int) int {
	if v < 0 {
		return -v
	}
	return v
}

// lessOrdinal compares as .NET's StringComparer.Ordinal does (UTF-16 code units). For text in the Basic
// Multilingual Plane, code point order is the same, and Go's byte order of UTF-8 is code point order.
func lessOrdinal(a, b string) bool { return a < b }
