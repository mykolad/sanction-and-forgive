package api

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"encoding/base64"
	"encoding/json"
	"log/slog"
	"math/big"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/golang-jwt/jwt/v5"
)

// issuer is a stand-in for Entra: metadata and one signing key.
func issuer(t *testing.T) (*httptest.Server, *rsa.PrivateKey) {
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	var server *httptest.Server
	server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/tenant/v2.0/.well-known/openid-configuration":
			json.NewEncoder(w).Encode(map[string]string{"issuer": server.URL + "/tenant/v2.0", "authorization_endpoint": server.URL + "/authorize", "jwks_uri": server.URL + "/keys"}) //nolint:errcheck
		case "/keys":
			e := base64.RawURLEncoding.EncodeToString(big.NewInt(int64(key.E)).Bytes())
			json.NewEncoder(w).Encode(map[string]any{"keys": []map[string]string{{"kty": "RSA", "use": "sig", "kid": "k1", "n": base64.RawURLEncoding.EncodeToString(key.N.Bytes()), "e": e}}}) //nolint:errcheck
		default:
			http.NotFound(w, r)
		}
	}))
	t.Cleanup(server.Close)
	return server, key
}

func mint(t *testing.T, key *rsa.PrivateKey, claims jwt.MapClaims) string {
	token := jwt.NewWithClaims(jwt.SigningMethodRS256, claims)
	token.Header["kid"] = "k1"
	signed, err := token.SignedString(key)
	if err != nil {
		t.Fatal(err)
	}
	return signed
}

func TestSignIn_AcceptsOnlyTheIssuersTokensForThisApp(t *testing.T) {
	server, key := issuer(t)
	other, _ := rsa.GenerateKey(rand.Reader, 2048)
	signIn := NewSignIn("client", "", server.URL+"/tenant/v2.0", slog.New(slog.DiscardHandler))
	now := time.Now()
	claims := func(audience, iss string, expires time.Time) jwt.MapClaims {
		return jwt.MapClaims{"iss": iss, "aud": audience, "exp": expires.Unix(), "nbf": now.Unix(), "preferred_username": "owner@example.invalid", "roles": []string{adminRole}, "nonce": "n1"}
	}
	issuerURL := server.URL + "/tenant/v2.0"

	good := signIn.validate(context.Background(), mint(t, key, claims("client", issuerURL, now.Add(time.Hour))), "n1")
	if good == nil || good.User.Name != "owner@example.invalid" || !good.User.IsAdmin {
		t.Fatalf("a valid token: %+v", good)
	}
	for name, token := range map[string]string{
		"another key":      mint(t, other, claims("client", issuerURL, now.Add(time.Hour))),
		"another app":      mint(t, key, claims("another-app", issuerURL, now.Add(time.Hour))),
		"another issuer":   mint(t, key, claims("client", server.URL+"/elsewhere/v2.0", now.Add(time.Hour))),
		"expired":          mint(t, key, claims("client", issuerURL, now.Add(-10*time.Minute))),
		"not a token":      "not.a.token",
		"another sign-in ": mint(t, key, claims("client", issuerURL, now.Add(time.Hour))),
	} {
		nonce := ""
		if name == "another sign-in " {
			nonce = "n2"
		}
		if signIn.validate(context.Background(), token, nonce) != nil {
			t.Errorf("%s: accepted", name)
		}
	}
}

func TestLocalReturn_StaysOnThisSite(t *testing.T) {
	for path, want := range map[string]string{"/allow-list": "/allow-list", "//elsewhere.example": "/admin", "/\\elsewhere.example": "/admin", "https://elsewhere.example/": "/admin", "": "/admin", "/\t/elsewhere.example": "/admin"} {
		if got := localReturn(path); got != want {
			t.Errorf("%q: %q, want %q", path, got, want)
		}
	}
}

func TestSignIn_AfterAFailedFetch_WaitsBeforeTryingAgain(t *testing.T) {
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		calls++
		w.WriteHeader(http.StatusServiceUnavailable)
	}))
	defer server.Close()
	now := time.Now()
	signIn := NewSignIn("client", "", server.URL+"/tenant/v2.0", slog.New(slog.DiscardHandler))
	signIn.Now = func() time.Time { return now }

	for range 3 {
		if _, err := signIn.metadata(context.Background(), false); err == nil {
			t.Fatal("no metadata, yet no error")
		}
	}
	if calls != 1 {
		t.Fatalf("%d fetches within a minute of a failed one, want 1", calls)
	}
	now = now.Add(2 * time.Minute)
	signIn.metadata(context.Background(), false) //nolint:errcheck // only the fetch counts
	if calls != 2 {
		t.Fatalf("%d fetches after the minute, want 2", calls)
	}
}
