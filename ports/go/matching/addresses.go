package matching

import (
	"fmt"
	"strings"
	"unicode"
)

// AddressState says what comparing the entered address with the listed ones showed.
type AddressState int

const (
	NotCompared AddressState = iota
	Corroborated
	Conflicting
)

func (s AddressState) String() string {
	switch s {
	case Corroborated:
		return "Corroborated"
	case Conflicting:
		return "Conflicting"
	}
	return "NotCompared"
}

// addressForm is an address read as section 5.8 says: its country, the places it names, its other words and numbers.
type addressForm struct {
	Raw            string
	Words          []*Token
	Numbers        []*Token
	Places         []int
	Country        string
	ImpliedCountry string
}

func (a *addressForm) EffectiveCountry() string {
	if a.Country != "" {
		return a.Country
	}
	return a.ImpliedCountry
}

func (a *addressForm) IsEmpty() bool {
	return len(a.Words) == 0 && len(a.Numbers) == 0 && len(a.Places) == 0 && a.EffectiveCountry() == ""
}

type addressParser struct {
	data      *MatcherData
	tokenizer *tokenizer
}

func (p *addressParser) Parse(raw string, sourceCountryCode string) *addressForm {
	country := ""
	if code := toUpperInvariant(sourceCountryCode); len([]rune(sourceCountryCode)) == 2 && p.data.CountryCodes[code] {
		country = code
	}
	words := Words(raw)
	if code, ok := trailingCode(raw); ok && p.data.CountryCodes[code] && len(words) > 0 {
		if country == "" {
			country = code
		}
		words = words[:len(words)-1]
	}

	tokens := make([]*Token, len(words))
	for i, w := range words {
		tokens[i] = p.tokenizer.Get(w)
	}
	var places []int
	namedCountry, impliedCountry := "", ""
	var remaining, numbers []*Token
	for i := 0; i < len(tokens); i++ {
		if code, length, ok := lookup(tokens, i, p.data.MaxCountryWords, p.data.Countries); ok {
			namedCountry = code
			i += length - 1
			continue
		}
		if pl, length, ok := lookup(tokens, i, p.data.MaxPlaceWords, p.data.Places); ok {
			places = append(places, pl.ID)
			if impliedCountry == "" {
				impliedCountry = pl.Country
			}
			i += length - 1
			continue
		}
		token := tokens[i]
		if token.IsNumber {
			numbers = append(numbers, token)
		} else if !p.data.AddressWords[token.Text] && !anyIn(token.Forms, p.data.AddressWords) && token.Len() > 1 {
			remaining = append(remaining, token)
		}
	}
	var distinctPlaces []int
	for _, id := range places {
		if !containsInt(distinctPlaces, id) {
			distinctPlaces = append(distinctPlaces, id)
		}
	}
	if country == "" {
		country = namedCountry
	}
	return &addressForm{raw, remaining, numbers, distinctPlaces, country, impliedCountry}
}

func anyIn(values []string, set map[string]bool) bool {
	for _, v := range values {
		if set[v] {
			return true
		}
	}
	return false
}

// lookup finds the longest name starting at start, on the words' own text or one of their Latin readings.
func lookup[T any](tokens []*Token, start, maxWords int, names map[string]T) (T, int, bool) {
	for length := min(maxWords, len(tokens)-start); length >= 1; length-- {
		span := tokens[start : start+length]
		maxForms := 0
		for _, t := range span {
			maxForms = max(maxForms, len(t.Forms))
		}
		for variant := -1; variant < maxForms; variant++ {
			parts := make([]string, len(span))
			for k, t := range span {
				if variant < 0 || variant >= len(t.Forms) {
					parts[k] = t.Text
				} else {
					parts[k] = t.Forms[variant]
				}
			}
			if value, ok := names[strings.Join(parts, " ")]; ok {
				return value, length, true
			}
		}
	}
	var zero T
	return zero, 0, false
}

// trailingCode is the reference's \b([A-Z]{2})\W*$: two capital ASCII letters, a word boundary before them, and
// nothing after them but non-word characters (\w in .NET: letters, marks, decimal digits, connector punctuation).
func trailingCode(raw string) (string, bool) {
	runes := []rune(raw)
	end := len(runes)
	for end > 0 && !isWordChar(runes[end-1]) {
		end--
	}
	if end < 2 {
		return "", false
	}
	a, b := runes[end-2], runes[end-1]
	if a < 'A' || a > 'Z' || b < 'A' || b > 'Z' {
		return "", false
	}
	if end-3 >= 0 && isWordChar(runes[end-3]) {
		return "", false
	}
	return string([]rune{a, b}), true
}

