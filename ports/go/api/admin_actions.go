package api

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"time"

	"sanctionandforgive/matching"
	"sanctionandforgive/store"
)

func (a *AdminService) now() time.Time { return a.Server.Screenings.Now() }

// pathID is the {id} route value as an int (the reference's {id:int} constraint): otherwise 404.
func pathID(w http.ResponseWriter, r *http.Request) (int, bool) {
	id, err := strconv.Atoi(r.PathValue("id"))
	if err != nil {
		http.NotFound(w, r)
		return 0, false
	}
	return id, true
}

type reviewResponse struct {
	ImportID int    `json:"importId"`
	Result   string `json:"result"`
	Reloaded bool   `json:"reloaded"`
}

func (a *AdminService) review(w http.ResponseWriter, r *http.Request, confirm bool) {
	user := a.authorize(w, r, true)
	if user == nil {
		return
	}
	id, ok := pathID(w, r)
	if !ok {
		return
	}
	imports := &store.Imports{DB: a.DB, Now: a.now}
	result, err := imports.Review(r.Context(), id, confirm, user.Name)
	if err != nil {
		a.Server.Logger.Error("Reviewing an import failed", "importId", id, "error", err.Error())
		w.WriteHeader(http.StatusInternalServerError)
		return
	}
	a.Server.Logger.Info(fmt.Sprintf("Import %d review: %s by %s", id, result, user.Name))
	switch result {
	case store.ReviewConfirmed, store.ReviewRejected:
		reloaded := a.Loader.Refresh(context.WithoutCancel(r.Context())) == nil
		writeJSON(w, http.StatusOK, reviewResponse{id, string(result), reloaded})
	case store.ReviewNotFound:
		http.NotFound(w, r)
	default:
		writeJSON(w, http.StatusConflict, reviewResponse{id, string(result), false})
	}
}

type listSwitchResponse struct {
	Code     string `json:"code"`
	Result   string `json:"result"`
	Reloaded bool   `json:"reloaded"`
}

func (a *AdminService) switchList(w http.ResponseWriter, r *http.Request, off bool) {
	user := a.authorize(w, r, true)
	if user == nil {
		return
	}
	code := r.PathValue("code")
	var reason *string
	if off {
		var body struct {
			Reason *string `json:"reason"`
		}
		json.NewDecoder(io.LimitReader(r.Body, 1<<16)).Decode(&body) //nolint:errcheck // a missing reason is refused below
		trimmed := strings.TrimSpace(deref(body.Reason))
		if trimmed == "" || utf16Len(trimmed) > 500 {
			badRequest(w, map[string][]string{"Reason": {"1 to 500 characters: why it's switched off. Every result shows it."}})
			return
		}
		reason = &trimmed
	}
	if len(code) > 32 {
		http.NotFound(w, r)
		return
	}
	result, err := store.Switch(r.Context(), a.DB, code, off, reason, user.Name, a.now())
	if err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		return
	}
	direction, answer, already := "on", "On", "AlreadyOn"
	if off {
		direction, answer, already = "off", "SwitchedOff", "AlreadyOff"
	}
	switch result {
	case store.Switched:
		a.Server.Logger.Info(fmt.Sprintf("List %s switched %s by %s", code, direction, user.Name))
		reloaded := a.Loader.Refresh(context.WithoutCancel(r.Context())) == nil
		writeJSON(w, http.StatusOK, listSwitchResponse{code, answer, reloaded})
	case store.SwitchNotFound:
		http.NotFound(w, r)
	default:
		writeJSON(w, http.StatusConflict, listSwitchResponse{code, already, false})
	}
}

type possibleMatch struct {
	Key        string  `json:"key"`
	List       string  `json:"list"`
	ListName   string  `json:"listName"`
	ListedName string  `json:"listedName"`
	Alias      string  `json:"alias"`
	Percent    int     `json:"percent"`
	Programme  *string `json:"programme"`
}

