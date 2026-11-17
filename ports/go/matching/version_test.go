package matching

import "testing"

// The reference's MatcherVersion for the shipped dictionaries and default options (French country names, 2026-11-16).
const referenceMatcherVersion = "e6a5156d037b"

func TestMatcherVersion_IsTheReferences(t *testing.T) {
	if got := NewScreener(DefaultOptions()).MatcherVersion; got != referenceMatcherVersion {
		t.Fatalf("matcher version %s, reference %s", got, referenceMatcherVersion)
	}
}
