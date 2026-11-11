package api

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"math/big"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/golang-jwt/jwt/v5"
)

// The admin sign-in in the app (AdminSignIn.cs): OpenID Connect with Microsoft Entra ID, the ID token posted back to
// the callback, and that token as the session cookie, checked on every admin request. No secret, nothing on the server.
const (
	sessionCookie  = "saf-admin"
	pendingCookie  = "saf-sign-in"
	signInStart    = "/.auth/login/aad"
	signInCallback = "/.auth/login/aad/callback"
	signOutPage    = "/.auth/logout"
	defaultReturn  = "/admin"
	tokenSkew      = 5 * time.Minute
	maxToken       = 4000
	metadataTTL    = 24 * time.Hour
	keysRefreshGap = 5 * time.Minute
)

// SignIn is Admin:ClientId and Admin:TenantId (or Admin:Authority, any OpenID Connect issuer); on only with both.
type SignIn struct {
	ClientID  string
	Authority string
	Logger    *slog.Logger
	Now       func() time.Time
	Client    *http.Client

	mu        sync.Mutex
	meta      *issuerMetadata
	fetched   time.Time
	refreshed time.Time
}

type issuerMetadata struct {
	Issuer                string `json:"issuer"`
	AuthorizationEndpoint string `json:"authorization_endpoint"`
	JwksURI               string `json:"jwks_uri"`
	keys                  map[string]*rsa.PublicKey
}

// NewSignIn reads the settings by the reference's names.
func NewSignIn(clientID, tenantID, authority string, logger *slog.Logger) *SignIn {
	if authority == "" && tenantID != "" {
		authority = "https://login.microsoftonline.com/" + tenantID + "/v2.0"
	}
	return &SignIn{ClientID: clientID, Authority: authority, Logger: logger, Now: time.Now, Client: &http.Client{Timeout: 30 * time.Second}}
}

func (s *SignIn) Enabled() bool { return s != nil && s.ClientID != "" && s.Authority != "" }

// metadata is the issuer's endpoints and keys, cached for a day; force reads them again (a token named a key it
// doesn't know), at most every five minutes.
func (s *SignIn) metadata(ctx context.Context, force bool) (*issuerMetadata, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	now := s.Now()
	if s.meta != nil && now.Sub(s.fetched) < metadataTTL && (!force || now.Sub(s.refreshed) < keysRefreshGap) {
		return s.meta, nil
	}
	var meta issuerMetadata
	if err := s.getJSON(ctx, strings.TrimRight(s.Authority, "/")+"/.well-known/openid-configuration", &meta); err != nil {
		return s.stale(err)
	}
	var set struct {
		Keys []struct {
			Kty string `json:"kty"`
			Kid string `json:"kid"`
			Use string `json:"use"`
			N   string `json:"n"`
			E   string `json:"e"`
		} `json:"keys"`
	}
	if err := s.getJSON(ctx, meta.JwksURI, &set); err != nil {
		return s.stale(err)
	}
	meta.keys = map[string]*rsa.PublicKey{}
	for _, key := range set.Keys {
		if key.Kty != "RSA" || (key.Use != "" && key.Use != "sig") {
			continue
		}
		n, errN := base64.RawURLEncoding.DecodeString(key.N)
		e, errE := base64.RawURLEncoding.DecodeString(key.E)
		if errN != nil || errE != nil || len(e) > 4 {
			continue
		}
		meta.keys[key.Kid] = &rsa.PublicKey{N: new(big.Int).SetBytes(n), E: int(new(big.Int).SetBytes(e).Int64())}
	}
	s.meta, s.fetched, s.refreshed = &meta, now, now
	return s.meta, nil
}

// stale keeps the keys it had when the issuer can't be reached.
func (s *SignIn) stale(err error) (*issuerMetadata, error) {
	s.Logger.Error("The sign-in issuer's metadata couldn't be read", "error", err.Error())
	if s.meta != nil {
		return s.meta, nil
	}
	return nil, err
}

