package lists

import (
	"archive/zip"
	"bytes"
	"encoding/xml"
	"errors"
	"io"
	"math"
	"strconv"
	"strings"
	"time"
)

// XlsxRows and ExcelDate in the reference's Spreadsheets: the rows of an Office Open XML workbook's named sheet, found
// through the workbook and its relationships, cells placed by their reference, shared strings resolved (phonetic runs
// left out).

const (
	xlsxMain             = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
	xlsxRelationships    = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
	packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships"
)

func xmlParts(data []byte) (*zip.Reader, error) {
	archive, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		return nil, formatError("The file isn't a spreadsheet (zip): %s", err.Error())
	}
	return archive, nil
}

func hasPart(archive *zip.Reader, name string) bool {
	for _, file := range archive.File {
		if file.Name == name {
			return true
		}
	}
	return false
}

func attr(element xml.StartElement, space, local string) (string, bool) {
	for _, a := range element.Attr {
		if a.Name.Space == space && a.Name.Local == local {
			return a.Value, true
		}
	}
	return "", false
}

// eachElement decodes a part, calling visit for every token; a decoding error is a format error.
func eachToken(data []byte, name string, visit func(xml.Token) (bool, error)) error {
	part, err := openPart(data, name)
	if err != nil {
		return err
	}
	decoder := xml.NewDecoder(part)
	for {
		token, err := decoder.Token()
		if errors.Is(err, io.EOF) {
			return nil
		}
		if err != nil {
			var formatErr *FormatError
			if errors.As(err, &formatErr) {
				return err
			}
			return formatError("The workbook's %s isn't valid XML: %s", name, err.Error())
		}
		done, err := visit(token)
		if err != nil || done {
			return err
		}
	}
}

// XlsxRows reads the rows of the workbook's sheet named sheetName. The sheet and the shared strings are read to their
// ends, so one cut short or broken is refused; a shared string's text counts against the expansion limit each time a
// cell uses it.
func XlsxRows(reader io.Reader, sheetName string) ([][]string, error) {
	data, err := io.ReadAll(reader)
	if err != nil {
		return nil, err
	}
	archive, err := xmlParts(data)
	if err != nil {
		return nil, err
	}

	relationshipID := ""
	err = eachToken(data, "xl/workbook.xml", func(token xml.Token) (bool, error) {
		if t, ok := token.(xml.StartElement); ok && t.Name.Space == xlsxMain && t.Name.Local == "sheet" {
			name, _ := attr(t, "", "name")
			if id, ok := attr(t, xlsxRelationships, "id"); ok && name == sheetName {
				relationshipID = id
				return true, nil
			}
		}
		return false, nil
	})
	if err != nil {
		return nil, err
	}
	if relationshipID == "" {
		return nil, formatError("The workbook has no sheet named %q.", sheetName)
	}

	target := ""
	err = eachToken(data, "xl/_rels/workbook.xml.rels", func(token xml.Token) (bool, error) {
		if t, ok := token.(xml.StartElement); ok && t.Name.Space == packageRelationships && t.Name.Local == "Relationship" {
			id, _ := attr(t, "", "Id")
			if value, ok := attr(t, "", "Target"); ok && id == relationshipID {
				if strings.HasPrefix(value, "/") {
					target = value[1:]
				} else {
					target = "xl/" + value
				}
				return true, nil
			}
		}
		return false, nil
	})
	if err != nil {
		return nil, err
	}
	if target == "" {
		return nil, formatError("The workbook's relationship %s has no target.", relationshipID)
	}

	var shared []string
	if hasPart(archive, "xl/sharedStrings.xml") {
		var current strings.Builder
		phonetic, inT := 0, false
		err = eachToken(data, "xl/sharedStrings.xml", func(token xml.Token) (bool, error) {
			switch t := token.(type) {
			case xml.StartElement:
				if t.Name.Space != xlsxMain {
					break
				}
				switch t.Name.Local {
				case "si":
					current.Reset()
				case "rPh":
					phonetic++
				case "t":
					inT = true
				}
			case xml.EndElement:
				if t.Name.Space != xlsxMain {
					break
				}
				switch t.Name.Local {
				case "si":
					shared = append(shared, current.String())
				case "rPh":
					phonetic--
				case "t":
					inT = false
				}
			case xml.CharData:
				if inT && phonetic == 0 {
					current.Write(t)
				}
			}
			return false, nil
		})
		if err != nil {
			return nil, err
		}
	}

	var rows [][]string
	var cells []string
	expanded := &expansion{}
	inRow, column, cellType := false, 0, ""
	var value, inline strings.Builder
	element, phonetic := "", 0
	err = eachToken(data, target, func(token xml.Token) (bool, error) {
		switch t := token.(type) {
		case xml.StartElement:
			if t.Name.Space != xlsxMain {
				break
			}
			switch {
			case t.Name.Local == "row":
				inRow, cells = true, nil
			case inRow && t.Name.Local == "c":
				reference, _ := attr(t, "", "r")
				if c, ok := cellColumn(reference); ok {
					column = c
				} else {
					column = len(cells)
				}
				cellType, _ = attr(t, "", "t")
				value.Reset()
				inline.Reset()
			case t.Name.Local == "rPh":
				phonetic++
			case t.Name.Local == "v" || t.Name.Local == "t":
				element = t.Name.Local
			}
		case xml.EndElement:
			if t.Name.Space != xlsxMain {
				break
			}
			switch {
			case t.Name.Local == "row":
				inRow = false
				if trimmed := trimmedCells(cells); len(trimmed) > 0 {
					rows = append(rows, trimmed)
				}
			case inRow && t.Name.Local == "c":
				text := value.String()
				switch cellType {
				case "s":
					index, err := strconv.Atoi(text)
					if err != nil || index < 0 || index >= len(shared) {
						return false, formatError("A cell refers to shared string %s, which the workbook doesn't have.", text)
					}
					text = shared[index]
				case "inlineStr":
					text = inline.String()
				}
				if column < maxColumns {
					for len(cells) <= column {
						if err := expanded.add(cellBytes); err != nil {
							return false, err
						}
						cells = append(cells, "")
					}
					if err := expanded.add(len(text)); err != nil {
						return false, err
					}
					cells[column] = text
				}
			case t.Name.Local == "rPh":
				phonetic--
			case t.Name.Local == "v" || t.Name.Local == "t":
				element = ""
			}
		case xml.CharData:
			if element == "v" {
				value.Write(t)
			} else if element == "t" && phonetic == 0 {
				inline.Write(t)
			}
		}
		return false, nil
	})
	if err != nil {
		return nil, err
	}
	return rows, nil
}

