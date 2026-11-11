package api

import (
	"database/sql"
	"net/http"
	"sanctionandforgive/lists"
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
	mux.HandleFunc("POST /api/v1/admin/imports/{id}/confirm", func(w http.ResponseWriter, r *http.Request) { a.review(w, r, true) })
	mux.HandleFunc("POST /api/v1/admin/imports/{id}/reject", func(w http.ResponseWriter, r *http.Request) { a.review(w, r, false) })
	mux.HandleFunc("POST /api/v1/admin/allow-list", a.addToAllowList)
	mux.HandleFunc("POST /api/v1/admin/allow-list/{id}/remove", a.removeFromAllowList)
	mux.HandleFunc("POST /api/v1/admin/lists/{code}/switch-off", func(w http.ResponseWriter, r *http.Request) { a.switchList(w, r, true) })
	mux.HandleFunc("POST /api/v1/admin/lists/{code}/switch-on", func(w http.ResponseWriter, r *http.Request) { a.switchList(w, r, false) })
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
	enabled := a.Server.SignIn.Enabled()
	user := a.Server.currentUser(r)
	response := adminMeResponse{Enabled: enabled, SignedIn: user != nil}
	if user != nil {
		response.Name, response.IsAdmin, response.SignOutURL = &user.Name, user.IsAdmin, lists.Optional(signOutPath)
	}
	if enabled {
		response.SignInURL = lists.Optional(signInPath)
	}
	writeJSON(w, http.StatusOK, response)
}

// authorize is the administrator a request is from, its session checked once for the whole request; or nil, with why
// it may not go on written: sign-in off (404), not signed in (401), without the role (403), or a change without the
// site's own header (400).
func (a *AdminService) authorize(w http.ResponseWriter, r *http.Request, change bool) *AdminUser {
	user := a.Server.currentUser(r)
	switch {
	case !a.Server.SignIn.Enabled():
		http.NotFound(w, r)
	case user == nil:
		w.WriteHeader(http.StatusUnauthorized)
	case !user.IsAdmin:
		w.WriteHeader(http.StatusForbidden)
	case change && !fromThisSite(r):
		badRequest(w, map[string][]string{requestHeader: {"Send this request from the admin page."}})
	default:
		return user
	}
	return nil
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
	if a.authorize(w, r, false) == nil {
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
