package matching

import "testing"

// A country name names the street after a street type and a particle ("rue de Malte"), right after a street type with
// more of the address after it ("Via Germania 12"), or right before a street type written after the name ("Almanya
// Caddesi"); at the end after a street type it is still the country ("Some Road, Malta"), since commas are gone.
func TestAddress_AStreetNamedAfterACountry_IsAStreet(t *testing.T) {
	data := Data()
	parser := &addressParser{data: data, tokenizer: &tokenizer{data: data, cache: map[string]*Token{}}}
	for raw, want := range map[string]string{
		"12 rue de Malte, Paris":              "",
		"Almanya Caddesi 12, Istanbul":        "",
		"Via Germania 12, Roma":               "",
		"Corso Italia 3, Milano":              "",
		"12 Jordan Road, Kowloon":             "",
		"Sheikh Zayed Road, UAE":              "AE",
		"Office 4, Some Road, Malta":          "MT",
		"Corso Italia 3, Milano, Italia":      "IT",
		"12 Example Avenue, Germany, 10115":   "DE",
		"12 Example Avenue\nGermany 10115":    "DE",
		"Office 4 Some Road Malta":            "MT",
		"Platz der Türkei 12, Berlin":         "",
		"Ataturk Cad. 12, Istanbul":           "",
		"Kuba Kowalski, Kwiatowa 5, Warszawa": "",
	} {
		if got := parser.Parse(raw, "").Country; got != want {
			t.Errorf("Parse(%q).Country = %q, want %q", raw, got, want)
		}
	}
}

// A final ISO code reads like the country's name: "Via Germania, IT" keeps the street's word, as "Via Germania,
// Italia" does.
func TestAddress_AFinalIsoCode_ReadsLikeTheCountryName(t *testing.T) {
	data := Data()
	parser := &addressParser{data: data, tokenizer: &tokenizer{data: data, cache: map[string]*Token{}}}
	for _, raw := range []string{"Via Germania, IT", "Via Germania, Italia"} {
		if words := parser.Parse(raw, "").Words; len(words) != 1 || words[0].Text != "germania" {
			t.Errorf("Parse(%q) kept %d words, want germania", raw, len(words))
		}
	}
}

// "Sokagi" is "Sokağı" typed without Turkish letters: a street type, dropped like the accented one.
func TestAddress_UnaccentedTurkishStreetTypes_AreDropped(t *testing.T) {
	data := Data()
	parser := &addressParser{data: data, tokenizer: &tokenizer{data: data, cache: map[string]*Token{}}}
	for _, raw := range []string{"Karanfil Sokagi 5", "Karanfil Sokağı 5", "Ataturk Bulvari 5"} {
		if words := parser.Parse(raw, "").Words; len(words) != 1 {
			t.Errorf("Parse(%q) kept %d words, want 1", raw, len(words))
		}
	}
}
