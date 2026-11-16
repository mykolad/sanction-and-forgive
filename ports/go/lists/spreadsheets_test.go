package lists

import (
	"archive/zip"
	"bytes"
	"errors"
	"fmt"
	"strings"
	"testing"
)

const odsNamespaces = `xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"`

func zipOf(t *testing.T, name, content string) *bytes.Reader {
	t.Helper()
	var buffer bytes.Buffer
	archive := zip.NewWriter(&buffer)
	part, err := archive.Create(name)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := part.Write([]byte(content)); err != nil {
		t.Fatal(err)
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	return bytes.NewReader(buffer.Bytes())
}

func TestOdsRowsReadsTheFirstTableWithItsSeparators(t *testing.T) {
	content := `<office:document-content ` + odsNamespaces + `><office:body><office:spreadsheet><table:table><table:table-row>` +
		`<table:table-cell><text:p>Pieter<text:s/>Willem<text:s></text:s>Jansen</text:p></table:table-cell>` +
		`<table:table-cell table:number-columns-repeated="2"/><table:table-cell><text:p>c</text:p></table:table-cell></table:table-row>` +
		`</table:table><table:table><table:table-row><table:table-cell><text:p>second</text:p></table:table-cell></table:table-row></table:table>` +
		`</office:spreadsheet></office:body></office:document-content>`
	rows, err := OdsRows(zipOf(t, "content.xml", content))
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 1 || strings.Join(rows[0], "|") != "Pieter Willem Jansen|||c" {
		t.Fatalf("rows = %q", rows)
	}
}

func TestOdsRowsCutShortOrBrokenAfterTheFirstTableIsAFormatError(t *testing.T) {
	start := `<office:document-content ` + odsNamespaces + `><office:body><office:spreadsheet><table:table><table:table-row><table:table-cell><text:p>a</text:p></table:table-cell></table:table-row>`
	for _, content := range []string{start, start + `</table:table>`, start + `</table:table><table:table></office:spreadsheet>`} {
		var formatErr *FormatError
		if _, err := OdsRows(zipOf(t, "content.xml", content)); !errors.As(err, &formatErr) {
			t.Fatalf("%q: err = %v, want a format error", content, err)
		}
	}
}

func TestOdsRowsWhoseRepeatsExpandPastTheLimitIsAFormatError(t *testing.T) {
	// A cell of 16 KiB repeated across 256 columns, the row a thousand times: about 4 GiB from a few kilobytes.
	content := `<office:document-content ` + odsNamespaces + `><office:body><office:spreadsheet><table:table><table:table-row table:number-rows-repeated="1000">` +
		`<table:table-cell table:number-columns-repeated="256"><text:p>` + strings.Repeat("x", 16*1024) + `</text:p></table:table-cell></table:table-row>` +
		`</table:table></office:spreadsheet></office:body></office:document-content>`
	var formatErr *FormatError
	if _, err := OdsRows(zipOf(t, "content.xml", content)); !errors.As(err, &formatErr) {
		t.Fatalf("err = %v, want a format error", err)
	}
}

// workbookOf is a workbook of one sheet, "Register", written as given, with these shared strings.
func workbookOf(t *testing.T, sheet, sharedStrings string) *bytes.Reader {
	t.Helper()
	var buffer bytes.Buffer
	archive := zip.NewWriter(&buffer)
	for _, part := range [][2]string{
		{"xl/workbook.xml", `<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Register" sheetId="1" r:id="rId1"/></sheets></workbook>`},
		{"xl/_rels/workbook.xml.rels", `<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>`},
		{"xl/sharedStrings.xml", sharedStrings},
		{"xl/worksheets/sheet1.xml", sheet},
	} {
		writer, err := archive.Create(part[0])
		if err != nil {
			t.Fatal(err)
		}
		if _, err := writer.Write([]byte(part[1])); err != nil {
			t.Fatal(err)
		}
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	return bytes.NewReader(buffer.Bytes())
}

func TestXlsxRowsWhoseSheetOrSharedStringsAreCutShortIsAFormatError(t *testing.T) {
	sheet := `<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c></row>`
	strings := `<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>a</t></si>`
	for _, parts := range [][2]string{{sheet, strings + `</sst>`}, {sheet + `</sheetData></worksheet>`, strings}} {
		var formatErr *FormatError
		if _, err := XlsxRows(workbookOf(t, parts[0], parts[1]), "Register"); !errors.As(err, &formatErr) {
			t.Fatalf("%q: err = %v, want a format error", parts, err)
		}
	}
}

func TestXlsxRowsWhoseSharedStringIsUsedPastTheLimitIsAFormatError(t *testing.T) {
	// One shared string of 16 KiB in 20,000 cells: about 320 MB of text from under a megabyte.
	var sheet strings.Builder
	sheet.WriteString(`<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>`)
	for r := 1; r <= 100; r++ {
		sheet.WriteString(`<row>` + strings.Repeat(`<c t="s"><v>0</v></c>`, 200) + `</row>`)
	}
	sheet.WriteString(`</sheetData></worksheet>`)
	shared := `<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>` + strings.Repeat("x", 16*1024) + `</t></si></sst>`
	var formatErr *FormatError
	if _, err := XlsxRows(workbookOf(t, sheet.String(), shared), "Register"); !errors.As(err, &formatErr) {
		t.Fatalf("err = %v, want a format error", err)
	}
}

func TestOdsRowsWhoseSparseRowsRepeatPastTheLimitIsAFormatError(t *testing.T) {
	// A thousand rows of 255 empty cells and an "x", each repeated a thousand times: little text, a million rows.
	row := `<table:table-row table:number-rows-repeated="1000"><table:table-cell table:number-columns-repeated="255"/><table:table-cell><text:p>x</text:p></table:table-cell></table:table-row>`
	content := `<office:document-content ` + odsNamespaces + `><office:body><office:spreadsheet><table:table>` + strings.Repeat(row, 1000) +
		`</table:table></office:spreadsheet></office:body></office:document-content>`
	var formatErr *FormatError
	if _, err := OdsRows(zipOf(t, "content.xml", content)); !errors.As(err, &formatErr) {
		t.Fatalf("err = %v, want a format error", err)
	}
}

func TestXlsxRowsWhoseSparseRowsOpenCellsPastTheLimitIsAFormatError(t *testing.T) {
	// 40,000 rows of one cell in the 256th column: little XML, ten million cell slots.
	var sheet strings.Builder
	sheet.WriteString(`<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>`)
	for r := 1; r <= 40000; r++ {
		fmt.Fprintf(&sheet, `<row><c r="IV%d" t="inlineStr"><is><t>x</t></is></c></row>`, r)
	}
	sheet.WriteString(`</sheetData></worksheet>`)
	var formatErr *FormatError
	if _, err := XlsxRows(workbookOf(t, sheet.String(), `<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"/>`), "Register"); !errors.As(err, &formatErr) {
		t.Fatalf("err = %v, want a format error", err)
	}
}

func TestOdsRowsWhoseCellIsLongerThanTheLimitIsAFormatErrorAsItsRead(t *testing.T) {
	// 20,000 runs of a hundred spaces: 500 kB of XML for 2 million characters in one cell.
	content := `<office:document-content ` + odsNamespaces + `><office:body><office:spreadsheet><table:table><table:table-row><table:table-cell><text:p>a` +
		strings.Repeat(`<text:s text:c="100"/>`, 20000) + `b</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>`
	var formatErr *FormatError
	if _, err := OdsRows(zipOf(t, "content.xml", content)); !errors.As(err, &formatErr) {
		t.Fatalf("err = %v, want a format error", err)
	}
}
