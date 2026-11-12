package api

import (
	"testing"
	"time"
)

// A refresh that failed is tried again after a wait that grows: never at every check, never more than a minute apart.
func TestAFailedRefresh_IsTriedAgainAfter5s_ThenTwiceAsLong_AtMostAMinute(t *testing.T) {
	var got []time.Duration
	var wait time.Duration
	for range 7 {
		wait = nextRetryWait(wait)
		got = append(got, wait)
	}
	want := []time.Duration{5 * time.Second, 10 * time.Second, 20 * time.Second, 40 * time.Second, time.Minute, time.Minute, time.Minute}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("got %v, want %v", got, want)
		}
	}
}
