// Package matching is the Go port of SanctionAndForgive.Matching: pure, deterministic, no I/O beyond its embedded
// dictionaries. Every rule follows the .NET reference exactly (docs/conformance.md): the golden files compare results
// field for field. Strings are handled as runes; .NET counts UTF-16 units, which is the same for every character the
// lists and the dictionaries use (the Basic Multilingual Plane).
package matching

import (
	"strings"
	"unicode"

	"golang.org/x/text/unicode/norm"
)

// Token is one normalised word with every Latin reading of it. Tokens are interned per index.
type Token struct {
	Text       string
	Forms      []string
	Folds      []string
	IsCyrillic bool
	IsNumber   bool
	length     int
}

// Len is the number of letters (or digits) in the token.
func (t *Token) Len() int { return t.length }

// Key is the key for weights and postings: the first fold key, or the text itself.
func (t *Token) Key() string {
	if len(t.Folds) > 0 {
		return t.Folds[0]
	}
	return t.Text
}

func (t *Token) String() string { return t.Text }

// RawToken is a token as the tokenizer produced it; Initials holds a run of single letters as separate tokens.
type RawToken struct {
	Token    *Token
	Initials []*Token
}

const apostrophes = "'’ʼʻ`′‘"

// Words returns the normalised words of value (NFKC, lower case, apostrophes inside words deleted, punctuation as
// separators, & as "and").
func Words(value string) []string {
	text := []rune(toLowerInvariant(norm.NFKC.String(value)))
	words := []string{}
	var word []rune
	flush := func() {
		if len(word) > 0 {
			words = append(words, string(word))
			word = word[:0]
		}
	}
	for i := 0; i < len(text); i++ {
		c := text[i]
		switch {
		case isLetterOrDigit(c) || unicode.Is(unicode.Mn, c) || unicode.Is(unicode.Mc, c):
			word = append(word, c)
		case strings.ContainsRune(apostrophes, c) && len(word) > 0 && i+1 < len(text) && unicode.IsLetter(text[i+1]):
			// Inside a word: deleted, so the word stays whole.
		default:
			flush()
			if c == '&' {
				words = append(words, "and")
			}
		}
	}
	flush()
	return words
}

// ToLowerInvariant maps each character to its simple lower case, as .NET's ToLowerInvariant does: like Go's, except
// that .NET leaves the Turkish capital İ (U+0130) as it is.
func ToLowerInvariant(s string) string { return strings.Map(LowerInvariant, s) }

// ToUpperInvariant is .NET's ToUpperInvariant: like Go's, except that the Turkish dotless ı (U+0131) stays as it is.
func ToUpperInvariant(s string) string { return strings.Map(UpperInvariant, s) }

// LowerInvariant is .NET's char.ToLowerInvariant.
func LowerInvariant(c rune) rune {
	if c == 'İ' {
		return c
	}
	return unicode.ToLower(c)
}

// UpperInvariant is .NET's char.ToUpperInvariant.
func UpperInvariant(c rune) rune {
	if c == 'ı' {
		return c
	}
	return unicode.ToUpper(c)
}

func toLowerInvariant(s string) string { return ToLowerInvariant(s) }

func toUpperInvariant(s string) string { return ToUpperInvariant(s) }

// isLetterOrDigit is .NET's char.IsLetterOrDigit: a letter (L) or a decimal digit (Nd).
func isLetterOrDigit(c rune) bool {
	return unicode.IsLetter(c) || unicode.Is(unicode.Nd, c)
}

// StripDiacritics returns a Latin word without diacritics: Ştefan → stefan, straße → strasse.
func StripDiacritics(value string) string {
	var b strings.Builder
	for _, c := range norm.NFD.String(value) {
		switch c {
		case 'ß':
			b.WriteString("ss")
		case 'ø':
			b.WriteRune('o')
		case 'đ':
			b.WriteRune('d')
		case 'ł':
			b.WriteRune('l')
		case 'æ':
			b.WriteString("ae")
		case 'œ':
			b.WriteString("oe")
		case 'ı':
			b.WriteRune('i')
		default:
			if !unicode.Is(unicode.Mn, c) {
				b.WriteRune(c)
			}
		}
	}
	return norm.NFC.String(b.String())
}

