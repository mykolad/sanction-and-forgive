package lists

import (
	"errors"
	"fmt"
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
		`{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne physique", "Nom": "A", "RegistreDetail": [{"TypeChamp": "PRENOM", "Valeur": [{"Prenom": "Ivan"}, {"Prenom": 7}]}]}]}}`,
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

func TestFindLinkTakesTheNewestMatchingLinkOnThePagesOwnHost(t *testing.T) {
	document := `<a href="./list20261001.csv">October</a> <a HREF='list20261105.csv'>November</a>
		<a href="https://elsewhere.example/sanctions/list20991231.csv">a copy</a> <a href="./list20261201.xlsx">Excel</a>
		<a href="/sanctions/list20261231.csv?download=1&amp;x=2">with a query</a> <a href = "./list20261104.csv">again</a>`
	link, err := FindLink("https://www.example.go.jp/sanctions/list.html", document, `/list[0-9]{8}\.csv$`)
	if err != nil || link != "https://www.example.go.jp/sanctions/list20261105.csv" {
		t.Fatalf("link = %q, err = %v", link, err)
	}
	var formatErr *FormatError
	if _, err := FindLink("https://www.example.go.jp/sanctions/list.html", `<a href="list.pdf">PDF</a>`, `/list[0-9]{8}\.csv$`); !errors.As(err, &formatErr) {
		t.Fatalf("err = %v, want a format error", err)
	}
}

// The Japanese list, as JapanParser's tests read it in the reference.
func TestJapanReadsPeopleAndGroupsWeakAliasesAndPairedAddresses(t *testing.T) {
	header := []string{"区分", "番号", "告示日付", "告示番号", "個人・団体", "氏名（日本語）", "氏名（英語）", "別名・別称（日本語）", "別名・別称（英語）", "旧称（日本語）", "旧称（英語）",
		"確定に十分でない別名（日本語）", "確定に十分でない別名（英語）", "称号（日本語）", "称号（英語）", "役職（日本語）", "役職（英語）", "生年月日", "出生地（日本語）", "出生地（英語）",
		"国籍（日本語）", "国籍（英語）", "旅券番号", "身分証番号", "住所・所在地（国）（日本語）", "住所・所在地（都市その他の情報）（日本語）", "住所・所在地（国）（英語）",
		"住所・所在地（都市その他の情報）（英語）", "国連参照番号", "リスト掲載日", "その他の情報", "外務省告示情報"}
	row := func(cells map[string]string) string {
		out := make([]string, len(header))
		for i, column := range header {
			if value, ok := cells[column]; ok {
				out[i] = `"` + strings.ReplaceAll(value, `"`, `""`) + `"`
			}
		}
		return strings.Join(out, ",")
	}
	document := "\uFEFF" + strings.Join([]string{
		strings.Join(header, ","),
		row(map[string]string{"区分": "29", "番号": "029-000001", "告示日付": "2022.3.8 2023.1.10改訂", "個人・団体": "個人", "氏名（日本語）": "ソコロフ・イワン",
			"氏名（英語）": "Ivan SOKOLOV; Ivan Petrovich Sokolov", "別名・別称（英語）": "Vanya Sokolov；I. Sokolov", "確定に十分でない別名（英語）": "Big Ivan",
			"生年月日": "1970/1/2", "国籍（英語）": "Russia", "住所・所在地（国）（英語）": "Russian Federation(リスト掲載時点); Cyprus",
			"住所・所在地（都市その他の情報）（英語）": "Moscow,; Limassol,", "住所・所在地（国）（日本語）": "ロシア"}),
		strings.Repeat(",", len(header)-1),
		row(map[string]string{"区分": "2", "番号": "002-000005", "告示日付": "2001.9.22", "個人・団体": "団体", "氏名（英語）": "Northwind Trading LLC",
			"旧称（英語）": "Northwind Export LLC", "住所・所在地（国）（日本語）": "日本", "住所・所在地（都市その他の情報）（日本語）": "東京"}),
		row(map[string]string{"番号": "044-000001", "個人・団体": "その他", "氏名（英語）": "UNNAMED GANG", "住所・所在地（国）（英語）": "Haiti; Haiti",
			"住所・所在地（都市その他の情報）（英語）": "Port-au-Prince"}),
		"",
	}, "\r\n")
	subjects, err := parseJapan(strings.NewReader(document))
	if err != nil {
		t.Fatal(err)
	}
	if len(subjects) != 3 {
		t.Fatalf("subjects = %d", len(subjects))
	}
	person := subjects[0]
	var names []string
	for _, name := range person.Names {
		names = append(names, fmt.Sprintf("%s/%t", name.FullName, name.IsWeak))
	}
	var addresses []string
	for _, address := range person.Addresses {
		addresses = append(addresses, address.Raw)
	}
	if person.ExternalID != "029-000001" || person.Type != Individual || person.PrimaryName != "Ivan SOKOLOV" ||
		strings.Join(names, "|") != "Ivan SOKOLOV/false|Ivan Petrovich Sokolov/false|ソコロフ・イワン/false|Vanya Sokolov/false|I. Sokolov/false|Big Ivan/true" ||
		strings.Join(addresses, "|") != "Moscow, Russian Federation|Limassol, Cyprus" ||
		deref(person.Programmes) != japanProgramme+", category 29" || person.ListedOn.Format("2006-01-02") != "2022-03-08" ||
		deref(person.Remarks) != "born 1970/1/2; nationality Russia" {
		t.Fatalf("person = %+v", person)
	}
	group := subjects[1]
	if group.Type != Entity || group.PrimaryName != "Northwind Trading LLC" || len(group.Addresses) != 1 || group.Addresses[0].Raw != "東京, 日本" {
		t.Fatalf("group = %+v", group)
	}
	other := subjects[2]
	if other.Type != Unknown || len(other.Addresses) != 2 || other.Addresses[0].Raw != "Port-au-Prince" || other.Addresses[1].Raw != "Haiti" {
		t.Fatalf("other = %+v", other)
	}
}
