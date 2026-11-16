package lists

import (
	"errors"
	"strings"
	"testing"
)

// The French register's malformed documents are format errors, never a shorter list (FranceParser's tests in the
// reference): a field or value that isn't the shape expected, as well as a broken document.
func TestFrance_RejectsMalformedDocuments(t *testing.T) {
	for _, document := range []string{
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A"}]}`,
		`{"Publications": {"PublicationDetail": [{"Nature": "Personne morale", "Nom": "A"}]}}`,
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A"}, 7]}}`,
		`{"Publications": {}}`,
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale"}]}}`,
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne physique", "Nom": " ", "RegistreDetail": [{"TypeChamp": "PRENOM", "Valeur": [{"Prenom": "Ivan"}]}]}]}}`,
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": {"TypeChamp": "ALIAS"}}]}}`,
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ALIAS", "Valeur": {"Alias": "B"}}]}]}}`,
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"Valeur": [{"Alias": "B"}]}]}]}}`,
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ALIAS", "Valeur": ["B"]}]}]}}`,
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": ["ALIAS"]}]}}`,
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ALIAS", "Valeur": [{"Alias": 7}]}]}]}}`,
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ADRESSE_PM", "Valeur": [{"Pays": "FRANCE"}]}]}]}}`,
	} {
		_, err := parseFrance(strings.NewReader(document))
		var formatErr *FormatError
		if !errors.As(err, &formatErr) {
			t.Errorf("%s: got %v, want a format error", document, err)
		}
	}
}

func TestFrance_ReadsAWellFormedEntry(t *testing.T) {
	subjects, err := parseFrance(strings.NewReader(`{"Publications": {"PublicationDetail": [{"IdRegistre": 101, "Nature": "Personne physique", "Nom": "SOKOLOV",
		"RegistreDetail": [{"TypeChamp": "PRENOM", "Valeur": [{"Prenom": "Ivan"}]}, {"TypeChamp": "ALIAS", "Valeur": [{"Alias": "Vanya", "Commentaire": "alias faible"}]}]}]}}`))
	if err != nil || len(subjects) != 1 || subjects[0].PrimaryName != "Ivan SOKOLOV" || len(subjects[0].Names) != 2 || !subjects[0].Names[1].IsWeak {
		t.Fatalf("got %+v, %v", subjects, err)
	}
}
