package lists

import (
	"html"
	"io"
	"regexp"
	"slices"
	"strings"
	"time"
)

// The US UFLPA Entity List (UflpaParser and HtmlTables in the reference): the DHS page itself, one table per section
// of the Act, aliases written in the name.

const uflpaProgramme = "Uyghur Forced Labor Prevention Act"

var (
	htmlTable       = regexp.MustCompile(`(?is)<table\b[^>]*>(.*?)</table\s*>`)
	htmlRow         = regexp.MustCompile(`(?is)<tr\b[^>]*>(.*?)</tr\s*>`)
	htmlCell        = regexp.MustCompile(`(?is)<t[dh]\b[^>]*>(.*?)</t[dh]\s*>`)
	htmlTag         = regexp.MustCompile(`(?s)<[^>]*>`)
	uflpaAliasLead  = regexp.MustCompile(`(?i)^(?:also known as|formerly known as|(?:and|including) (?:\w+ )?alias(?:es)?:)\s*`)
	uflpaAcronym    = regexp.MustCompile(`^[A-Z0-9&-]{2,12}$`)
	uflpaTrailing   = regexp.MustCompile(`(?i),?\s+(and (?:its subordinate and affiliated entities|subsidiaries))\s*$`)
	uflpaSectionRef = regexp.MustCompile(`Section\s+\d+\([a-z]\)\(\d+\)\([A-Z]\)\([ivx]+\)`)
	htmlTableOpen   = regexp.MustCompile(`(?i)<table\b`)
	htmlTableClose  = regexp.MustCompile(`(?i)</table\s*>`)
	htmlDocumentEnd = regexp.MustCompile(`(?i)</html\s*>\s*$`)
)

type htmlTableRows struct {
	before string
	rows   [][]string
}

// htmlTextOf is a fragment's text: tags as spaces, character references decoded, whitespace collapsed.
func htmlTextOf(fragment string) string {
	return deref(cleanString(html.UnescapeString(htmlTag.ReplaceAllString(fragment, " "))))
}

// htmlIsComplete says whether a page holding this many read tables is whole: as many table openings and closings as
// tables read, and the document's closing tag.
func htmlIsComplete(document string, tables int) bool {
	return len(htmlTableOpen.FindAllStringIndex(document, -1)) == tables && len(htmlTableClose.FindAllStringIndex(document, -1)) == tables &&
		htmlDocumentEnd.MatchString(document)
}

// readHTMLTables reads every table's rows and cells, and the page's text between the previous table and this one.
func readHTMLTables(document string) []htmlTableRows {
	var tables []htmlTableRows
	previousEnd := 0
	for _, match := range htmlTable.FindAllStringSubmatchIndex(document, -1) {
		table := htmlTableRows{before: htmlTextOf(document[previousEnd:match[0]])}
		previousEnd = match[1]
		for _, row := range htmlRow.FindAllStringSubmatch(document[match[2]:match[3]], -1) {
			var cells []string
			for _, cell := range htmlCell.FindAllStringSubmatch(row[1], -1) {
				cells = append(cells, htmlTextOf(cell[1]))
			}
			if len(cells) > 0 {
				table.rows = append(table.rows, cells)
			}
		}
		tables = append(tables, table)
	}
	return tables
}

type uflpaEntity struct {
	primary  string
	names    []string
	sections []string
	remarks  []string
	listedOn *time.Time
}