type allowListRefusal struct {
	Code            string          `json:"code"`
	Message         string          `json:"message"`
	PossibleMatches []possibleMatch `json:"possibleMatches"`
}

const maxPossibleMatches = 100

func existsRefusal(id int) allowListRefusal {
	return allowListRefusal{"exists", fmt.Sprintf("This name is already on the allow-list (entry %d). To change its reason, remove it and add it again.", id), []possibleMatch{}}
}

func (a *AdminService) addToAllowList(w http.ResponseWriter, r *http.Request) {
	user := a.authorize(w, r, true)
	if user == nil {
		return
	}
	var body struct {
		Name      *string   `json:"name"`
		Reason    *string   `json:"reason"`
		Confirmed []*string `json:"confirmed"`
	}
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<20)).Decode(&body); err != nil {
		badRequest(w, map[string][]string{"$": {"The request body isn't a valid allow-list entry."}})
		return
	}
	errs := map[string][]string{}
	name, reason := strings.TrimSpace(deref(body.Name)), strings.TrimSpace(deref(body.Reason))
	if name == "" || utf16Len(name) > 300 {
		errs["Name"] = []string{"1 to 300 characters: the name exactly as it will be entered."}
	}
	if reason == "" || utf16Len(reason) > 500 {
		errs["Reason"] = []string{"1 to 500 characters: why this party isn't the listed one."}
	}
	confirmed := map[string]bool{}
	if body.Confirmed != nil {
		valid := len(body.Confirmed) <= maxPossibleMatches
		for _, key := range body.Confirmed {
			if key == nil || *key == "" || utf16Len(*key) > 256 {
				valid = false
			} else {
				confirmed[*key] = true
			}
		}
		if !valid {
			errs["Confirmed"] = []string{"At most 100 keys of 1 to 256 characters, as the possible matches gave them."}
		}
	}
	if len(errs) > 0 {
		badRequest(w, errs)
		return
	}

	var status int
	var response any
	var addedID *int
	var keys []string
	a.Loader.UnderGate(func() {
		status, response, addedID, keys = a.checkAndStore(r, name, reason, user.Name, confirmed)
		if addedID != nil {
			// Stored: reloaded at once, under the same gate, whatever the request's cancellation.
			reloaded := a.Loader.tryLoadAllowList(context.WithoutCancel(r.Context()))
			response = map[string]any{"id": *addedID, "reloaded": reloaded}
		}
	})
	if addedID != nil {
		confirmedText := "none"
		if len(keys) > 0 {
			confirmedText = strings.Join(keys, ", ")
		}
		a.Server.Logger.Info(fmt.Sprintf("Allow-list entry %d added by %s; possible matches confirmed: %s", *addedID, user.Name, confirmedText))
	}
	writeJSON(w, status, response)
}

