package api

import (
	"context"
	"net/http/httptest"
	"runtime"
	"testing"
)

// hold takes one of g's places until release is closed.
func hold(g *screeningGate, release <-chan struct{}) <-chan bool {
	entered, done := make(chan struct{}), make(chan bool)
	go func() { done <- g.run(context.Background(), func() { close(entered); <-release }) }()
	<-entered
	return done
}

func TestGate_WaitsWhileThereIsRoom_RefusesBeyondIt(t *testing.T) {
	g := newScreeningGate(1, 1)
	release := make(chan struct{})
	held := hold(g, release)

	waited := make(chan bool)
	go func() { waited <- g.run(context.Background(), func() {}) }()
	for g.admitted.Load() < 2 {
		runtime.Gosched() // until the second one waits for the place
	}
	if g.run(context.Background(), func() { t.Error("ran past the limit") }) {
		t.Fatal("a third screening was admitted with one place and one waiting")
	}

	close(release)
	if !<-held || !<-waited {
		t.Fatal("the held and the waiting screenings should both have run")
	}
	if !g.run(context.Background(), func() {}) {
		t.Fatal("a free gate refused a screening")
	}
}

func TestBusy_Is503WithRetryAfter(t *testing.T) {
	w := httptest.NewRecorder()
	busy(w)
	if w.Code != 503 || w.Header().Get("Retry-After") != "1" || w.Body.String() != "{\"state\":\"busy\"}\n" {
		t.Fatalf("got %d %q %q", w.Code, w.Header().Get("Retry-After"), w.Body.String())
	}
}
