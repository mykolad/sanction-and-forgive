// Package api is the Go port of SanctionAndForgive.Api: the same endpoints, responses and pages (docs/api.md).
package api

import (
	"bufio"
	"bytes"
	"embed"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"io/fs"
	"log/slog"
	"net"
	"net/http"
	"sanctionandforgive/lists"
	"strconv"
	"strings"
	"sync"
	"time"

	"sanctionandforgive/matching"
)

//go:embed wwwroot
var wwwroot embed.FS

//go:embed openapi.json
var openAPI []byte

// Config is the reference's configuration, by the same names.
type Config struct {
	Version           string
	Revision          string
	RateLimitPerMin   int
	StreamMaxRecords  int
	StreamMaxSeconds  int
	ConcurrentStreams int
	SignInEnabled     bool
}

// Server serves the API and the pages.
type Server struct {
	Config     Config
	Catalog    *Catalog
	Screenings *ScreeningService
	Admin      *AdminService
	Logger     *slog.Logger
	streams    chan struct{}
	limiter    *fixedWindow
}

func (s *Server) Handler() http.Handler {
	s.streams = make(chan struct{}, max(1, s.Config.ConcurrentStreams))
	s.limiter = &fixedWindow{limit: s.Config.RateLimitPerMin, counts: map[string]int{}}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", s.health)
	mux.HandleFunc("GET /readyz", s.ready)
	mux.HandleFunc("GET /openapi/v1.json", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json; charset=utf-8")
		w.Write(openAPI) //nolint:errcheck // the client went away
	})
	mux.HandleFunc("GET /api/v1/status", s.status)
	mux.HandleFunc("GET /api/v1/sources", s.sources)
	mux.HandleFunc("GET /api/v1/sources/{code}/history", s.history)
	mux.HandleFunc("GET /api/v1/examples", s.examples)
	mux.HandleFunc("POST /api/v1/lists/{code}/browse", s.browse)
	mux.HandleFunc("POST /api/v1/screenings", s.screen)
	mux.HandleFunc("POST /api/v1/screenings/stream", s.stream)
	s.Admin.routes(mux)
	pages, _ := fs.Sub(wwwroot, "wwwroot")
	mux.Handle("/", http.FileServerFS(pages))
	return mux
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(value) //nolint:errcheck // the client went away
}

type warmingUp struct {
	State string `json:"state"`
}

type healthResponse struct {
	Status   string  `json:"status"`
	Version  string  `json:"version"`
	Revision *string `json:"revision"`
}

func (s *Server) health(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, healthResponse{"ok", s.Config.Version, lists.Optional(s.Config.Revision)})
}

func (s *Server) ready(w http.ResponseWriter, _ *http.Request) {
	if !s.Catalog.IsReady() {
		writeJSON(w, http.StatusServiceUnavailable, warmingUp{"warmingUp"})
		return
	}
	writeJSON(w, http.StatusOK, healthResponse{"ready", s.Config.Version, lists.Optional(s.Config.Revision)})
}

type statusResponse struct {
	State          string         `json:"state"`
	Version        string         `json:"version"`
	MatcherVersion string         `json:"matcherVersion"`
	Lists          []ListResponse `json:"lists"`
}

func (s *Server) status(w http.ResponseWriter, _ *http.Request) {
	now := s.Screenings.Now()
	state := "warmingUp"
	if s.Catalog.IsReady() {
		state = "ready"
	}
	lists := []ListResponse{}
	for _, list := range s.Catalog.Lists() {
		lists = append(lists, listResponse(list, now))
	}
	writeJSON(w, http.StatusOK, statusResponse{state, s.Config.Version, s.Screenings.Screener.MatcherVersion, lists})
}

