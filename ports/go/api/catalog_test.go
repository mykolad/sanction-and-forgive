package api

import (
	"testing"
	"time"
)

// When the lists are refreshed (refreshSchedule): scripted times and values of store.ChangesSQL, with no clock and no
// database. Checks every 2 s, the full refresh every 15 minutes, as in production.

var scheduleStart = time.Date(2026, 11, 12, 0, 0, 0, 0, time.UTC)

func newSchedule() *refreshSchedule {
	return &refreshSchedule{refreshEvery: 15 * time.Minute, checkEvery: 2 * time.Second}
}

func at(seconds float64) time.Time {
	return scheduleStart.Add(time.Duration(seconds * float64(time.Second)))
}

// run asks for a refresh and finishes it at once, as it went.
func run(s *refreshSchedule, seconds float64, changes string, read, succeeds bool) bool {
	if !s.begin(at(seconds), changes, read) {
		return false
	}
	s.finished(at(seconds), succeeds)
	return true
}

func TestTheFirstCheck_Refreshes_AndAnUnchangedValue_DoesntAgain(t *testing.T) {
	s := newSchedule()
	if !run(s, 0, "a", true, true) || s.begin(at(2), "a", true) || s.begin(at(4), "a", true) {
		t.Fatal("refreshes first, then not for the same value")
	}
}

func TestANewValue_RefreshesAtOnce(t *testing.T) {
	s := newSchedule()
	run(s, 0, "a", true, true)
	if !s.begin(at(2), "b", true) {
		t.Fatal("a new value didn't refresh")
	}
}

func TestAChangeMadeDuringARefresh_IsRefreshedForAtTheNextCheck(t *testing.T) {
	s := newSchedule()
	run(s, 0, "a", true, true)
	if !s.begin(at(2), "b", true) {
		t.Fatal("b didn't refresh")
	}
	s.finished(at(3), true)
	if !s.begin(at(5), "c", true) {
		t.Fatal("the change made during the refresh wasn't refreshed for")
	}
}

func TestAFailedRefresh_IsTriedAgainAfterItsWait_NotAtEveryCheck_UntilItWorks(t *testing.T) {
	s := newSchedule()
	run(s, 0, "a", true, true)
	if !run(s, 2, "b", true, false) || s.retryWait != 5*time.Second || s.begin(at(4), "b", true) || s.begin(at(6), "b", true) {
		t.Fatal("first failure: retried only after 5 s")
	}
	if !run(s, 7, "b", true, false) || s.retryWait != 10*time.Second || s.begin(at(15), "b", true) {
		t.Fatal("second failure: retried only after 10 s")
	}
	if !run(s, 17, "b", true, true) || s.retryWait != 0 || s.begin(at(19), "b", true) || s.begin(at(40), "b", true) {
		t.Fatal("once it works: no more retries")
	}
}

func TestANewValue_DoesntWaitForAnEarlierFailuresRetry(t *testing.T) {
	s := newSchedule()
	run(s, 0, "a", true, true)
	run(s, 2, "b", true, false)
	if !s.begin(at(4), "c", true) {
		t.Fatal("a new value waited for the retry")
	}
}

func TestAFailedCheck_RefreshesOnlyForARetryOrTheBackstop(t *testing.T) {
	s := newSchedule()
	run(s, 0, "a", true, true)
	if s.begin(at(2), "", false) {
		t.Fatal("a failed check refreshed")
	}
	run(s, 2, "b", true, false)
	if s.begin(at(4), "", false) || !s.begin(at(7), "", false) {
		t.Fatal("a failed check: only the retry, at its time")
	}
}

func TestWithNothingChanging_TheBackstopRefreshesEvery15Minutes(t *testing.T) {
	s := newSchedule()
	run(s, 0, "a", true, true)
	if s.begin(at(15*60-1), "a", true) || !s.begin(at(15*60), "a", true) {
		t.Fatal("the backstop isn't every 15 minutes")
	}
}

func TestItSleepsTheCheckInterval_OrLessWhenARetryFallsSooner(t *testing.T) {
	withCheck := newSchedule()
	run(withCheck, 0, "a", true, true)
	first := withCheck.nextWait()
	run(withCheck, 2, "b", true, false)
	if first != 2*time.Second || withCheck.nextWait() != 2*time.Second {
		t.Fatal("with the check: its interval")
	}
	withoutCheck := &refreshSchedule{refreshEvery: 15 * time.Minute, checkEvery: 15 * time.Minute}
	run(withoutCheck, 0, "", false, false)
	if withoutCheck.nextWait() != 5*time.Second || !withoutCheck.begin(at(5), "", false) {
		t.Fatal("without the check: the retry")
	}
}

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
