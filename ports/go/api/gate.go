package api

import (
	"context"
	"net/http"
	"sync/atomic"
)

// screeningGate holds single screenings at once (Screening:MaxConcurrent) and waiting for one of those places
// (Screening:MaxWaiting). One more is refused at once (503 busy, Retry-After 1) rather than queued: past its capacity a
// replica answers quickly instead of making every caller wait for seconds.
type screeningGate struct {
	running  chan struct{}
	admitted atomic.Int64
	limit    int64
}

func newScreeningGate(maxConcurrent, maxWaiting int) *screeningGate {
	concurrent := max(1, maxConcurrent)
	return &screeningGate{running: make(chan struct{}, concurrent), limit: int64(concurrent + max(0, maxWaiting))}
}

// run runs work once a place is free and says whether it did: false at once when every place is taken and as many
// are waiting as allowed, or when ctx ends while it waits.
func (g *screeningGate) run(ctx context.Context, work func()) bool {
	if g.admitted.Add(1) > g.limit {
		g.admitted.Add(-1)
		return false
	}
	defer g.admitted.Add(-1)
	select {
	case g.running <- struct{}{}:
	case <-ctx.Done():
		return false
	}
	defer func() { <-g.running }()
	work()
	return true
}

// busy answers 503 when the gate is full: try again in a second.
func busy(w http.ResponseWriter) {
	w.Header().Set("Retry-After", "1")
	writeJSON(w, http.StatusServiceUnavailable, warmingUp{"busy"})
}
