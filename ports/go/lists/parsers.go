package lists

import (
	"bytes"
	"encoding/json"
	"io"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
)

// Parser reduces one feed's file to subjects (IListParser in the reference).
type Parser func(io.Reader) ([]ParsedSubject, error)

// ParserFor returns the parser for a catalogued feed's code, or nil.
func ParserFor(code string) Parser {
	switch code {
	case "eu":
		return parseEu
	case "ofac-sdn", "ofac-cons":
		return parseOfac
	case "un":
		return parseUn
	case "uk":
		return parseUk
	case "us-export":
		return parseUsExport
	case "ch-seco":
		return parseSeco
	case "ca-sema":
		return parseCanada
	case "ua-sfms":
		return parseSfms
	}
	return nil
}

func s(value string) *string { return &value }

func add(out *[]ParsedSubject, subject *ParsedSubject) {
	if subject != nil {
		*out = append(*out, *subject)
	}
}

func parseEu(reader io.Reader) ([]ParsedSubject, error) {
	var out []ParsedSubject
	err := records(reader, "export", 1, func(entity *Element) error {
		if entity.Name != "sanctionEntity" {
			return nil
		}
		kind := Unknown
		switch code := entity.El("subjectType").Attr("code"); {
		case eq(code, "person"):
			kind = Individual
		case eq(code, "enterprise"):
			kind = Entity
		}
		id := entity.Attr("logicalId")
		if id == nil {
			return formatError("An EU sanctionEntity has no logicalId.")
		}
		regulations := entity.Els("regulation")
		var programmes []*string
		seen := map[string]bool{}
		for _, r := range regulations {
			p := r.Attr("programme")
			key := "\x00"
			if p != nil {
				key = *p
			}
			if !seen[key] {
				seen[key] = true
				programmes = append(programmes, p)
			}
		}
		b := newBuilder(*id, kind)
		b.Programmes = Join(", ", programmes...)
		b.ListedOn = ParseDate(entity.Attr("designationDate"), "2006-01-02")
		var remarks []*string
		for _, r := range entity.Els("remark") {
			remarks = append(remarks, s(r.Value()))
		}
		b.Remarks = Join(" ", remarks...)
		for _, r := range regulations {
			if url := r.Val("publicationUrl"); url != nil {
				b.SourceURL = url
				break
			}
		}
		for _, alias := range entity.Els("nameAlias") {
			name := alias.Attr("wholeName")
			if name == nil {
				name = Join(" ", alias.Attr("firstName"), alias.Attr("middleName"), alias.Attr("lastName"))
			}
			b.AddName(name, eq(alias.Attr("strong"), "false"), false)
		}
		for _, a := range entity.Els("address") {
			raw := Join(", ", a.Attr("street"), a.Attr("poBox"), a.Attr("zipCode"), a.Attr("city"), a.Attr("place"), a.Attr("region"), a.Attr("countryDescription"))
			b.AddAddress(raw, a.Attr("countryIso2Code"))
		}
		add(&out, b.Build())
		return nil
	})
	return out, err
}

