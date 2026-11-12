package api

import (
	"context"
	"net/http"
	"sync/atomic"
	"time"
)

// screeningGate holds single screenings at once (Screening:MaxConcurrent, 4), waiting for one of those places at most
// Screening:MaxWaitSeconds (2), and at most Screening:MaxWaiting (256) waiting at all, a bound on memory. One that would
// wait longer, or one more, is refused (503 busy, Retry-After 1): past its capacity a replica answers quickly instead
// of making every caller wait longer and longer. The time, not the count, is the limit in practice.
type screeningGate struct {
	running  chan struct{}
	admitted atomic.Int64
	limit    int64
	maxWait  time.Duration
}

func newScreeningGate(maxConcurrent, maxWaiting int, maxWait time.Duration) *screeningGate {
	concurrent := max(1, maxConcurrent)
	return &screeningGate{running: make(chan struct{}, concurrent), limit: int64(concurrent + max(0, maxWaiting)), maxWait: max(0, maxWait)}
}

// gateResult says what became of a screening at the gate.
type gateResult int

const (
	ran  gateResult = iota // it ran
	full                   // refused: every place taken and as many waiting as allowed, or no place within maxWait
	gone                   // the caller gave up while it waited
)

// run runs work once a place is free.
func (g *screeningGate) run(ctx context.Context, work func()) gateResult {
	if g.admitted.Add(1) > g.limit {
		g.admitted.Add(-1)
		return full
	}
	defer g.admitted.Add(-1)
	timer := time.NewTimer(g.maxWait)
	defer timer.Stop()
	select {
	case g.running <- struct{}{}:
	case <-timer.C:
		// A free place and the time running out together: take the place.
		select {
		case g.running <- struct{}{}:
		default:
			return full
		}
	case <-ctx.Done():
		return gone
	}
	defer func() { <-g.running }()
	work()
	return ran
}

// busy answers 503 when the gate is full: try again in a second.
func busy(w http.ResponseWriter) {
	w.Header().Set("Retry-After", "1")
	writeJSON(w, http.StatusServiceUnavailable, warmingUp{"busy"})
}
