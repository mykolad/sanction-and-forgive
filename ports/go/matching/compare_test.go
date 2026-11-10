package matching

import (
	"math/rand/v2"
	"strings"
	"testing"
)

// fullDistance is the optimal string alignment distance over the whole matrix: what damerauLevenshtein must give,
// capped at the limit.
func fullDistance(a, b []rune) int {
	d := make([][]int, len(a)+1)
	for i := range d {
		d[i] = make([]int, len(b)+1)
		d[i][0] = i
	}
	for j := range d[0] {
		d[0][j] = j
	}
	for i := 1; i <= len(a); i++ {
		for j := 1; j <= len(b); j++ {
			cost := 1
			if a[i-1] == b[j-1] {
				cost = 0
			}
			d[i][j] = min(d[i-1][j]+1, d[i][j-1]+1, d[i-1][j-1]+cost)
			if i > 1 && j > 1 && a[i-1] == b[j-2] && a[i-2] == b[j-1] {
				d[i][j] = min(d[i][j], d[i-2][j-2]+1)
			}
		}
	}
	return d[len(a)][len(b)]
}

func TestDamerauLevenshtein_IsTheFullDistanceCapped(t *testing.T) {
	random := rand.New(rand.NewPCG(1, 2))
	word := func() string {
		// Four letters, one of them Cyrillic: near words, transpositions and both the byte and the rune paths.
		letters := []rune("abcж")
		out := make([]rune, random.IntN(13))
		for i := range out {
			out[i] = letters[random.IntN(len(letters))]
		}
		return string(out)
	}
	for range 200_000 {
		a, b := word(), word()
		full := fullDistance([]rune(a), []rune(b))
		for _, limit := range []int{1, 2, 3, 4} {
			want := min(full, limit)
			if got := damerauLevenshtein([]rune(a), []rune(b), limit); got != want {
				t.Fatalf("%q, %q, limit %d: %d, the full distance gives %d", a, b, limit, got, want)
			}
			if isASCII(a) && isASCII(b) {
				if got := damerauLevenshtein([]byte(a), []byte(b), limit); got != want {
					t.Fatalf("%q, %q as bytes, limit %d: %d, the full distance gives %d", a, b, limit, got, want)
				}
			}
		}
	}
}

// The reference reads UTF-16 units: a character beyond the Basic Multilingual Plane is two surrogates, neither a letter,
// so it separates words (Copilot's point on #37), and a mark there isn't stripped.
func TestWords_ACharacterBeyondTheBasicPlane_SeparatesWords(t *testing.T) {
	for input, want := range map[string]string{"Acme\U00010400": "acme", "ab\U00010400cd": "ab cd", "o'\U00010400neil": "o neil"} {
		if got := strings.Join(Words(input), " "); got != want {
			t.Errorf("Words(%q) = %q, want %q", input, got, want)
		}
	}
	if got := StripDiacritics("e\U0001D167"); got != "e\U0001D167" {
		t.Errorf("StripDiacritics kept %q", got)
	}
}