func parseOfac(reader io.Reader) ([]ParsedSubject, error) {
	var out []ParsedSubject
	err := records(reader, "sanctionsData", 2, func(entity *Element) error {
		if entity.Name != "entity" {
			return nil
		}
		kind := Unknown
		switch t := entity.El("generalInfo").Val("entityType"); {
		case eq(t, "Individual"):
			kind = Individual
		case eq(t, "Entity"):
			kind = Entity
		case eq(t, "Vessel"), eq(t, "Aircraft"):
			return nil
		}
		id := entity.Attr("id")
		if id == nil {
			return formatError("An OFAC entity has no id.")
		}
		b := newBuilder(*id, kind)
		var programmes []*string
		seen := map[string]bool{}
		for _, p := range entity.El("sanctionsPrograms").Els("sanctionsProgram") {
			if v := p.Value(); !seen[v] {
				seen[v] = true
				programmes = append(programmes, s(v))
			}
		}
		b.Programmes = Join(", ", programmes...)
		for _, list := range entity.El("sanctionsLists").Els("sanctionsList") {
			if date := ParseDate(list.Attr("datePublished"), "2006-01-02"); date != nil && (b.ListedOn == nil || date.Before(*b.ListedOn)) {
				b.ListedOn = date
			}
		}
		b.SourceURL = s("https://sanctionssearch.ofac.treas.gov/Details.aspx?id=" + *id)
		for _, name := range entity.El("names").Els("name") {
			isPrimary := eq(name.Val("isPrimary"), "true")
			isWeak := eq(name.Val("isLowQuality"), "true")
			for _, t := range name.El("translations").Els("translation") {
				fullName := Join(" ", t.Val("formattedFirstName"), t.Val("formattedLastName"))
				if fullName == nil {
					fullName = t.Val("formattedFullName")
				}
				b.AddName(fullName, isWeak, isPrimary && eq(t.Val("isPrimary"), "true"))
			}
		}
		for _, a := range entity.El("addresses").Els("address") {
			country := a.Val("country")
			translations := a.El("translations").Els("translation")
			if len(translations) == 0 {
				b.AddAddress(country, nil)
			}
			for _, t := range translations {
				var parts []*string
				for _, part := range t.El("addressParts").Els("addressPart") {
					parts = append(parts, part.Val("value"))
				}
				b.AddAddress(Join(", ", append(parts, country)...), nil)
			}
		}
		add(&out, b.Build())
		return nil
	})
	return out, err
}

func parseUn(reader io.Reader) ([]ParsedSubject, error) {
	var out []ParsedSubject
	err := records(reader, "CONSOLIDATED_LIST", 2, func(record *Element) error {
		var kind SubjectType
		switch record.Name {
		case "INDIVIDUAL":
			kind = Individual
		case "ENTITY":
			kind = Entity
		default:
			return nil
		}
		id := record.Val("REFERENCE_NUMBER")
		if id == nil {
			id = record.Val("DATAID")
		}
		if id == nil {
			return formatError("A UN record has neither REFERENCE_NUMBER nor DATAID.")
		}
		b := newBuilder(*id, kind)
		b.Programmes = record.Val("UN_LIST_TYPE")
		b.ListedOn = ParseDate(record.Val("LISTED_ON"), "2006-01-02")
		b.Remarks = record.Val("COMMENTS1")
		b.SourceURL = s("https://main.un.org/securitycouncil/en/content/un-sc-consolidated-list")
		b.AddName(Join(" ", record.Val("FIRST_NAME"), record.Val("SECOND_NAME"), record.Val("THIRD_NAME"), record.Val("FOURTH_NAME")), false, true)
		b.AddName(record.Val("NAME_ORIGINAL_SCRIPT"), false, false)
		prefix := "ENTITY"
		if kind == Individual {
			prefix = "INDIVIDUAL"
		}
		for _, alias := range record.Els(prefix + "_ALIAS") {
			quality := alias.Val("QUALITY")
			b.AddName(alias.Val("ALIAS_NAME"), eq(quality, "Low") || eq(quality, "Very Low"), false)
		}
		for _, a := range record.Els(prefix + "_ADDRESS") {
			b.AddAddress(Join(", ", a.Val("STREET"), a.Val("ZIP_CODE"), a.Val("CITY"), a.Val("STATE_PROVINCE"), a.Val("COUNTRY")), nil)
		}
		add(&out, b.Build())
		return nil
	})
	return out, err
}

