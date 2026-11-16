package lists

import (
	"archive/zip"
	"bytes"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"strconv"
	"strings"
)

// Spreadsheets in the reference: rows of an OpenDocument (.ods) spreadsheet, read from its zip and XML with the
// standard library. A row is its cells' text, trailing empty cells dropped; empty rows are skipped. A file that isn't
// the format, or whose part unpacks to more than maxPartBytes, is a format error.

const (
	maxPartBytes = 256 << 20
	maxColumns   = 256
	odsTable     = "urn:oasis:names:tc:opendocument:xmlns:table:1.0"
	odsText      = "urn:oasis:names:tc:opendocument:xmlns:text:1.0"
)

// openPart opens one part of a zip held in memory, refusing one that unpacks past the limit.
func openPart(data []byte, name string) (io.Reader, error) {
	archive, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		return nil, formatError("The file isn't a spreadsheet (zip): %s", err.Error())
	}
	for _, file := range archive.File {
		if file.Name != name {
			continue
		}
		if file.UncompressedSize64 > maxPartBytes {
			return nil, formatError("The spreadsheet's %s unpacks to %d bytes, more than %d.", name, file.UncompressedSize64, maxPartBytes)
		}
		reader, err := file.Open()
		if err != nil {
			return nil, formatError("The spreadsheet's %s can't be read: %s", name, err.Error())
		}
		return &limitedReader{reader: reader, left: maxPartBytes, name: name}, nil
	}
	return nil, formatError("The spreadsheet has no %s.", name)
}

// limitedReader stops at the limit, whatever the zip's directory claims the part's size is.
type limitedReader struct {
	reader io.Reader
	left   int64
	name   string
}

func (l *limitedReader) Read(p []byte) (int, error) {
	n, err := l.reader.Read(p)
	l.left -= int64(n)
	if l.left < 0 {
		return n, formatError("The spreadsheet's %s unpacks to more than %d bytes.", l.name, maxPartBytes)
	}
	return n, err
}

func repeatCount(attrs []xml.Attr, local string) int {
	for _, a := range attrs {
		if a.Name.Space == odsTable && a.Name.Local == local {
			if n, err := strconv.Atoi(a.Value); err == nil && n > 0 {
				return n
			}
		}
	}
	return 1
}

// OdsRows reads the rows of an OpenDocument spreadsheet's first table.
func OdsRows(reader io.Reader) ([][]string, error) {
	data, err := io.ReadAll(reader)
	if err != nil {
		return nil, err
	}
	content, err := openPart(data, "content.xml")
	if err != nil {
		return nil, err
	}
	decoder := xml.NewDecoder(content)
	var rows [][]string
	inTable, tableDone := false, false
	for !tableDone {
		token, err := decoder.Token()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			var formatErr *FormatError
			if errors.As(err, &formatErr) {
				return nil, err
			}
			return nil, formatError("The spreadsheet's content.xml isn't valid XML: %s", err.Error())
		}
		switch t := token.(type) {
		case xml.StartElement:
			if t.Name.Space == odsTable && t.Name.Local == "table" {
				if inTable {
					tableDone = true
					break
				}
				inTable = true
			} else if inTable && t.Name.Space == odsTable && t.Name.Local == "table-row" {
				repeat := repeatCount(t.Attr, "number-rows-repeated")
				row, err := odsRow(decoder)
				if err != nil {
					return nil, err
				}
				if len(row) > 0 {
					// A filled row repeated is rare; an empty one repeated to the sheet's end is common, and skipped.
					for i := 0; i < min(repeat, 1000); i++ {
						rows = append(rows, row)
					}
				}
			}
		case xml.EndElement:
			if inTable && t.Name.Space == odsTable && t.Name.Local == "table" {
				tableDone = true
			}
		}
	}
	if !inTable {
		return nil, formatError("The spreadsheet has no table.")
	}
	return rows, nil
}

func odsRow(decoder *xml.Decoder) ([]string, error) {
	var cells []string
	for {
		token, err := decoder.Token()
		if err != nil {
			return nil, formatError("The spreadsheet's content.xml ends inside a row.")
		}
		switch t := token.(type) {
		case xml.StartElement:
			if t.Name.Space == odsTable && (t.Name.Local == "table-cell" || t.Name.Local == "covered-table-cell") {
				repeat := repeatCount(t.Attr, "number-columns-repeated")
				text, err := odsCellText(decoder)
				if err != nil {
					return nil, err
				}
				for i := 0; i < repeat && len(cells) < maxColumns; i++ {
					cells = append(cells, text)
				}
			}
		case xml.EndElement:
			if t.Name.Space == odsTable && t.Name.Local == "table-row" {
				return trimmedCells(cells), nil
			}
		}
	}
}