// checkAndStore runs under the refresh gate, so the lists the name is checked against are the ones in use when it's
// stored (AllowListAdmin.CheckAndStoreAsync).
func (a *AdminService) checkAndStore(r *http.Request, name, reason, by string, confirmed map[string]bool) (int, any, *int, []string) {
	catalog := a.Server.Catalog
	var screened []matching.ScreenedList
	unavailable := !catalog.IsReady() || len(catalog.Lists()) == 0
	listNames := map[string]string{}
	for _, list := range catalog.Lists() {
		listNames[list.Code] = list.Name
		screened = append(screened, matching.ScreenedList{Code: list.Code, Name: list.Name, Index: list.Index})
		unavailable = unavailable || list.Index == nil
	}
	if unavailable {
		return http.StatusServiceUnavailable, allowListRefusal{"unavailable", "A list can't be checked right now (loading, or not current), so the name can't be checked against it. Try again later.", []possibleMatch{}}, nil, nil
	}
	if existing := catalog.AllowList().Find(name); existing != nil {
		return http.StatusConflict, existsRefusal(existing.ID), nil, nil
	}
	hits, _ := a.Server.Screenings.Screener.NameHits(name, screened)
	var match *matching.ListedHit
	for i := range hits {
		if hits[i].Match.Outcome == matching.Match && (match == nil || hits[i].Match.Percent > match.Match.Percent) {
			match = &hits[i]
		}
	}
	if match != nil {
		return http.StatusConflict, allowListRefusal{"listed", fmt.Sprintf("This name is a match on %s: %s: a listed party can't be allow-listed.", listNames[match.List], match.Match.Subject.PrimaryName), []possibleMatch{}}, nil, nil
	}
	seen := map[string]bool{}
	possible := []possibleMatch{}
	for _, hit := range hits {
		key := hit.List + "/" + hit.Match.Subject.ExternalID
		if seen[key] {
			continue
		}
		seen[key] = true
		possible = append(possible, possibleMatch{key, hit.List, listNames[hit.List], hit.Match.Subject.PrimaryName, hit.Match.Alias, hit.Match.Percent, hit.Match.Subject.Programme})
	}
	sort.SliceStable(possible, func(x, y int) bool {
		p, q := possible[x], possible[y]
		switch {
		case p.Percent != q.Percent:
			return p.Percent > q.Percent
		case p.List != q.List:
			return p.List < q.List
		}
		return p.ListedName < q.ListedName
	})
	if len(possible) > maxPossibleMatches {
		return http.StatusConflict, allowListRefusal{"tooBroad", fmt.Sprintf("This name is a possible match for %d listed parties, more than the %d an entry can be checked against: it's too generic to allow-list. Enter the full name, legal form included.", len(possible), maxPossibleMatches), possible[:maxPossibleMatches]}, nil, nil
	}
	for _, p := range possible {
		if !confirmed[p.Key] {
			message := fmt.Sprintf("This name is a possible match for %d listed parties. Once it's on the allow-list, these matches are shown as allowed and no longer raise the result. Add it only if this name is none of them.", len(possible))
			if len(possible) == 1 {
				message = "This name is a possible match for a listed party. Once it's on the allow-list, that match is shown as allowed and no longer raises the result. Add it only if this name is not that party."
			}
			return http.StatusConflict, allowListRefusal{"confirm", message, possible}, nil, nil
		}
	}
	id, existingID, err := store.AllowListAdd(r.Context(), a.DB, name, reason, by, a.now(), func(inForce []store.AllowListRecord) *int {
		entries := make([]matching.AllowListEntry, len(inForce))
		for i, e := range inForce {
			entries[i] = matching.AllowListEntry{ID: e.ID, Name: e.Name, Reason: e.Reason}
		}
		if found := matching.BuildAllowList(entries, "stored").Find(name); found != nil {
			return &found.ID
		}
		return nil
	})
	if err != nil {
		a.Server.Logger.Error("Adding to the allow-list failed", "error", err.Error())
		return http.StatusInternalServerError, map[string]string{}, nil, nil
	}
	if id == nil {
		return http.StatusConflict, existsRefusal(*existingID), nil, nil
	}
	keys := make([]string, len(possible))
	for i, p := range possible {
		keys[i] = p.Key
	}
	return http.StatusOK, nil, id, keys
}

func (a *AdminService) removeFromAllowList(w http.ResponseWriter, r *http.Request) {
	user := a.authorize(w, r, true)
	if user == nil {
		return
	}
	id, ok := pathID(w, r)
	if !ok {
		return
	}
	removed, err := store.AllowListRemove(r.Context(), a.DB, id, user.Name, a.now())
	if err != nil {
		w.WriteHeader(http.StatusInternalServerError)
		return
	}
	if !removed {
		http.NotFound(w, r)
		return
	}
	a.Server.Logger.Info(fmt.Sprintf("Allow-list entry %d removed by %s", id, user.Name))
	reloaded := a.Loader.ReloadAllowList(context.WithoutCancel(r.Context()), &id)
	writeJSON(w, http.StatusOK, map[string]any{"id": id, "reloaded": reloaded})
}