func parseUk(reader io.Reader) ([]ParsedSubject, error) {
	var out []ParsedSubject
	err := records(reader, "Designations", 1, func(d *Element) error {
		if d.Name != "Designation" {
			return nil
		}
		kind := Unknown
		switch t := d.Val("IndividualEntityShip"); {
		case eq(t, "Individual"):
			kind = Individual
		case eq(t, "Entity"):
			kind = Entity
		case eq(t, "Ship"):
			return nil
		}
		id := d.Val("UniqueID")
		if id == nil {
			return formatError("A UK Designation has no UniqueID.")
		}
		b := newBuilder(*id, kind)
		b.Programmes = d.Val("RegimeName")
		b.ListedOn = ParseDate(d.Val("DateDesignated"), "02/01/2006")
		b.Remarks = d.Val("OtherInformation")
		b.SourceURL = s("https://www.gov.uk/government/publications/the-uk-sanctions-list")
		for _, n := range d.El("Names").Els("Name") {
			fullName := Join(" ", n.Val("Name1"), n.Val("Name2"), n.Val("Name3"), n.Val("Name4"), n.Val("Name5"), n.Val("Name6"))
			strength := n.Val("AliasStrength")
			isWeak := strength != nil && strings.HasPrefix(strings.ToLower(*strength), "low")
			b.AddName(fullName, isWeak, eq(n.Val("NameType"), "Primary Name"))
		}
		for _, n := range d.El("NonLatinNames").Els("NonLatinName") {
			if value := n.Val("NameNonLatinScript"); value != nil {
				for _, spelling := range strings.Split(*value, " / ") {
					b.AddName(s(spelling), false, false)
				}
			}
		}
		for _, a := range d.El("Addresses").Els("Address") {
			b.AddAddress(Join(", ", a.Val("AddressLine1"), a.Val("AddressLine2"), a.Val("AddressLine3"), a.Val("AddressLine4"),
				a.Val("AddressLine5"), a.Val("AddressLine6"), a.Val("AddressPostalCode"), a.Val("AddressCountry")), nil)
		}
		add(&out, b.Build())
		return nil
	})
	return out, err
}

type usAddress struct {
	Address    *string `json:"address"`
	City       *string `json:"city"`
	State      *string `json:"state"`
	PostalCode *string `json:"postal_code"`
	Country    *string `json:"country"`
}

type usRecord struct {
	ID                   *string     `json:"id"`
	Source               *string     `json:"source"`
	Type                 *string     `json:"type"`
	Name                 *string     `json:"name"`
	AltNames             []string    `json:"alt_names"`
	Programs             []string    `json:"programs"`
	Addresses            []usAddress `json:"addresses"`
	StartDate            *string     `json:"start_date"`
	Remarks              *string     `json:"remarks"`
	SourceListURL        *string     `json:"source_list_url"`
	SourceInformationURL *string     `json:"source_information_url"`
}

