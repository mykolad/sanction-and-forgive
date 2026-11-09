// The API: the Go port of SanctionAndForgive.Api, configured by the reference's names (docs/conformance.md).
package main

import (
	"context"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"

	"sanctionandforgive/api"
	"sanctionandforgive/matching"
	"sanctionandforgive/store"
)

func setting(name, fallback string) string {
	if value := os.Getenv(name); value != "" {
		return value
	}
	return fallback
}

func intSetting(name string, fallback int) int {
	if value, err := strconv.Atoi(os.Getenv(name)); err == nil {
		return value
	}
	return fallback
}

func main() {
	// One JSON object per line, as the reference logs outside Development.
	logger := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{ReplaceAttr: func(_ []string, attr slog.Attr) slog.Attr {
		switch attr.Key {
		case slog.TimeKey:
			attr.Key = "Timestamp"
		case slog.LevelKey:
			attr.Key = "LogLevel"
		case slog.MessageKey:
			attr.Key = "Message"
		}
		return attr
	}}))

	connection := os.Getenv("ConnectionStrings__Lists")
	if connection == "" {
		logger.Error("No connection string: set ConnectionStrings__Lists.")
		os.Exit(2)
	}
	db, err := store.Open(connection)
	if err != nil {
		logger.Error("Opening the database failed", "error", err.Error())
		os.Exit(2)
	}

	refresh := 15.0
	if value, err := strconv.ParseFloat(os.Getenv("Lists__RefreshMinutes"), 64); err == nil && value > 0 {
		refresh = value
	}
	config := api.Config{
		Version:           setting("App__Version", "dev"),
		Revision:          os.Getenv("CONTAINER_APP_REVISION"),
		RateLimitPerMin:   intSetting("RateLimiting__PerMinute", 120),
		StreamMaxRecords:  intSetting("Screening__StreamMaxRecords", 50000),
		StreamMaxSeconds:  intSetting("Screening__StreamMaxSeconds", 200),
		ConcurrentStreams: intSetting("Screening__ConcurrentStreams", 1),
		SignInEnabled:     strings.EqualFold(os.Getenv("Admin__SignInEnabled"), "true"),
	}

	catalog := api.NewCatalog()
	loader := api.NewLoader(db, catalog, logger)
	server := &api.Server{Config: config, Catalog: catalog, Logger: logger,
		Screenings: &api.ScreeningService{Catalog: catalog, Screener: matching.NewScreener(matching.DefaultOptions()), Logger: logger, Now: time.Now}}
	server.Admin = &api.AdminService{Server: server, DB: db, Loader: loader}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	go loader.RunRefresh(ctx, time.Duration(refresh*float64(time.Minute)))

	port := setting("PORT", "8080")
	httpServer := &http.Server{Addr: ":" + port, Handler: server.Handler(), ReadHeaderTimeout: 30 * time.Second}
	go func() {
		<-ctx.Done()
		shutdown, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		httpServer.Shutdown(shutdown) //nolint:errcheck // exiting
	}()
	logger.Info("Now listening on: http://[::]:" + port)
	if err := httpServer.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		logger.Error("The server stopped", "error", err.Error())
		os.Exit(1)
	}
}
