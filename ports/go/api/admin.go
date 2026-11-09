package api

import (
	"database/sql"
	"net/http"
	"time"

	"sanctionandforgive/store"
)

const (
	signInPath  = "/.auth/login/aad?post_login_redirect_uri=/admin"
	signOutPath = "/.auth/logout?post_logout_redirect_uri=/admin"
)

// AdminService is /api/v1/admin (AdminController.cs): who is signed in, reviewing held updates, switching lists and
// the allow-list. Everything but me needs the Lists.Admin role, and every change the site's own request header.
type AdminService struct {
	Server *Server
	DB     *sql.DB
	Loader *Loader
}

func (a *AdminService) routes(mux *http.ServeMux) {
	mux.HandleFunc("GET /api/v1/admin/me", a.me)
	mux.HandleFunc("GET /api/v1/admin/allow-list", a.allowList)
	notYet := func(w http.ResponseWriter, r *http.Request) {
		if a.refuse(w, r, true) {
			return
		}
		w.WriteHeader(http.StatusNotImplemented)
	}
	mux.HandleFunc("POST /api/v1/admin/imports/{id}/confirm", notYet)
	mux.HandleFunc("POST /api/v1/admin/imports/{id}/reject", notYet)
	mux.HandleFunc("POST /api/v1/admin/allow-list", notYet)
	mux.HandleFunc("POST /api/v1/admin/allow-list/{id}/remove", notYet)
	mux.HandleFunc("POST /api/v1/admin/lists/{code}/switch-off", notYet)
	mux.HandleFunc("POST /api/v1/admin/lists/{code}/switch-on", notYet)
}

type adminMeResponse struct {
	Enabled    bool    `json:"enabled"`
	SignedIn   bool    `json:"signedIn"`
	Name       *string `json:"name"`
	IsAdmin    bool    `json:"isAdmin"`
	SignInURL  *string `json:"signInUrl"`
	SignOutURL *string `json:"signOutUrl"`
}

func (a *AdminService) me(w http.ResponseWriter, r *http.Request) {
	enabled := a.Server.Config.SignInEnabled
	user := a.Server.currentUser(r)
	response := adminMeResponse{Enabled: enabled, SignedIn: user != nil}
	if user != nil {
		response.Name, response.IsAdmin, response.SignOutURL = &user.Name, user.IsAdmin, optional(signOutPath)
	}
	if enabled {
		response.SignInURL = optional(signInPath)
	}
	writeJSON(w, http.StatusOK, response)
}

// refuse writes why a request may not go on, and says whether it did: sign-in off (404), not signed in (401), without
// the role (403), or a change without the site's own header (400).
func (a *AdminService) refuse(w http.ResponseWriter, r *http.Request, change bool) bool {
	user := a.Server.currentUser(r)
	switch {
	case !a.Server.Config.SignInEnabled:
		http.NotFound(w, r)
	case user == nil:
		w.WriteHeader(http.StatusUnauthorized)
	case !user.IsAdmin:
		w.WriteHeader(http.StatusForbidden)
	case change && !fromThisSite(r):
		badRequest(w, map[string][]string{requestHeader: {"Send this request from the admin page."}})
	default:
		return false
	}
	return true
}

type allowListEntryResponse struct {
	ID        int        `json:"id"`
	Name      string     `json:"name"`
	Reason    string     `json:"reason"`
	AddedBy   string     `json:"addedBy"`
	AddedAt   time.Time  `json:"addedAt"`
	RemovedBy *string    `json:"removedBy"`
	RemovedAt *time.Time `json:"removedAt"`
}

func (a *AdminService) allowList(w http.ResponseWriter, r *http.Request) {
	if a.refuse(w, r, false) {
		return
	}
	records, err := store.AllowListHistory(r.Context(), a.DB)
	if err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		return
	}
	entries := []allowListEntryResponse{}
	for _, e := range records {
		entries = append(entries, allowListEntryResponse{e.ID, e.Name, e.Reason, e.AddedBy, e.AddedAt, e.RemovedBy, e.RemovedAt})
	}
	writeJSON(w, http.StatusOK, map[string]any{"entries": entries})
}