func isWordChar(c rune) bool {
	return IsLetter(c) || Is(unicode.Mn, c) || Is(unicode.Nd, c) || Is(unicode.Pc, c)
}

type addressComparison struct {
	State       AddressState
	Explanation string
}

type addressComparer struct {
	comparer *tokenComparer
	data     *MatcherData
}

func (c *addressComparer) Compare(entered *addressForm, listed []*addressForm) addressComparison {
	if entered == nil || entered.IsEmpty() {
		return addressComparison{NotCompared, "Name only: no address entered"}
	}
	var results []addressComparison
	for _, address := range listed {
		if !address.IsEmpty() {
			results = append(results, c.compareOne(entered, address))
		}
	}
	if len(results) == 0 {
		return addressComparison{NotCompared, "Address not compared: none listed"}
	}
	for _, r := range results {
		if r.State == Corroborated {
			return r
		}
	}
	for _, r := range results {
		if r.State == Conflicting {
			return r
		}
	}
	return addressComparison{NotCompared, "Address compared, nothing in common"}
}

type addressOnly struct {
	Aligned     int
	Matches     bool
	Explanation string
	Words       []WordAlignment
}

func (c *addressComparer) CompareAddressOnly(entered, listed *addressForm) addressOnly {
	if a, b := entered.EffectiveCountry(), listed.EffectiveCountry(); a != "" && b != "" && a != b {
		return addressOnly{0, false, "Address only: the countries differ", []WordAlignment{}}
	}
	var places []int
	for _, p := range entered.Places {
		if containsInt(listed.Places, p) && !containsInt(places, p) {
			places = append(places, p)
		}
	}
	words := c.alignedWords(entered.Words, listed.Words)
	var listedNumbers []string
	for _, n := range listed.Numbers {
		listedNumbers = append(listedNumbers, n.Text)
	}
	var numbers []string
	for _, n := range entered.Numbers {
		if contains(listedNumbers, n.Text) && !contains(numbers, n.Text) {
			numbers = append(numbers, n.Text)
		}
	}
	aligned := len(places) + len(words) + len(numbers)
	matches := aligned >= 3 && len(numbers) >= 1 && len(places)+len(words) >= 1
	parts := []WordAlignment{}
	for _, p := range places {
		parts = append(parts, WordAlignment{nil, strPtr(c.data.placeNames[p]), "SamePlace"})
	}
	for _, w := range words {
		position := -1
		for k, t := range entered.Words {
			if t == w[0] {
				position = k
				break
			}
		}
		parts = append(parts, WordAlignment{intPtr(position + 1), strPtr(w[1].Text), how(c.comparer.Compare(w[0], w[1]).Method)})
	}
	for _, n := range numbers {
		parts = append(parts, WordAlignment{nil, strPtr(n), "SameNumber"})
	}
	return addressOnly{aligned, matches, fmt.Sprintf("Address only: %d parts align (%d words or places, %d numbers)", aligned, len(places)+len(words), len(numbers)), parts}
}

func (c *addressComparer) compareOne(entered, listed *addressForm) addressComparison {
	enteredCountry, listedCountry := entered.EffectiveCountry(), listed.EffectiveCountry()
	if enteredCountry != "" && listedCountry != "" && enteredCountry != listedCountry {
		return addressComparison{Conflicting, fmt.Sprintf("Address: countries differ (%s, listed %s)", enteredCountry, listedCountry)}
	}
	for _, p := range entered.Places {
		if containsInt(listed.Places, p) {
			return addressComparison{Corroborated, fmt.Sprintf("Address: same place (%s)", c.data.placeNames[p])}
		}
	}
	if words := c.alignedWords(entered.Words, listed.Words); len(words) > 0 {
		parts := make([]string, len(words))
		for k, w := range words {
			parts[k] = w[0].Text + " ~ " + w[1].Text
		}
		return addressComparison{Corroborated, "Address: " + strings.Join(parts, ", ")}
	}
	if enteredCountry != "" && enteredCountry == listedCountry {
		return addressComparison{Corroborated, fmt.Sprintf("Address: country only (%s)", enteredCountry)}
	}
	return addressComparison{NotCompared, "Address compared, nothing in common"}
}

func (c *addressComparer) alignedWords(entered, listed []*Token) [][2]*Token {
	used := map[*Token]bool{}
	var pairs [][2]*Token
	for _, word := range entered {
		for _, candidate := range listed {
			if !used[candidate] && c.comparer.Compare(word, candidate).Aligned() {
				used[candidate] = true
				pairs = append(pairs, [2]*Token{word, candidate})
				break
			}
		}
	}
	return pairs
}