func IsCyrillic(c rune) bool { return c >= 'Ѐ' && c <= 'ԯ' }

func IsLatin(c rune) bool {
	return (c >= 'a' && c <= 'z') || (c >= 'À' && c <= 'ɏ') || (c >= 'Ḁ' && c <= 'ỿ')
}

func isASCIIDigit(c rune) bool  { return c >= '0' && c <= '9' }
func isASCIILetter(c rune) bool { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') }

var foldRules = [][2]string{
	{"shch", "s"}, {"sch", "s"}, {"kh", "h"}, {"zh", "z"}, {"ts", "c"}, {"tz", "c"}, {"ch", "c"}, {"sh", "s"},
	{"ya", "a"}, {"ia", "a"}, {"yu", "u"}, {"iu", "u"}, {"ye", "e"}, {"ie", "e"}, {"yo", "o"}, {"io", "o"},
	{"y", "i"}, {"j", "i"}, {"g", "h"},
}

// FoldKey is a key on which romanisation schemes agree, for comparison only (spec section 5.2).
func FoldKey(latin string) string {
	key := latin
	for _, rule := range foldRules {
		key = strings.ReplaceAll(key, rule[0], rule[1])
	}
	if strings.HasSuffix(key, "ff") {
		key = key[:len(key)-2] + "v"
	}
	var b strings.Builder
	var last rune = -1
	for _, c := range key {
		if c != last {
			b.WriteRune(c)
			last = c
		}
	}
	return b.String()
}

// TransliterationTable is one language's Cyrillic-to-Latin table; Initial letters apply at the start of a word.
type TransliterationTable struct {
	Language string
	Letters  map[rune]string
	Initial  map[rune]string
}

func (t *TransliterationTable) Transliterate(text string) string {
	runes := []rune(text)
	var b strings.Builder
	for i, c := range runes {
		wordStart := i == 0 || !unicode.IsLetter(runes[i-1])
		if initial, ok := t.Initial[c]; wordStart && ok {
			b.WriteString(initial)
		} else if latin, ok := t.Letters[c]; ok {
			b.WriteString(latin)
		} else {
			b.WriteRune(c)
		}
	}
	return b.String()
}

const maxForms = 8

var lookAlikes = map[rune]rune{
	'а': 'a', 'в': 'b', 'е': 'e', 'к': 'k', 'м': 'm', 'н': 'h', 'о': 'o', 'р': 'p',
	'с': 'c', 'т': 't', 'у': 'y', 'х': 'x', 'і': 'i', 'ј': 'j', 'ѕ': 's',
}

// tokenFactory builds tokens: every Latin reading and its fold key. Never strips marks before transliterating.
type tokenFactory struct{ tables []*TransliterationTable }

func (f *tokenFactory) Create(text string) *Token {
	runes := []rune(text)
	hasCyrillic, hasLatin, isNumber := false, false, true
	for _, c := range runes {
		hasCyrillic = hasCyrillic || IsCyrillic(c)
		hasLatin = hasLatin || IsLatin(c)
		isNumber = isNumber && isASCIIDigit(c)
	}
	var forms []string
	if hasCyrillic {
		for _, table := range f.tables {
			forms = append(forms, table.Transliterate(text))
		}
		allLookAlike := len(runes) <= 4
		if allLookAlike {
			for _, c := range runes {
				if _, ok := lookAlikes[c]; !ok && !isASCIILetter(c) {
					allLookAlike = false
					break
				}
			}
		}
		if hasLatin || allLookAlike {
			mapped := make([]rune, len(runes))
			for i, c := range runes {
				if latin, ok := lookAlikes[c]; ok {
					mapped[i] = latin
				} else {
					mapped[i] = c
				}
			}
			forms = append(forms, string(mapped))
		}
		for i := range forms {
			forms[i] = StripDiacritics(forms[i])
		}
	} else if hasLatin || isNumber {
		forms = append(forms, StripDiacritics(text))
		if strings.ContainsAny(text, "üöä") {
			replaced := strings.NewReplacer("ü", "ue", "ö", "oe", "ä", "ae").Replace(text)
			forms = append(forms, StripDiacritics(replaced))
		}
	}

	distinct := []string{}
	for _, form := range forms {
		if form != "" && !contains(distinct, form) {
			distinct = append(distinct, form)
		}
	}
	if len(distinct) > maxForms {
		distinct = distinct[:maxForms]
	}
	folds := distinct
	if !isNumber {
		folds = []string{}
		for _, form := range distinct {
			if fold := FoldKey(form); !contains(folds, fold) {
				folds = append(folds, fold)
			}
		}
	}
	return &Token{Text: text, Forms: distinct, Folds: folds, IsCyrillic: hasCyrillic, IsNumber: isNumber, length: len(runes)}
}

func contains(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}

// tokenizer follows section 5.1: normalise, replace multi-word legal forms, join runs of single letters (keeping the
// letters for initials), then transliterate. With a cache, tokens are interned.
type tokenizer struct {
	data  *MatcherData
	cache map[string]*Token
}

func (t *tokenizer) Tokenize(value string, joinInitials bool) []RawToken {
	words := t.replaceLegalPhrases(Words(removeHyphenatedHonorifics(value)))
	tokens := make([]RawToken, 0, len(words))
	for i := 0; i < len(words); i++ {
		if !joinInitials || !isSingleLetter(words[i]) {
			tokens = append(tokens, RawToken{Token: t.Get(words[i])})
			continue
		}
		run := []string{words[i]}
		for i+1 < len(words) && isSingleLetter(words[i+1]) {
			i++
			run = append(run, words[i])
		}
		initials := make([]*Token, len(run))
		for j, letter := range run {
			initials[j] = t.Get(letter)
		}
		tokens = append(tokens, RawToken{Token: t.Get(strings.Join(run, "")), Initials: initials})
	}
	return tokens
}

func (t *tokenizer) Get(text string) *Token {
	if t.cache == nil {
		return t.data.tokens.Create(text)
	}
	token, ok := t.cache[text]
	if !ok {
		token = t.data.tokens.Create(text)
		t.cache[text] = token
	}
	return token
}

func isSingleLetter(word string) bool {
	runes := []rune(word)
	return len(runes) == 1 && unicode.IsLetter(runes[0])
}

func (t *tokenizer) replaceLegalPhrases(words []string) []string {
	if len(words) < 2 {
		return words
	}
	result := make([]string, 0, len(words))
	for i := 0; i < len(words); i++ {
		var found *legalPhrase
		for _, candidate := range t.data.LegalPhrases[words[i]] {
			if phraseMatches(words, i, candidate.Words) {
				c := candidate
				found = &c
				break
			}
		}
		if found == nil {
			result = append(result, words[i])
		} else {
			result = append(result, found.Short)
			i += len(found.Words) - 1
		}
	}
	return result
}

func phraseMatches(words []string, start int, phrase []string) bool {
	if start+len(phrase) > len(words) {
		return false
	}
	for j, word := range phrase {
		if words[start+j] != word {
			return false
		}
	}
	return true
}

// removeHyphenatedHonorifics removes г-н and г-жа (any case), with an optional full stop, where no letter touches
// them: the reference's (?<!\p{L})г-(н|жа)(?!\p{L})\.? replaced by a space.
func removeHyphenatedHonorifics(value string) string {
	runes := []rune(value)
	var b strings.Builder
	is := func(i int, lower rune) bool { return i < len(runes) && unicode.ToLower(runes[i]) == lower }
	for i := 0; i < len(runes); i++ {
		if (i == 0 || !unicode.IsLetter(runes[i-1])) && is(i, 'г') && i+1 < len(runes) && runes[i+1] == '-' {
			end := -1
			if is(i+2, 'н') {
				end = i + 3
			} else if is(i+2, 'ж') && is(i+3, 'а') {
				end = i + 4
			}
			if end >= 0 && (end >= len(runes) || !unicode.IsLetter(runes[end])) {
				if end < len(runes) && runes[end] == '.' {
					end++
				}
				b.WriteRune(' ')
				i = end - 1
				continue
			}
		}
		b.WriteRune(runes[i])
	}
	return b.String()
}
