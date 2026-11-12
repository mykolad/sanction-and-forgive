package api

import (
	"context"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"runtime"
	"strings"
	"testing"
)

// hold takes one of g's places until release is closed.
func hold(g *screeningGate, release <-chan struct{}) <-chan bool {
	entered, done := make(chan struct{}), make(chan bool)
	go func() { done <- g.run(context.Background(), func() { close(entered); <-release }) == ran }()
	<-entered
	return done
}

func TestGate_WaitsWhileThereIsRoom_RefusesBeyondIt(t *testing.T) {
	g := newScreeningGate(1, 1)
	release := make(chan struct{})
	held := hold(g, release)

	waited := make(chan bool)
	go func() { waited <- g.run(context.Background(), func() {}) == ran }()
	for g.admitted.Load() < 2 {
		runtime.Gosched() // until the second one waits for the place
	}
	if g.run(context.Background(), func() { t.Error("ran past the limit") }) != full {
		t.Fatal("a third screening was admitted with one place and one waiting")
	}

	close(release)
	if !<-held || !<-waited {
		t.Fatal("the held and the waiting screenings should both have run")
	}
	if g.run(context.Background(), func() {}) != ran {
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

func TestGate_ACallerWhoLeavesWhileWaiting_IsGone_NotBusy(t *testing.T) {
	g := newScreeningGate(1, 1)
	release := make(chan struct{})
	held := hold(g, release)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	if got := g.run(ctx, func() { t.Error("ran for a caller who left") }); got != gone {
		t.Fatalf("got %d, want gone", got)
	}
	close(release)
	<-held
	if g.admitted.Load() != 0 {
		t.Fatalf("%d places still taken", g.admitted.Load())
	}
}

// The gate's "caller left" comes from the connection, which can be wrong: the answer is still busy, never an empty 200.
func TestScreen_ACallerWhoSeemsGone_GetsBusy_NeverAnEmptySuccess(t *testing.T) {
	catalog := NewCatalog()
	catalog.Replace(nil)
	s := &Server{Config: Config{MaxConcurrent: 1, MaxWaiting: 1}, Catalog: catalog, Logger: slog.New(slog.DiscardHandler)}
	handler := s.Handler()
	release := make(chan struct{})
	held := hold(s.gate, release)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	request := httptest.NewRequestWithContext(ctx, http.MethodPost, "/api/v1/screenings", strings.NewReader(`{"reference":"r-1","receiverName":"Someone"}`))
	w := httptest.NewRecorder()

	handler.ServeHTTP(w, request)

	close(release)
	<-held
	if w.Code != http.StatusServiceUnavailable || w.Header().Get("Retry-After") != "1" || w.Body.String() != "{\"state\":\"busy\"}\n" {
		t.Fatalf("got %d %q %q", w.Code, w.Header().Get("Retry-After"), w.Body.String())
	}
}
