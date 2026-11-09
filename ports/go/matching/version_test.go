package matching

import "testing"

// The reference's MatcherVersion for the shipped dictionaries and default options (production's /api/v1/status, 2026-11-09).
const referenceMatcherVersion = "566ac3d19d5b"

func TestMatcherVersion_IsTheReferences(t *testing.T) {
	if got := NewScreener(DefaultOptions()).MatcherVersion; got != referenceMatcherVersion {
		t.Fatalf("matcher version %s, reference %s", got, referenceMatcherVersion)
	}
}
