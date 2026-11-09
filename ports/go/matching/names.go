package matching

import (
	"sort"
)

// nameForm is a name reduced to what decides a match (section 5.3).
type nameForm struct {
	Core         []*Token
	Generic      []string
	LegalClasses []string
	Initials     []*Token
	LegalOnly    bool
	IsPerson     bool
}

func (f *nameForm) IsEmpty() bool { return len(f.Core) == 0 }

type tokenKind int

const (
	kindCore tokenKind = iota
	kindLegal
	kindAmbiguousLegal
	kindGeneric
	kindNearGeneric
	kindStop
	kindHonorific
)

const soleTraderClass = "SOLE"

type nameParser struct{ data *MatcherData }

type classified struct {
	token      *Token
	class      tokenKind
	legalClass string
	generic    string
}

// Entity reads a name as a company: legal forms and generic words stripped from the ends.
func (p *nameParser) Entity(tokens []RawToken) *nameForm {
	var items []classified
	for _, raw := range tokens {
		class, legal, generic := p.classify(raw.Token)
		if class != kindStop {
			items = append(items, classified{raw.Token, class, legal, generic})
		}
	}

	hasCore := false
	for _, item := range items {
		if item.class == kindCore || item.class == kindHonorific || item.class == kindAmbiguousLegal {
			hasCore = true
		}
	}
	for i := range items {
		if items[i].class == kindNearGeneric {
			if hasCore {
				items[i].class = kindGeneric
			} else {
				items[i].class = kindCore
			}
		}
	}

	if len(items) > 0 {
		allLegal := true
		for _, item := range items {
			allLegal = allLegal && item.class == kindLegal
		}
		if allLegal {
			var classes []string
			for _, item := range items {
				if !contains(classes, item.legalClass) {
					classes = append(classes, item.legalClass)
				}
			}
			sort.Strings(classes)
			return &nameForm{LegalClasses: classes, LegalOnly: true}
		}
	}

	legalClasses := map[string]bool{}
	hasCoreBesides := func(index int) bool {
		for i, item := range items {
			if i != index && (item.class == kindCore || item.class == kindHonorific) {
				return true
			}
		}
		return false
	}
	hasAnyBesides := func(index int) bool {
		for i, item := range items {
			if i != index && item.class != kindLegal {
				return true
			}
		}
		return false
	}
	for changed := true; changed && len(items) > 1; {
		changed = false
		for _, index := range []int{len(items) - 1, 0} {
			item := items[index]
			if (item.class == kindLegal && hasAnyBesides(index)) || (item.class == kindAmbiguousLegal && hasCoreBesides(index)) {
				legalClasses[item.legalClass] = true
				items = append(items[:index:index], items[index+1:]...)
				changed = true
				break
			}
		}
	}
	kept := items[:0:0]
	for _, item := range items {
		if item.class == kindLegal {
			legalClasses[item.legalClass] = true
		} else {
			kept = append(kept, item)
		}
	}
	items = kept

	classes := make([]string, 0, len(legalClasses))
	for class := range legalClasses {
		classes = append(classes, class)
	}
	sort.Strings(classes)

	var core []*Token
	var genericItems []classified
	for _, item := range items {
		switch item.class {
		case kindCore, kindAmbiguousLegal, kindHonorific:
			core = append(core, item.token)
		case kindGeneric:
			genericItems = append(genericItems, item)
		}
	}
	if len(core) == 0 {
		for _, item := range genericItems {
			core = append(core, item.token)
		}
		return &nameForm{Core: core, Generic: []string{}, LegalClasses: classes}
	}
	var generic []string
	for _, item := range genericItems {
		if !contains(generic, item.generic) {
			generic = append(generic, item.generic)
		}
	}
	sort.Strings(generic)
	return &nameForm{Core: core, Generic: generic, LegalClasses: classes}
}

// Person reads a name as a person's: honorifics dropped, single letters kept as initials.
func (p *nameParser) Person(tokens []RawToken) *nameForm {
	form := &nameForm{IsPerson: true, Generic: []string{}, LegalClasses: []string{}}
	for _, raw := range tokens {
		if raw.Initials != nil {
			form.Initials = append(form.Initials, raw.Initials...)
			continue
		}
		kind, legalClass, _ := p.classify(raw.Token)
		if kind == kindStop || kind == kindHonorific || ((kind == kindLegal || kind == kindAmbiguousLegal) && legalClass == soleTraderClass) {
			continue
		}
		form.Core = append(form.Core, raw.Token)
	}
	return form
}

func (p *nameParser) classify(token *Token) (tokenKind, string, string) {
	if token.IsNumber {
		return kindCore, "", ""
	}
	keys := append([]string{token.Text}, token.Forms...)
	for _, k := range keys {
		if legal, ok := p.data.LegalForms[k]; ok {
			if legal.Ambiguous {
				return kindAmbiguousLegal, legal.Class, ""
			}
			return kindLegal, legal.Class, ""
		}
	}
	for _, k := range keys {
		if canonical, ok := p.data.Generic[k]; ok {
			return kindGeneric, "", canonical
		}
	}
	for _, form := range token.Forms {
		if near := p.data.NearGeneric(form); near != "" {
			return kindNearGeneric, "", near
		}
	}
	for _, k := range keys {
		if p.data.StopWords[k] {
			return kindStop, "", ""
		}
	}
	for _, k := range keys {
		if p.data.Honorifics[k] {
			return kindHonorific, "", ""
		}
	}
	return kindCore, "", ""
}
