package lists

import (
	"archive/zip"
	"bytes"
	"errors"
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

func TestOdsRowsWhoseCellIsLongerThanTheLimitIsAFormatErrorAsItsRead(t *testing.T) {
	// 20,000 runs of a hundred spaces: 500 kB of XML for 2 million characters in one cell.
	content := `<office:document-content ` + odsNamespaces + `><office:body><office:spreadsheet><table:table><table:table-row><table:table-cell><text:p>a` +
		strings.Repeat(`<text:s text:c="100"/>`, 20000) + `b</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>`
	var formatErr *FormatError
	if _, err := OdsRows(zipOf(t, "content.xml", content)); !errors.As(err, &formatErr) {
		t.Fatalf("err = %v, want a format error", err)
	}
}