// odsCellText reads a cell to its end: its paragraphs, one a line; text:s is spaces, text:line-break and text:tab are
// what they say.
func odsCellText(decoder *xml.Decoder) (string, error) {
	var paragraphs []string
	var current strings.Builder
	inParagraph, depth := false, 0
	for {
		token, err := decoder.Token()
		if err != nil {
			return "", formatError("The spreadsheet's content.xml ends inside a cell.")
		}
		switch t := token.(type) {
		case xml.StartElement:
			depth++
			switch {
			case t.Name.Space == odsText && (t.Name.Local == "p" || t.Name.Local == "h"):
				inParagraph = true
				current.Reset()
			case inParagraph && t.Name.Space == odsText && t.Name.Local == "s":
				count := 1
				for _, a := range t.Attr {
					if a.Name.Space == odsText && a.Name.Local == "c" {
						if n, err := strconv.Atoi(a.Value); err == nil && n > 0 {
							count = n
						}
					}
				}
				current.WriteString(strings.Repeat(" ", min(count, 100)))
			case inParagraph && t.Name.Space == odsText && t.Name.Local == "line-break":
				current.WriteByte('\n')
			case inParagraph && t.Name.Space == odsText && t.Name.Local == "tab":
				current.WriteByte('\t')
			}
		case xml.EndElement:
			if depth == 0 {
				return strings.Join(paragraphs, "\n"), nil
			}
			depth--
			if t.Name.Space == odsText && (t.Name.Local == "p" || t.Name.Local == "h") {
				paragraphs = append(paragraphs, current.String())
				inParagraph = false
			}
		case xml.CharData:
			if inParagraph {
				current.Write(t)
			}
		}
	}
}

func trimmedCells(cells []string) []string {
	last := -1
	for i, cell := range cells {
		if strings.TrimSpace(cell) != "" {
			last = i
		}
	}
	return cells[:last+1]
}

// netherlandsProgramme is the Dutch list's legal basis.
const netherlandsProgramme = "Sanctieregeling terrorisme 2007-II"

// parseNetherlands reads the Netherlands' national terrorism list (ODS): NetherlandsParser in the reference.
func parseNetherlands(reader io.Reader) ([]ParsedSubject, error) {
	rows, err := OdsRows(reader)
	if err != nil {
		return nil, err
	}
	is := func(cell, start string) bool {
		return strings.HasPrefix(strings.ToLower(strings.TrimSpace(cell)), start)
	}
	headerAt := -1
	for i, row := range rows {
		if len(row) > 1 && is(row[0], "surname") && is(row[1], "first name") {
			headerAt = i
			break
		}
	}
	if headerAt < 0 {
		return nil, formatError("No header row (Surname, First name(s), …) in the Dutch terrorism list.")
	}
	header := rows[headerAt]
	column := func(start string) (int, error) {
		for i, cell := range header {
			if is(cell, start) {
				return i, nil
			}
		}
		return 0, formatError("The Dutch terrorism list has no %q column.", start)
	}
	columns := map[string]int{}
	for _, name := range []string{"surname", "first name", "alias", "date of birth", "place of birth", "date of ministerial decision", "link official notification"} {
		index, err := column(name)
		if err != nil {
			return nil, err
		}
		columns[name] = index
	}

	var out []ParsedSubject
	ids := map[string]bool{}
	for _, row := range rows[headerAt+1:] {
		cell := func(name string) *string {
			if index := columns[name]; index < len(row) {
				return cleanString(row[index])
			}
			return nil
		}
		family := cell("surname")
		if family == nil {
			continue
		}
		first, where := cell("first name"), cell("place of birth")
		kind := Unknown
		switch {
		case first != nil:
			kind = Individual
		case where != nil && strings.HasPrefix(strings.ToLower(*where), "n/a"):
			kind = Entity
		}

		// One listing per notification; a repeated one (never seen) still gets an id of its own.
		id := cell("link official notification")
		if id == nil {
			id = Join(" ", family, first, cell("date of birth"))
		}
		base := *id
		for n := 2; ids[*id]; n++ {
			if notice := cell("link official notification"); notice != nil {
				base = *notice
			} else {
				base = *family
			}
			id = s(fmt.Sprintf("%s #%d", base, n))
		}
		ids[*id] = true

		b := newBuilder(*id, kind)
		b.Programmes = s(netherlandsProgramme)
		for _, layout := range []string{"2-1-2006", "2/1/2006"} {
			if b.ListedOn = ParseDate(cell("date of ministerial decision"), layout); b.ListedOn != nil {
				break
			}
		}
		var born *string
		if date := cell("date of birth"); date != nil {
			prefix := "born "
			if kind == Entity {
				prefix = "established "
			}
			born = s(prefix + *date)
		}
		b.Remarks = Join("; ", born, where)
		if kind == Individual {
			b.AddName(Join(" ", first, s(dutchSurname(*family))), false, true)
		} else {
			b.AddName(family, false, true)
		}
		b.AddName(cell("alias"), false, false)
		add(&out, b.Build())
	}
	return out, nil
}

// dutchSurname: "Dusseldorp, van" is "van Dusseldorp": a particle written after a comma goes first.
func dutchSurname(family string) string {
	comma := strings.LastIndex(family, ",")
	if comma > 0 && comma < len(family)-1 {
		particle := strings.TrimSpace(family[comma+1:])
		if !strings.Contains(particle, " ") {
			return particle + " " + strings.TrimSpace(family[:comma])
		}
	}
	return family
}