// clientIP is the X-Forwarded-For entry the ingress appended (forward limit 1: the last one), else the peer.
func clientIP(r *http.Request) string {
	if forwarded := r.Header.Values("X-Forwarded-For"); len(forwarded) > 0 {
		parts := strings.Split(forwarded[len(forwarded)-1], ",")
		if ip := strings.TrimSpace(parts[len(parts)-1]); ip != "" {
			return ip
		}
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

// fixedWindow is a per-client fixed one-minute window (the reference's FixedWindowRateLimiter, no queue).
type fixedWindow struct {
	limit  int
	mu     sync.Mutex
	window time.Time
	counts map[string]int
}

// allow returns 0 if allowed, else the seconds until the window resets.
func (f *fixedWindow) allow(key string, now time.Time) int {
	if f.limit <= 0 {
		return 0
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	if now.Sub(f.window) >= time.Minute {
		f.window = now
		f.counts = map[string]int{}
	}
	if f.counts[key] >= f.limit {
		return max(1, int(time.Minute-now.Sub(f.window)+time.Second-1)/int(time.Second))
	}
	f.counts[key]++
	return 0
}

type validationProblem struct {
	Type   string              `json:"type"`
	Title  string              `json:"title"`
	Status int                 `json:"status"`
	Errors map[string][]string `json:"errors"`
}

func badRequest(w http.ResponseWriter, errors map[string][]string) {
	writeJSON(w, http.StatusBadRequest, validationProblem{"https://tools.ietf.org/html/rfc9110#section-15.5.1", "One or more validation errors occurred.", 400, errors})
}

func (s *Server) screen(w http.ResponseWriter, r *http.Request) {
	if wait := s.limiter.allow(clientIP(r), time.Now()); wait > 0 {
		w.Header().Set("Retry-After", itoa(wait))
		w.WriteHeader(http.StatusTooManyRequests)
		return
	}
	var body ScreeningRequestBody
	decoder := json.NewDecoder(io.LimitReader(r.Body, 1<<20))
	if err := decoder.Decode(&body); err != nil || decoder.More() {
		// The parser's message can quote the input: say only that the body isn't valid.
		badRequest(w, map[string][]string{"$": {"The request body isn't a valid screening request."}})
		return
	}
	if errs := Validate(&body); len(errs) > 0 {
		badRequest(w, errs)
		return
	}
	if !s.Catalog.IsReady() {
		writeJSON(w, http.StatusServiceUnavailable, warmingUp{"warmingUp"})
		return
	}
	writeJSON(w, http.StatusOK, s.Screenings.Screen(&body))
}

const maxLineBytes = 4096

type readLine struct {
	value   string
	tooLong bool
	err     error
}

// readLines reads the body as lines (\n, a \r before it dropped), at most maxLineBytes UTF-8 bytes each: a longer one
// comes back marked too long, its rest skipped. A UTF-8 byte order mark at the start is skipped.
func readLines(body io.Reader, out chan<- readLine, stop <-chan struct{}) {
	defer close(out)
	reader := bufio.NewReaderSize(body, 16*1024)
	if bom, err := reader.Peek(3); err == nil && bytes.Equal(bom, []byte{0xEF, 0xBB, 0xBF}) {
		reader.Discard(3) //nolint:errcheck // peeked
	}
	for {
		var line []byte
		tooLong, any := false, false
		for {
			chunk, err := reader.ReadSlice('\n')
			any = any || len(chunk) > 0
			if !tooLong {
				if len(line)+len(chunk) > maxLineBytes+2 {
					tooLong = true
					line = nil
				} else {
					line = append(line, chunk...)
				}
			}
			if errors.Is(err, bufio.ErrBufferFull) {
				continue
			}
			if err != nil && !errors.Is(err, io.EOF) {
				select {
				case out <- readLine{err: err}:
				case <-stop:
				}
				return
			}
			if err != nil && !any {
				return
			}
			break
		}
		text := strings.TrimSuffix(string(line), "\n")
		text = strings.TrimSuffix(text, "\r")
		if !tooLong && len(text) > maxLineBytes {
			tooLong, text = true, ""
		}
		select {
		case out <- readLine{value: text, tooLong: tooLong}:
		case <-stop:
			return
		}
		if len(line) == 0 || line[len(line)-1] != '\n' {
			if _, err := reader.Peek(1); err != nil {
				return
			}
		}
	}
}

func (s *Server) stream(w http.ResponseWriter, r *http.Request) {
	select {
	case s.streams <- struct{}{}:
		defer func() { <-s.streams }()
	default:
		w.Header().Set("Retry-After", "1")
		w.WriteHeader(http.StatusTooManyRequests)
		return
	}
	if !s.Catalog.IsReady() {
		writeJSON(w, http.StatusServiceUnavailable, warmingUp{"warmingUp"})
		return
	}
	maxRecords, maxSeconds := s.Config.StreamMaxRecords, s.Config.StreamMaxSeconds
	deadline := time.Now().Add(time.Duration(maxSeconds) * time.Second)
	controller := http.NewResponseController(w)
	controller.SetWriteDeadline(deadline.Add(20 * time.Second)) //nolint:errcheck // best effort
	controller.EnableFullDuplex()                               //nolint:errcheck // HTTP/1 servers support it
	w.Header().Set("Content-Type", "application/x-ndjson; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	encoder := json.NewEncoder(w)
	write := func(line any) {
		encoder.Encode(line) //nolint:errcheck // the client went away: the loop ends on its own
		controller.Flush()   //nolint:errcheck // as above
	}

	lists, allowList, now := s.Catalog.Lists(), s.Catalog.AllowList(), s.Screenings.Now()
	rows := []ListResponse{}
	for _, list := range lists {
		rows = append(rows, listResponse(list, now))
	}
	write(StreamHeaderLine{"header", s.Screenings.Screener.MatcherVersion, DisclaimerText, rows, maxRecords, maxSeconds, allowList.Version})

	lines := make(chan readLine)
	stop := make(chan struct{})
	defer close(stop)
	go readLines(r.Body, lines, stop)
	timer := time.NewTimer(time.Until(deadline))
	defer timer.Stop()

	line, screened, errorCount := 0, 0, 0
	var nextLine *int
	for {
		if screened+errorCount >= maxRecords {
			next := line + 1
			nextLine = &next
			break
		}
		var read readLine
		var ok bool
		select {
		case read, ok = <-lines:
		case <-timer.C:
			next := line + 1
			nextLine = &next
		}
		if nextLine != nil || !ok || read.err != nil {
			break
		}
		line++
		if !time.Now().Before(deadline) {
			next := line
			nextLine = &next
			break
		}
		if read.value == "" && !read.tooLong {
			continue
		}
		body, problem := parseLine(read)
		if problem != nil {
			errorCount++
			var reference *string
			if body != nil && body.Reference != nil && utf16Len(*body.Reference) <= 64 {
				reference = body.Reference
			}
			write(StreamErrorLine{"error", line, reference, problem})
			continue
		}
		result := s.Screenings.ScreenForStream(body, lists, allowList)
		screened++
		entries := []StreamEntry{}
		for i := range result.Entries {
			entries = append(entries, streamEntry(&result.Entries[i], s.Screenings.Options()))
		}
		write(StreamResultLine{"result", line, *body.Reference, result.Outcome.String(), entries})
	}
	write(StreamEndLine{"end", screened, errorCount, nextLine != nil, nextLine})
}

func parseLine(read readLine) (*ScreeningRequestBody, map[string][]string) {
	if read.tooLong {
		return nil, map[string][]string{"": {"The line is longer than 4096 bytes."}}
	}
	trimmed := strings.TrimSpace(read.value)
	if trimmed == "null" {
		return nil, map[string][]string{"": {"The line isn't a JSON object."}}
	}
	var body ScreeningRequestBody
	decoder := json.NewDecoder(strings.NewReader(read.value))
	if err := decoder.Decode(&body); err != nil || !strings.HasPrefix(trimmed, "{") {
		return nil, map[string][]string{"": {"The line isn't a valid JSON object."}}
	}
	if _, err := decoder.Token(); !errors.Is(err, io.EOF) {
		return nil, map[string][]string{"": {"The line isn't a valid JSON object."}}
	}
	if errs := Validate(&body); len(errs) > 0 {
		return &body, errs
	}
	return &body, nil
}

func itoa(v int) string { return strconv.Itoa(v) }

// --- Admin access (AdminAccess.cs) ---

const (
	adminRole          = "Lists.Admin"
	principalHeader    = "X-MS-CLIENT-PRINCIPAL"
	requestHeader      = "X-Requested-By"
	requestHeaderValue = "sanction-and-forgive-admin"
)

// AdminUser is the signed-in user.
type AdminUser struct {
	Name    string
	IsAdmin bool
}

// currentUser reads X-MS-CLIENT-PRINCIPAL (base64 JSON claims; the platform doesn't let clients set it), only when
// sign-in is on.
func (s *Server) currentUser(r *http.Request) *AdminUser {
	return readPrincipal(s.Config.SignInEnabled, r)
}

func readPrincipal(enabled bool, r *http.Request) *AdminUser {
	values := r.Header.Values(principalHeader)
	if !enabled || len(values) != 1 || len(values[0]) == 0 || len(values[0]) >= 16384 {
		return nil
	}
	decoded, err := base64.StdEncoding.DecodeString(values[0])
	if err != nil {
		return nil
	}
	var principal struct {
		RoleType *string `json:"role_typ"`
		NameType *string `json:"name_typ"`
		Claims   []struct {
			Type  *string `json:"typ"`
			Value *string `json:"val"`
		} `json:"claims"`
	}
	if err := json.Unmarshal(decoded, &principal); err != nil || principal.Claims == nil {
		return nil
	}
	roleTypes := []string{"roles", "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"}
	nameTypes := []string{"preferred_username", "name", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"}
	if principal.NameType != nil {
		nameTypes = append([]string{*principal.NameType}, nameTypes...)
	}
	user := &AdminUser{Name: "signed-in user"}
	for _, claim := range principal.Claims {
		t, v := deref(claim.Type), deref(claim.Value)
		if (principal.RoleType != nil && t == *principal.RoleType) || contains(roleTypes, t) {
			if v == adminRole {
				user.IsAdmin = true
			}
		}
	}
	for _, nameType := range nameTypes {
		for _, claim := range principal.Claims {
			if deref(claim.Type) == nameType {
				if v := deref(claim.Value); v != "" {
					user.Name = v
					return user
				}
				break
			}
		}
	}
	return user
}

func fromThisSite(r *http.Request) bool { return r.Header.Get(requestHeader) == requestHeaderValue }

var _ = matching.Match