func parseUflpa(reader io.Reader) ([]ParsedSubject, error) {
	data, err := io.ReadAll(reader)
	if err != nil {
		return nil, err
	}
	tables := readHTMLTables(strings.TrimPrefix(string(data), "\uFEFF"))
	if len(tables) == 0 {
		return nil, formatError("The UFLPA page has no entity tables.")
	}
	// A page cut short loses its last rows or tables without a trace: the document must end, and every table close.
	if !htmlIsComplete(string(data), len(tables)) {
		return nil, formatError("The UFLPA page is cut short: a table or the document doesn't close.")
	}
	var entities []*uflpaEntity
	byKey := map[string]*uflpaEntity{}
	for _, table := range tables {
		if len(table.rows) == 0 || len(table.rows[0]) != 2 ||
			!strings.Contains(strings.ToLower(table.rows[0][0]), "entity") || !strings.Contains(strings.ToLower(table.rows[0][1]), "effective date") {
			return nil, formatError("A UFLPA table has no header (Name of Entity, Effective Date).")
		}
		var section string
		if sections := uflpaSectionRef.FindAllString(table.before, -1); len(sections) > 0 {
			section = sections[len(sections)-1]
		}
		for _, row := range table.rows[1:] {
			written := cleanString(row[0])
			var date *time.Time
			if len(row) == 2 {
				date = ParseDate(s(row[1]), "January 2, 2006")
			}
			if len(row) != 2 || written == nil || date == nil {
				return nil, formatError("A UFLPA row isn't a name and an effective date.")
			}
			primary, aliases, remark := uflpaSplitName(*written)
			// "Co., Ltd" and "Co., Ltd." in two tables are one entity: the first spelling is its id.
			key := strings.TrimRight(primary, ". ")
			entity, ok := byKey[key]
			if !ok {
				entity = &uflpaEntity{primary: primary}
				byKey[key] = entity
				entities = append(entities, entity)
			}
			entity.names = append(entity.names, append(aliases, primary)...)
			if section != "" && !slices.Contains(entity.sections, section) {
				entity.sections = append(entity.sections, section)
			}
			if remark != "" && !slices.Contains(entity.remarks, remark) {
				entity.remarks = append(entity.remarks, remark)
			}
			if entity.listedOn == nil || date.Before(*entity.listedOn) {
				entity.listedOn = date
			}
		}
	}
	var out []ParsedSubject
	for _, entity := range entities {
		b := newBuilder(entity.primary, Entity)
		if len(entity.sections) == 0 {
			b.Programmes = s(uflpaProgramme)
		} else {
			b.Programmes = s(uflpaProgramme + ", " + strings.Join(entity.sections, "; "))
		}
		b.ListedOn = entity.listedOn
		remarks := make([]*string, len(entity.remarks))
		for i := range entity.remarks {
			remarks[i] = &entity.remarks[i]
		}
		b.Remarks = Join("; ", remarks...)
		b.AddName(s(entity.primary), false, true)
		for _, name := range entity.names {
			b.AddName(s(name), false, false)
		}
		add(&out, b.Build())
	}
	return out, nil
}

// uflpaSplitName is a name as written: the name without its alias parentheses or trailing remark, the aliases, and
// the remark ("" if none).
func uflpaSplitName(written string) (string, []string, string) {
	var aliases []string
	var name strings.Builder
	for i := 0; i < len(written); {
		end := -1
		if written[i] == '(' {
			end = closingParenthesis(written, i)
		}
		if end < 0 {
			name.WriteByte(written[i])
			i++
			continue
		}
		inside := strings.TrimSpace(written[i+1 : end])
		if lead := uflpaAliasLead.FindString(inside); lead != "" {
			for _, part := range strings.Split(inside[len(lead):], ";") {
				alias := cleanString(part)
				if alias != nil && len(*alias) >= 4 && strings.EqualFold((*alias)[:4], "and ") {
					alias = cleanString((*alias)[4:])
				}
				if alias != nil {
					aliases = append(aliases, *alias)
				}
			}
		} else if uflpaAcronym.MatchString(inside) {
			aliases = append(aliases, inside)
		} else {
			name.WriteString(written[i : end+1])
		}
		i = end + 1
	}
	primary := written
	if cleaned := cleanString(name.String()); cleaned != nil {
		primary = *cleaned
	}
	remark := ""
	if match := uflpaTrailing.FindStringSubmatchIndex(primary); match != nil && match[0] > 0 {
		remark = deref(cleanString(primary[match[2]:match[3]]))
		if cleaned := cleanString(primary[:match[0]]); cleaned != nil {
			primary = *cleaned
		}
	}
	return primary, aliases, remark
}

// closingParenthesis is the index of the parenthesis closing the one at open, or -1 if it isn't closed.
func closingParenthesis(text string, open int) int {
	depth := 0
	for i := open; i < len(text); i++ {
		switch text[i] {
		case '(':
			depth++
		case ')':
			depth--
		}
		if depth == 0 {
			return i
		}
	}
	return -1
}