func (s *SignIn) getJSON(ctx context.Context, address string, into any) error {
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, address, nil)
	if err != nil {
		return err
	}
	response, err := s.Client.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return fmt.Errorf("%s: %d", address, response.StatusCode)
	}
	return json.NewDecoder(io.LimitReader(response.Body, 1<<20)).Decode(into)
}

// session is a signed-in administrator from a valid ID token, and when it ends.
type session struct {
	User    AdminUser
	Expires time.Time
}

// validate checks an ID token: signed by the issuer (RS256) for this app, not expired; with nonce, answering that sign-in.
func (s *SignIn) validate(ctx context.Context, token, nonce string) *session {
	if !s.Enabled() || len(token) == 0 || len(token) > maxToken {
		return nil
	}
	meta, err := s.metadata(ctx, false)
	if err != nil {
		return nil
	}
	keyFor := func(t *jwt.Token) (any, error) {
		kid, _ := t.Header["kid"].(string)
		if key, ok := meta.keys[kid]; ok {
			return key, nil
		}
		// Entra rotates its keys: look again once.
		if refreshed, err := s.metadata(ctx, true); err == nil {
			meta = refreshed
			if key, ok := refreshed.keys[kid]; ok {
				return key, nil
			}
		}
		return nil, errors.New("unknown signing key")
	}
	claims := jwt.MapClaims{}
	parsed, err := jwt.ParseWithClaims(token, claims, keyFor,
		jwt.WithValidMethods([]string{"RS256"}), jwt.WithAudience(s.ClientID), jwt.WithExpirationRequired(),
		jwt.WithLeeway(tokenSkew), jwt.WithTimeFunc(s.Now))
	if err != nil || !parsed.Valid {
		return nil
	}
	// The issuer the metadata names, at the time it was checked.
	if issuer, _ := claims.GetIssuer(); issuer != meta.Issuer {
		return nil
	}
	if nonce != "" {
		said, _ := claims["nonce"].(string)
		if subtle.ConstantTimeCompare([]byte(said), []byte(nonce)) != 1 {
			return nil
		}
	}
	user := AdminUser{Name: "signed-in user"}
	for _, claim := range []string{"preferred_username", "name"} {
		if value, _ := claims[claim].(string); value != "" {
			user.Name = value
			break
		}
	}
	if roles, ok := claims["roles"].([]any); ok {
		for _, role := range roles {
			if role == adminRole {
				user.IsAdmin = true
			}
		}
	}
	expires, _ := claims.GetExpirationTime()
	return &session{User: user, Expires: expires.Time}
}

// localReturn is a path on this site only: never another site's address (//host, /\host).
func localReturn(path string) string {
	if len(path) > 0 && len(path) <= 512 && path[0] == '/' && (len(path) == 1 || (path[1] != '/' && path[1] != '\\')) {
		return path
	}
	return defaultReturn
}

func randomToken() string {
	b := make([]byte, 32)
	rand.Read(b) //nolint:errcheck // never fails (crypto/rand)
	return base64.RawURLEncoding.EncodeToString(b)
}

func plainText(w http.ResponseWriter, status int, text string) {
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.WriteHeader(status)
	io.WriteString(w, text+"\n") //nolint:errcheck // the client went away
}

func pendingCookieOf(value string, maxAge int) *http.Cookie {
	return &http.Cookie{Name: pendingCookie, Value: value, Path: signInStart, MaxAge: maxAge, HttpOnly: true, Secure: true, SameSite: http.SameSiteNoneMode}
}