func parseUsExport(reader io.Reader) ([]ParsedSubject, error) {
	data, err := io.ReadAll(reader)
	if err != nil {
		return nil, err
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	if t, err := decoder.Token(); err != nil || t != json.Delim('{') {
		return nil, formatError("Expected a JSON object.")
	}
	var recordsFound []usRecord
	found := false
	for decoder.More() {
		key, err := decoder.Token()
		if err != nil {
			return nil, err
		}
		if key != "results" {
			var skip json.RawMessage
			if err := decoder.Decode(&skip); err != nil {
				return nil, err
			}
			continue
		}
		if t, err := decoder.Token(); err != nil || t != json.Delim('[') {
			return nil, formatError(`Expected "results" to be an array.`)
		}
		found = true
		recordsFound = nil
		for decoder.More() {
			var raw json.RawMessage
			if err := decoder.Decode(&raw); err != nil {
				return nil, err
			}
			if trimmed := bytes.TrimSpace(raw); len(trimmed) == 0 || trimmed[0] != '{' {
				return nil, formatError(`Expected an object in "results".`)
			}
			var record usRecord
			if err := json.Unmarshal(raw, &record); err != nil {
				return nil, err
			}
			recordsFound = append(recordsFound, record)
		}
		if _, err := decoder.Token(); err != nil {
			return nil, err
		}
	}
	if t, err := decoder.Token(); err != nil || t != json.Delim('}') {
		return nil, formatError("The JSON document doesn't end after its root object.")
	}
	if _, err := decoder.Token(); err != io.EOF {
		return nil, formatError("The JSON document doesn't end after its root object.")
	}
	if !found {
		return nil, formatError(`No "results" array.`)
	}

	var out []ParsedSubject
	for _, record := range recordsFound {
		kept := false
		for _, agency := range []string{"Bureau of Industry and Security", "State Department"} {
			kept = kept || (record.Source != nil && strings.HasSuffix(*record.Source, agency))
		}
		if !kept || eq(record.Type, "Vessel") || eq(record.Type, "Aircraft") {
			continue
		}
		id := Clean(record.ID)
		if id == nil {
			return nil, formatError("A US export record has no id.")
		}
		kind := Unknown
		switch {
		case eq(record.Type, "Individual"):
			kind = Individual
		case eq(record.Type, "Entity"):
			kind = Entity
		}
		b := newBuilder(*id, kind)
		parts := []*string{record.Source}
		for i := range record.Programs {
			parts = append(parts, &record.Programs[i])
		}
		b.Programmes = Join(", ", parts...)
		b.ListedOn = ParseDate(record.StartDate, "2006-01-02")
		b.Remarks = record.Remarks
		b.SourceURL = Clean(record.SourceListURL)
		if b.SourceURL == nil {
			b.SourceURL = Clean(record.SourceInformationURL)
		}
		b.AddName(record.Name, false, true)
		for i := range record.AltNames {
			b.AddName(&record.AltNames[i], false, false)
		}
		for _, a := range record.Addresses {
			b.AddAddress(Join(", ", a.Address, a.City, a.State, a.PostalCode, a.Country), a.Country)
		}
		add(&out, b.Build())
	}
	return out, nil
}

type secoPlace struct {
	text        *string
	countryCode *string
}

type secoPending struct {
	builder   *builder
	addresses [][2]*string
}

func parseSeco(reader io.Reader) ([]ParsedSubject, error) {
	programmes := map[string]string{}
	places := map[string]secoPlace{}
	var pending []secoPending
	err := records(reader, "swiss-sanctions-list", 1, func(record *Element) error {
		switch record.Name {
		case "sanctions-program":
			var key *string
			for _, k := range record.Els("program-key") {
				if eq(k.Attr("lang"), "eng") {
					key = s(k.Value())
					break
				}
			}
			for _, set := range record.Els("sanctions-set") {
				if ssid := set.Attr("ssid"); ssid != nil && key != nil {
					if _, seen := programmes[*ssid]; !seen {
						programmes[*ssid] = *key
					}
				}
			}
		case "target":
			target, err := readSecoTarget(record, programmes)
			if err != nil {
				return err
			}
			if target != nil {
				pending = append(pending, *target)
			}
		case "place":
			if id := record.Attr("ssid"); id != nil {
				country := record.El("country")
				var countryText *string
				if country != nil {
					countryText = s(country.Value())
				}
				places[*id] = secoPlace{Join(", ", record.Val("location"), record.Val("area"), countryText), country.Attr("iso-code")}
			}
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	var out []ParsedSubject
	for _, target := range pending {
		for _, a := range target.addresses {
			var place secoPlace
			if a[1] != nil {
				place = places[*a[1]]
			}
			target.builder.AddAddress(Join(", ", a[0], place.text), place.countryCode)
		}
		add(&out, target.builder.Build())
	}
	return out, nil
}

func readSecoTarget(target *Element, programmes map[string]string) (*secoPending, error) {
	modifications := target.Els("modification")
	if len(modifications) > 0 && eq(modifications[0].Attr("modification-type"), "de-listed") {
		return nil, nil
	}
	subject := target.El("individual")
	if subject == nil {
		subject = target.El("entity")
	}
	if subject == nil {
		return nil, nil
	}
	kind := Entity
	if subject.Name == "individual" {
		kind = Individual
	}
	id := target.Attr("ssid")
	if id == nil {
		return nil, formatError("A SECO target has no ssid.")
	}
	b := newBuilder(*id, kind)
	if setID := target.Val("sanctions-set-id"); setID != nil {
		if p, ok := programmes[*setID]; ok {
			b.Programmes = s(p)
		}
	}
	var listed *Element
	for _, m := range modifications {
		if eq(m.Attr("modification-type"), "listed") {
			listed = m
		}
	}
	b.ListedOn = ParseDate(listed.Attr("effective-date"), "2006-01-02")
	var justifications []*string
	for _, j := range subject.Els("justification") {
		justifications = append(justifications, s(j.Value()))
	}
	b.Remarks = Join(" ", justifications...)
	b.SourceURL = s("https://www.sesam.search.admin.ch/sesam-search-web/pages/search.xhtml?lang=en")
	var addresses [][2]*string
	for _, identity := range subject.Els("identity") {
		mainIdentity := eq(identity.Attr("main"), "true")
		for _, name := range identity.Els("name") {
			isWeak := eq(name.Attr("quality"), "low")
			isPrimary := mainIdentity && eq(name.Attr("name-type"), "primary-name")
			parts := append([]*Element{}, name.Els("name-part")...)
			order := func(e *Element) int {
				if o := e.Attr("order"); o != nil {
					if v, err := strconv.Atoi(*o); err == nil {
						return v
					}
				}
				return 0
			}
			sort.SliceStable(parts, func(a, c int) bool { return order(parts[a]) < order(parts[c]) })
			var values []*string
			for _, p := range parts {
				values = append(values, p.Val("value"))
			}
			b.AddName(Join(" ", values...), isWeak, isPrimary)

			type language struct{ lang, script string }
			var languages []language
			for _, p := range parts {
				for _, v := range p.Els("spelling-variant") {
					l := language{deref(v.Attr("lang")), deref(v.Attr("script"))}
					if !containsLanguage(languages, l, func(a, c language) bool { return a == c }) {
						languages = append(languages, l)
					}
				}
			}
			for _, l := range languages {
				var spellings [][]string
				for _, p := range parts {
					var inLanguage []string
					for _, v := range p.Els("spelling-variant") {
						if deref(v.Attr("lang")) == l.lang && deref(v.Attr("script")) == l.script {
							if c := cleanString(v.Value()); c != nil && !containsString(inLanguage, *c) {
								inLanguage = append(inLanguage, *c)
							}
						}
					}
					if len(inLanguage) == 0 {
						inLanguage = []string{deref(p.Val("value"))}
					}
					spellings = append(spellings, inLanguage)
				}
				for _, fullName := range fullNames(spellings) {
					ptrs := make([]*string, len(fullName))
					for i := range fullName {
						ptrs[i] = &fullName[i]
					}
					b.AddName(Join(" ", ptrs...), isWeak, false)
				}
			}
		}
		for _, a := range identity.Els("address") {
			addresses = append(addresses, [2]*string{Join(", ", a.Val("c-o"), a.Val("p-o-box"), a.Val("address-details"), a.Val("zip-code")), a.Attr("place-id")})
		}
	}
	if !b.HasNames() {
		return nil, nil
	}
	return &secoPending{b, addresses}, nil
}

func deref(v *string) string {
	if v == nil {
		return ""
	}
	return *v
}

func containsLanguage[T any](list []T, value T, equal func(a, b T) bool) bool {
	for _, item := range list {
		if equal(item, value) {
			return true
		}
	}
	return false
}

func containsString(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}

const maxCombinations = 8

// fullNames: every combination of the parts' spellings, up to 8; beyond that, the first spelling of every part plus
// each other spelling swapped in alone, so no spelling is ever dropped.
func fullNames(spellings [][]string) [][]string {
	count := int64(1)
	for _, choice := range spellings {
		count *= int64(len(choice))
	}
	if count <= maxCombinations {
		combinations := [][]string{{}}
		for _, choice := range spellings {
			var next [][]string
			for _, prefix := range combinations {
				for _, option := range choice {
					combination := append(append([]string{}, prefix...), option)
					next = append(next, combination)
				}
			}
			combinations = next
		}
		return combinations
	}
	first := make([]string, len(spellings))
	for i, choice := range spellings {
		first[i] = choice[0]
	}
	names := [][]string{first}
	for part, choice := range spellings {
		for _, other := range choice[1:] {
			name := append(append(append([]string{}, first[:part]...), other), first[part+1:]...)
			names = append(names, name)
		}
	}
	return names
}

var languageLabel = regexp.MustCompile(`^\s*\p{L}+:\s*`)

func parseCanada(reader io.Reader) ([]ParsedSubject, error) {
	var out []ParsedSubject
	err := records(reader, "data-set", 1, func(record *Element) error {
		if record.Name != "record" || record.Val("ShipIMONumber-NumeroOMIDuNavire") != nil {
			return nil
		}
		entityName := record.Val("EntityOrShip-EntiteOuNavire")
		personName := Join(" ", record.Val("GivenName-Prenom"), record.Val("LastName-NomDeFamille"))
		kind := Individual
		if entityName != nil {
			kind = Entity
		}
		country := record.Val("Country-Pays")
		id := Join("/", country, record.Val("Schedule-Annexe"), record.Val("Item-NumeroDarticle"))
		if id == nil {
			return formatError("A Canada SEMA record has no country, schedule or item.")
		}
		b := newBuilder(*id, kind)
		b.Programmes = country
		b.ListedOn = ParseDate(record.Val("DateOfListing-DateDinscription"), "2006-01-02")
		b.SourceURL = s("https://www.international.gc.ca/world-monde/international_relations-relations_internationales/sanctions/consolidated-consolide.aspx?lang=eng")
		if entityName != nil {
			b.AddName(entityName, false, true)
		} else {
			b.AddName(personName, false, true)
		}
		if aliases := record.Val("Aliases-Alias"); aliases != nil {
			for _, alias := range strings.FieldsFunc(*aliases, func(c rune) bool { return c == ',' || c == ';' }) {
				b.AddName(s(languageLabel.ReplaceAllString(alias, "")), false, false)
			}
		}
		add(&out, b.Build())
		return nil
	})
	return out, err
}

func parseSfms(reader io.Reader) ([]ParsedSubject, error) {
	var out []ParsedSubject
	err := records(reader, "list-terror", 1, func(record *Element) error {
		if record.Name != "acount-list" {
			return nil
		}
		kind := Unknown
		switch t := record.Val("type-entry"); {
		case eq(t, "1"):
			kind = Entity
		case eq(t, "2"):
			kind = Individual
		}
		id := record.Val("number-entry")
		if id == nil {
			return formatError("An SFMS record has no number-entry.")
		}
		b := newBuilder(*id, kind)
		b.Programmes = record.Val("program-entry")
		b.ListedOn = ParseDate(record.Val("date-entry"), "20060102")
		b.Remarks = record.Val("comments")
		b.SourceURL = s("https://fiu.gov.ua/")
		for _, aka := range record.Els("aka-list") {
			name := Join(" ", aka.Val("aka-name1"), aka.Val("aka-name2"), aka.Val("aka-name3"), aka.Val("aka-name4"))
			b.AddName(name, eq(aka.Val("quality-aka"), "2"), eq(aka.Val("type-aka"), "N"))
		}
		for _, a := range record.Els("address-list") {
			b.AddAddress(a.Val("address"), nil)
		}
		add(&out, b.Build())
		return nil
	})
	return out, err
}

var _ = time.Time{}
