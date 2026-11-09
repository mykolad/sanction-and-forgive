package matching

import (
	"crypto/sha256"
	"encoding/hex"
	"math"
	"strconv"
	"strings"
)

// Options holds every number the matcher uses (MatchingOptions in the reference).
type Options struct {
	MatchScore                   float64
	ReviewScore                  float64
	MatchMinPercent              int
	MatchMaxPercent              int
	ReviewMinPercent             int
	ReviewMaxPercent             int
	NoMatchMaxPercent            int
	NotAllAlignedCap             int
	ThinCoreCap                  int
	GenericCap                   int
	WeakAliasCap                 int
	CappedMethodCap              int
	ConflictingAddressCap        int
	ContainedNamePercent         int
	TwoInitialsPercent           int
	OneInitialPercent            int
	UncheckedInitialsPercent     int
	ContradictingInitialsPercent int
	CorroboratedBonus            int
	AddressOnlyPercent           int
	MinTokenWeight               float64
	CandidateLimit               int
	HitsPerCell                  int
	BestCandidateMinPercent      int
}

// DefaultOptions are the reference's defaults.
func DefaultOptions() *Options {
	return &Options{
		MatchScore: 0.95, ReviewScore: 0.85, MatchMinPercent: 90, MatchMaxPercent: 99, ReviewMinPercent: 50,
		ReviewMaxPercent: 89, NoMatchMaxPercent: 49, NotAllAlignedCap: 89, ThinCoreCap: 60, GenericCap: 75,
		WeakAliasCap: 75, CappedMethodCap: 75, ConflictingAddressCap: 80, ContainedNamePercent: 70,
		TwoInitialsPercent: 70, OneInitialPercent: 60, UncheckedInitialsPercent: 50, ContradictingInitialsPercent: 30,
		CorroboratedBonus: 5, AddressOnlyPercent: 50, MinTokenWeight: 1.0, CandidateLimit: 2000, HitsPerCell: 5,
		BestCandidateMinPercent: 20,
	}
}

// version is the reference's MatcherVersion: SHA-256 of the dictionaries' hash and every option by name (ordinal
// order, invariant formatting), the first 12 hex characters. The same dictionaries and options give the same version
// in every implementation.
func (o *Options) version(data *MatcherData) string {
	f := func(v float64) string { return strconv.FormatFloat(v, 'f', -1, 64) }
	i := strconv.Itoa
	values := []string{
		"BestCandidateMinPercent=" + i(o.BestCandidateMinPercent),
		"CandidateLimit=" + i(o.CandidateLimit),
		"CappedMethodCap=" + i(o.CappedMethodCap),
		"ConflictingAddressCap=" + i(o.ConflictingAddressCap),
		"ContainedNamePercent=" + i(o.ContainedNamePercent),
		"ContradictingInitialsPercent=" + i(o.ContradictingInitialsPercent),
		"CorroboratedBonus=" + i(o.CorroboratedBonus),
		"GenericCap=" + i(o.GenericCap),
		"HitsPerCell=" + i(o.HitsPerCell),
		"MatchMaxPercent=" + i(o.MatchMaxPercent),
		"MatchMinPercent=" + i(o.MatchMinPercent),
		"MatchScore=" + f(o.MatchScore),
		"MinTokenWeight=" + f(o.MinTokenWeight),
		"NoMatchMaxPercent=" + i(o.NoMatchMaxPercent),
		"NotAllAlignedCap=" + i(o.NotAllAlignedCap),
		"OneInitialPercent=" + i(o.OneInitialPercent),
		"ReviewMaxPercent=" + i(o.ReviewMaxPercent),
		"ReviewMinPercent=" + i(o.ReviewMinPercent),
		"ReviewScore=" + f(o.ReviewScore),
		"ThinCoreCap=" + i(o.ThinCoreCap),
		"TwoInitialsPercent=" + i(o.TwoInitialsPercent),
		"UncheckedInitialsPercent=" + i(o.UncheckedInitialsPercent),
		"WeakAliasCap=" + i(o.WeakAliasCap),
		"AddressOnlyPercent=" + i(o.AddressOnlyPercent),
	}
	sortOrdinal(values)
	sum := sha256.Sum256([]byte(data.Hash + "|" + strings.Join(values, ";")))
	return hex.EncodeToString(sum[:])[:12]
}

func sortOrdinal(values []string) {
	// Insertion sort: a couple of dozen strings, ordinal (byte) order.
	for i := 1; i < len(values); i++ {
		for j := i; j > 0 && values[j] < values[j-1]; j-- {
			values[j], values[j-1] = values[j-1], values[j]
		}
	}
}

// Outcome is a cell's outcome from its percentage.
type Outcome int

const (
	NoMatch Outcome = iota
	Review
	Match
)

func (o Outcome) String() string {
	switch o {
	case Review:
		return "Review"
	case Match:
		return "Match"
	}
	return "NoMatch"
}

func toPercent(score float64, o *Options) int {
	if score >= o.MatchScore {
		return linear(score, o.MatchScore, 1.0, o.MatchMinPercent, o.MatchMaxPercent)
	}
	if score >= o.ReviewScore {
		return linear(score, o.ReviewScore, o.MatchScore, o.ReviewMinPercent, o.ReviewMaxPercent)
	}
	return linear(score, 0, o.ReviewScore, 0, o.NoMatchMaxPercent)
}

func toOutcome(percent int, o *Options) Outcome {
	switch {
	case percent >= o.MatchMinPercent:
		return Match
	case percent >= o.ReviewMinPercent:
		return Review
	}
	return NoMatch
}

func bandMax(percent int, o *Options) int {
	switch toOutcome(percent, o) {
	case Match:
		return o.MatchMaxPercent
	case Review:
		return o.ReviewMaxPercent
	}
	return o.NoMatchMaxPercent
}

func linear(score, low, high float64, lowPercent, highPercent int) int {
	fraction := math.Max(0, math.Min(1, (score-low)/(high-low)))
	value := int(math.Floor(float64(lowPercent) + fraction*float64(highPercent-lowPercent)))
	return max(lowPercent, min(highPercent, value))
}