// start sends the browser to sign in, keeping the state and nonce in a cookie only the callback gets.
func (s *Server) signInStart(w http.ResponseWriter, r *http.Request) {
	if !s.SignIn.Enabled() {
		http.NotFound(w, r)
		return
	}
	meta, err := s.SignIn.metadata(r.Context(), false)
	if err != nil || meta.AuthorizationEndpoint == "" {
		plainText(w, http.StatusServiceUnavailable, "The sign-in service can't be reached right now. Try again in a minute.")
		return
	}
	scheme := "http"
	if r.TLS != nil {
		scheme = "https"
	}
	if forwarded := r.Header.Values("X-Forwarded-Proto"); len(forwarded) > 0 {
		scheme = strings.TrimSpace(forwarded[len(forwarded)-1])
	}
	state, nonce := randomToken(), randomToken()
	query := url.Values{
		"client_id":     {s.SignIn.ClientID},
		"response_type": {"id_token"},
		"response_mode": {"form_post"},
		"redirect_uri":  {scheme + "://" + r.Host + signInCallback},
		"scope":         {"openid profile"},
		"state":         {state},
		"nonce":         {nonce},
	}
	returnTo := localReturn(r.URL.Query().Get("post_login_redirect_uri"))
	http.SetCookie(w, pendingCookieOf(state+"."+nonce+"."+base64.RawURLEncoding.EncodeToString([]byte(returnTo)), 600))
	separator := "?"
	if strings.Contains(meta.AuthorizationEndpoint, "?") {
		separator = "&"
	}
	http.Redirect(w, r, meta.AuthorizationEndpoint+separator+query.Encode(), http.StatusFound)
}

// callback is the issuer's answer: the ID token becomes the session cookie.
func (s *Server) signInCallback(w http.ResponseWriter, r *http.Request) {
	if !s.SignIn.Enabled() {
		http.NotFound(w, r)
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, 64<<10)
	if err := r.ParseForm(); err != nil {
		plainText(w, http.StatusBadRequest, "The sign-in couldn't be completed. Start again from the admin page.")
		return
	}
	http.SetCookie(w, pendingCookieOf("", -1))
	if r.PostForm.Get("error") != "" {
		plainText(w, http.StatusForbidden, "The sign-in was refused. Only lists administrators can sign in here.")
		return
	}
	pending := ""
	if cookie, err := r.Cookie(pendingCookie); err == nil {
		pending = cookie.Value
	}
	parts := strings.Split(pending, ".")
	state, token := r.PostForm.Get("state"), r.PostForm.Get("id_token")
	var signedIn *session
	if len(parts) == 3 && state != "" && subtle.ConstantTimeCompare([]byte(parts[0]), []byte(state)) == 1 {
		signedIn = s.SignIn.validate(r.Context(), token, parts[1])
	}
	if signedIn == nil {
		s.SignIn.Logger.Warn("Admin sign-in refused: the state or the ID token isn't valid")
		plainText(w, http.StatusBadRequest, "The sign-in couldn't be completed. Start again from the admin page.")
		return
	}
	returnTo := defaultReturn
	if decoded, err := base64.RawURLEncoding.DecodeString(parts[2]); err == nil {
		returnTo = localReturn(string(decoded))
	}
	s.SignIn.Logger.Info("Admin sign-in: " + signedIn.User.Name)
	maxAge := int(signedIn.Expires.Sub(s.SignIn.Now()).Seconds())
	if maxAge <= 0 {
		maxAge = 60
	}
	http.SetCookie(w, &http.Cookie{Name: sessionCookie, Value: token, Path: "/", MaxAge: maxAge, HttpOnly: true, Secure: true, SameSite: http.SameSiteLaxMode})
	http.Redirect(w, r, returnTo, http.StatusFound)
}

// signOut ends the session here.
func (s *Server) signOut(w http.ResponseWriter, r *http.Request) {
	http.SetCookie(w, &http.Cookie{Name: sessionCookie, Value: "", Path: "/", MaxAge: -1, HttpOnly: true, Secure: true, SameSite: http.SameSiteLaxMode})
	http.Redirect(w, r, localReturn(r.URL.Query().Get("post_logout_redirect_uri")), http.StatusFound)
}