// cellColumn is the zero-based column of a cell reference ("C12" is 2).
func cellColumn(reference string) (int, bool) {
	column, letters := 0, 0
	for _, c := range reference {
		if c < 'A' || c > 'Z' {
			break
		}
		letters++
		if letters > 3 {
			return maxColumns, true
		}
		column = column*26 + int(c-'A'+1)
	}
	if letters == 0 {
		return 0, false
	}
	return column - 1, true
}

// ExcelDate is an Excel date (days since 1899-12-30, the 1900 date system), or nil when the value isn't one.
func ExcelDate(value *string) *time.Time {
	if value == nil {
		return nil
	}
	serial, err := strconv.ParseFloat(strings.TrimSpace(*value), 64)
	if err != nil || serial < 1 || serial >= 2958466 {
		return nil
	}
	date := time.Date(1899, 12, 30, 0, 0, 0, 0, time.UTC).AddDate(0, 0, int(math.Floor(serial)))
	return &date
}

const (
	newZealandSheet     = "Russia Sanctions Register"
	newZealandProgramme = "Russia Sanctions Act 2022"
)

// parseNewZealand reads New Zealand's Russia Sanctions Register (XLSX): NewZealandParser in the reference.
func parseNewZealand(reader io.Reader) ([]ParsedSubject, error) {
	rows, err := XlsxRows(reader, newZealandSheet)
	if err != nil {
		return nil, err
	}
	is := func(cell, name string) bool { return strings.EqualFold(strings.TrimSpace(cell), name) }
	headerAt := -1
	for i, row := range rows {
		if len(row) > 1 && is(row[0], "Type") && is(row[1], "Unique Identifier") {
			headerAt = i
			break
		}
	}
	if headerAt < 0 {
		return nil, formatError("No header row (Type, Unique Identifier, …) in New Zealand's register.")
	}
	header := rows[headerAt]
	find := func(name string) int {
		for i, cell := range header {
			if is(cell, name) {
				return i
			}
		}
		return -1
	}
	columns := map[string]int{}
	for _, name := range []string{"Type", "Unique Identifier", "First name", "Last name", "Alias/Alternate Spellings", "Address", "Sanction Status", "Date of Sanction"} {
		if columns[name] = find(name); columns[name] < 0 {
			return nil, formatError("New Zealand's register has no %q column.", name)
		}
	}
	for _, name := range []string{"Middle name(s)", "Title", "General Rationale for Sanction"} {
		columns[name] = find(name)
	}

	var out []ParsedSubject
	for _, row := range rows[headerAt+1:] {
		cell := func(name string) *string {
			if index := columns[name]; index >= 0 && index < len(row) {
				return cleanString(row[index])
			}
			return nil
		}
		kindText := cell("Type")
		kind, keep := Unknown, true
		if kindText == nil {
			keep = cell("Unique Identifier") != nil
		} else {
			switch strings.ToUpper(*kindText) {
			case "INDIVIDUAL":
				kind = Individual
			case "ENTITY", "BANK":
				kind = Entity
			case "ASSET", "TOTAL", "SHIP", "AIRCRAFT":
				keep = false
			}
		}
		if status := cell("Sanction Status"); keep && status != nil {
			lower := strings.ToLower(*status)
			for _, word := range []string{"revoked", "delisted", "removed"} {
				if strings.Contains(lower, word) {
					keep = false
				}
			}
		}
		if !keep {
			continue
		}
		id := cell("Unique Identifier")
		if id == nil {
			return nil, formatError("A %s row in New Zealand's register has no Unique Identifier.", deref(kindText))
		}
		b := newBuilder(*id, kind)
		b.Programmes = s(newZealandProgramme)
		if b.ListedOn = ExcelDate(cell("Date of Sanction")); b.ListedOn == nil {
			for _, layout := range []string{"2/1/2006"} {
				if b.ListedOn = ParseDate(cell("Date of Sanction"), layout); b.ListedOn != nil {
					break
				}
			}
		}
		b.Remarks = Join("; ", cell("Title"), cell("General Rationale for Sanction"))
		if kind == Individual {
			b.AddName(Join(" ", cell("First name"), cell("Middle name(s)"), cell("Last name")), false, true)
		} else if first := cell("First name"); first != nil {
			b.AddName(first, false, true)
		} else {
			b.AddName(cell("Last name"), false, true)
		}
		if aliases := cell("Alias/Alternate Spellings"); aliases != nil {
			for _, alias := range strings.Split(*aliases, ";") {
				b.AddName(s(alias), false, false)
			}
		}
		b.AddAddress(cell("Address"), nil)
		add(&out, b.Build())
	}
	return out, nil
}
