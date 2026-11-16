using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using SanctionAndForgive.Lists;
using SanctionAndForgive.Lists.Parsers;

namespace SanctionAndForgive.Tests.Lists;

// The spreadsheet formats (Spreadsheets) and the lists published in them, on small fictional files built here.
public class SpreadsheetTests
{
    /// <summary>An OpenDocument spreadsheet with one table of these rows (cells as written).</summary>
    internal static MemoryStream Ods(params string[][] rows)
    {
        const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"";
        var content = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content {ns}><office:body><office:spreadsheet><table:table table:name=\"Sheet1\">");
        foreach (var row in rows)
        {
            content.Append("<table:table-row>");
            foreach (var cell in row)
            {
                content.Append(cell.Length == 0 ? "<table:table-cell/>" : $"<table:table-cell><text:p>{SecurityElement.Escape(cell)}</text:p></table:table-cell>");
            }

            content.Append("<table:table-cell table:number-columns-repeated=\"1017\"/></table:table-row>");
        }

        content.Append("<table:table-row table:number-rows-repeated=\"1048000\"><table:table-cell table:number-columns-repeated=\"1024\"/></table:table-row>");
        content.Append("</table:table></office:spreadsheet></office:body></office:document-content>");
        return Zip(("mimetype", "application/vnd.oasis.opendocument.spreadsheet"), ("content.xml", content.ToString()));
    }

    /// <summary>A workbook with the named sheet (and another before it, to be passed over): cells as written, every
    /// text cell a shared string except those starting with "inline:", which are inline strings; a cell that's a number
    /// is a number.</summary>
    internal static MemoryStream Xlsx(string sheetName, params string[][] rows)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = new List<string>();
        var sheet = new StringBuilder($"<worksheet xmlns=\"{main}\"><sheetData>");
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Append(CultureInfo.InvariantCulture, $"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var cell = rows[r][c];
                var reference = $"{(char)('A' + c)}{r + 1}";
                if (cell.Length == 0)
                {
                    continue;
                }

                if (cell.StartsWith("inline:", StringComparison.Ordinal))
                {
                    sheet.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(cell[7..])}</t></is></c>");
                }
                else if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    sheet.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\"><v>{cell}</v></c>");
                }
                else
                {
                    shared.Add(cell);
                    sheet.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"s\"><v>{shared.Count - 1}</v></c>");
                }
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");
        // Shared strings with a phonetic run (rPh), which isn't the cell's text.
        var strings = new StringBuilder($"<sst xmlns=\"{main}\">");
        foreach (var text in shared)
        {
            strings.Append(CultureInfo.InvariantCulture, $"<si><t>{SecurityElement.Escape(text)}</t><rPh sb=\"0\" eb=\"1\"><t>PHONETIC</t></rPh></si>");
        }

        strings.Append("</sst>");
        var workbook = $"<workbook xmlns=\"{main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Legend\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"{SecurityElement.Escape(sheetName)}\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>";
        const string relationships = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"worksheet\" Target=\"worksheets/sheet2.xml\"/></Relationships>";
        return Zip(
            ("xl/workbook.xml", workbook), ("xl/_rels/workbook.xml.rels", relationships), ("xl/sharedStrings.xml", strings.ToString()),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{main}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Not this one</t></is></c></row></sheetData></worksheet>"),
            ("xl/worksheets/sheet2.xml", sheet.ToString()));
    }

    internal static MemoryStream Zip(params (string Name, string Content)[] parts)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in parts)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Ods_ReadsTheFirstTable_SkipsEmptyRows_AndTrailingEmptyCells()
    {
        using var file = Ods(["Title"], [], ["a", "", "c"]);

        var rows = Spreadsheets.OdsRows(file, CancellationToken.None);

        Assert.Equal([["Title"], ["a", "", "c"]], rows.Select(row => row.ToArray()));
    }

    [Fact]
    public void Ods_SpacesTabsAndLineBreaks_ReadAlike_SelfClosedOrNot()
    {
        const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"";
        const string content = $"<office:document-content {ns}><office:body><office:spreadsheet><table:table><table:table-row><table:table-cell><text:p>Pieter<text:s/>Willem<text:s></text:s>Jansen<text:tab></text:tab>x<text:line-break></text:line-break>y</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>";
        using var file = Zip(("content.xml", content));

        Assert.Equal("Pieter Willem Jansen\tx\ny", Assert.Single(Spreadsheets.OdsRows(file, CancellationToken.None))[0]);
    }

    [Fact]
    public void Ods_CutShortOrBroken_AfterItsFirstTable_IsAFormatError()
    {
        const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"";
        const string start = $"<office:document-content {ns}><office:body><office:spreadsheet><table:table><table:table-row><table:table-cell><text:p>a</text:p></table:table-cell></table:table-row>";

        // Cut short after a complete row, inside the first table; after it; and broken after it.
        foreach (var content in (string[])[start, start + "</table:table>", start + "</table:table><table:table></office:spreadsheet>"])
        {
            using var file = Zip(("content.xml", content));
            Assert.Throws<ListFormatException>(() => Spreadsheets.OdsRows(file, CancellationToken.None));
        }
    }

    [Fact]
    public void Ods_WhoseRepeatsExpandPastTheLimit_IsAFormatError()
    {
        // A cell of 16 KiB repeated across 256 columns, the row a thousand times: about 4 GiB from a few kilobytes.
        const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"";
        var cell = new string('x', 16 * 1024);
        var content = $"<office:document-content {ns}><office:body><office:spreadsheet><table:table><table:table-row table:number-rows-repeated=\"1000\"><table:table-cell table:number-columns-repeated=\"256\"><text:p>{cell}</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>";
        using var file = Zip(("content.xml", content));

        Assert.Throws<ListFormatException>(() => Spreadsheets.OdsRows(file, CancellationToken.None));
    }

    [Fact]
    public void Ods_WhoseSparseRowsRepeatPastTheLimit_IsAFormatError()
    {
        // A thousand rows of 255 empty cells and an "x", each repeated a thousand times: little text, a million rows.
        const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"";
        var row = "<table:table-row table:number-rows-repeated=\"1000\"><table:table-cell table:number-columns-repeated=\"255\"/><table:table-cell><text:p>x</text:p></table:table-cell></table:table-row>";
        var content = $"<office:document-content {ns}><office:body><office:spreadsheet><table:table>{string.Concat(Enumerable.Repeat(row, 1000))}</table:table></office:spreadsheet></office:body></office:document-content>";
        using var file = Zip(("content.xml", content));

        Assert.Throws<ListFormatException>(() => Spreadsheets.OdsRows(file, CancellationToken.None));
    }

    [Fact]
    public void Ods_ThatIsntAZip_OrHasNoContent_IsAFormatError()
    {
        using var notZip = new MemoryStream(Encoding.UTF8.GetBytes("Surname,First name"));
        using var noContent = Zip(("mimetype", "application/vnd.oasis.opendocument.spreadsheet"));

        Assert.Throws<ListFormatException>(() => Spreadsheets.OdsRows(notZip, CancellationToken.None));
        Assert.Throws<ListFormatException>(() => Spreadsheets.OdsRows(noContent, CancellationToken.None));
    }

    [Fact]
    public void Xlsx_ReadsTheNamedSheet_SharedAndInlineStrings_ByCellReference_WithoutPhoneticRuns()
    {
        using var file = Xlsx("Register", ["Name", "", "Since"], [], ["inline:Inline text", "", "44638"]);

        var rows = Spreadsheets.XlsxRows(file, "Register", CancellationToken.None);

        Assert.Equal([["Name", "", "Since"], ["Inline text", "", "44638"]], rows.Select(row => row.ToArray()));
        Assert.Equal(new DateOnly(2022, 3, 18), Spreadsheets.ExcelDate(rows[1][2]));
    }

    [Fact]
    public void Xlsx_WithoutTheNamedSheet_IsAFormatError()
    {
        using var file = Xlsx("Register", ["Name"]);

        Assert.Throws<ListFormatException>(() => Spreadsheets.XlsxRows(file, "Another", CancellationToken.None));
    }

    [Fact]
    public void Xlsx_WhoseSheetOrSharedStringsAreCutShort_IsAFormatError()
    {
        const string sheet = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row>";
        const string strings = "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>a</t></si>";

        foreach (var (sheetXml, stringsXml) in new[] { (sheet, strings + "</sst>"), (sheet + "</sheetData></worksheet>", strings) })
        {
            using var file = Workbook(sheetXml, stringsXml);
            Assert.Throws<ListFormatException>(() => Spreadsheets.XlsxRows(file, "Register", CancellationToken.None));
        }
    }

    [Fact]
    public void Xlsx_WhoseSharedStringIsUsedPastTheLimit_IsAFormatError()
    {
        // One shared string of 16 KiB in 20,000 cells: about 320 MB of text from under a megabyte.
        var sheet = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 1; r <= 100; r++)
        {
            sheet.Append(CultureInfo.InvariantCulture, $"<row r=\"{r}\">");
            for (var c = 0; c < 200; c++)
            {
                sheet.Append("<c t=\"s\"><v>0</v></c>");
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");
        using var file = Workbook(sheet.ToString(), $"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>{new string('x', 16 * 1024)}</t></si></sst>");

        Assert.Throws<ListFormatException>(() => Spreadsheets.XlsxRows(file, "Register", CancellationToken.None));
    }

    [Fact]
    public void Xlsx_WhoseSparseRowsOpenCellsPastTheLimit_IsAFormatError()
    {
        // 40,000 rows of one cell in the 256th column: little XML, ten million cell slots.
        var sheet = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 1; r <= 40_000; r++)
        {
            sheet.Append(CultureInfo.InvariantCulture, $"<row><c r=\"IV{r}\" t=\"inlineStr\"><is><t>x</t></is></c></row>");
        }

        sheet.Append("</sheetData></worksheet>");
        using var file = Workbook(sheet.ToString(), "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>");

        Assert.Throws<ListFormatException>(() => Spreadsheets.XlsxRows(file, "Register", CancellationToken.None));
    }

    /// <summary>A workbook of one sheet, "Register", written as given, with these shared strings.</summary>
    private static MemoryStream Workbook(string sheet, string sharedStrings) => Zip(
        ("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Register\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
        ("xl/sharedStrings.xml", sharedStrings),
        ("xl/worksheets/sheet1.xml", sheet));

    private static readonly string[] NewZealandHeader =
    [
        "Type", "Unique Identifier", "First name", "Middle name(s)", "Last name", "Name of Asset", "Name of Service", "DOB", "Title",
        "Alias/Alternate Spellings", "Address", "Place of Birth", "Citizenship", "Citizenship 2", "Citizenship 3", "Passport Number",
        "Sanction Status", "Date of Sanction", "Date of Additional Sanction", "Travel Ban", "Asset Freeze", "Aircraft Ban", "Ship Ban",
        "Service Prohibition", "Dealing with Securities", "General Rationale for Sanction", "Associates/Relatives",
    ];

    private static string[] NewZealandRow(string type, string id, string first, string middle, string last, string alias, string address, string status, string date, string rationale)
    {
        var row = new string[NewZealandHeader.Length];
        Array.Fill(row, string.Empty);
        (row[0], row[1], row[2], row[3], row[4], row[9], row[10], row[16], row[17], row[25]) = (type, id, first, middle, last, alias, address, status, date, rationale);
        return row;
    }

    [Fact]
    public void NewZealand_ReadsPeopleEntitiesAndBanks_LeavesOutAssetsTotalsAndRevoked()
    {
        using var file = Xlsx(
            NewZealandParser.Sheet,
            ["Sanction Type", "Link to Description"],
            ["Asset Freeze", "Russia Sanctions Regulations 2022"],
            NewZealandHeader,
            NewZealandRow("Individual", "IND-1", "Ivan", "Petrovich", "Sokolov", "Vanya Sokolov; I. P. Sokolov", "", "Sanctioned", "44638", "Member of a fictional board"),
            NewZealandRow("Entity", "ENT-1", "Northwind Trading JSC", "", "", "Northwind; NWT", "12 Fictional Street, Moscow, Russian Federation", "Sanctioned", "44700", ""),
            NewZealandRow("Bank", "BAN-1", "Fictional Bank", "", "", "", "", "Sanctioned", "44638", ""),
            NewZealandRow("Asset", "AST-1", "", "", "", "", "", "Sanctioned", "44638", ""),
            NewZealandRow("Individual", "IND-2", "Pavel", "", "Revokov", "", "", "Revoked", "44638", ""),
            ["Total", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "3"]);

        var subjects = new NewZealandParser().Parse(file, CancellationToken.None).ToList();

        Assert.Equal(["IND-1", "ENT-1", "BAN-1"], subjects.Select(subject => subject.ExternalId));
        Assert.Equal((SubjectType.Individual, "Ivan Petrovich Sokolov"), (subjects[0].Type, subjects[0].PrimaryName));
        Assert.Equal(["Ivan Petrovich Sokolov", "Vanya Sokolov", "I. P. Sokolov"], subjects[0].Names.Select(name => name.FullName));
        Assert.Equal((new DateOnly(2022, 3, 18), NewZealandParser.Programme, "Member of a fictional board"), (subjects[0].ListedOn, subjects[0].Programmes, subjects[0].Remarks));
        Assert.Equal((SubjectType.Entity, "Northwind Trading JSC"), (subjects[1].Type, subjects[1].PrimaryName));
        Assert.Equal("12 Fictional Street, Moscow, Russian Federation", Assert.Single(subjects[1].Addresses).Raw);
        Assert.Equal(SubjectType.Entity, subjects[2].Type);
    }

    [Fact]
    public void NewZealand_WithoutItsHeader_OrARowWithoutItsId_IsAFormatError()
    {
        using var noHeader = Xlsx(NewZealandParser.Sheet, ["Kind", "Id"], ["Individual", "IND-1"]);
        using var noId = Xlsx(NewZealandParser.Sheet, NewZealandHeader, NewZealandRow("Individual", "", "Ivan", "", "Sokolov", "", "", "Sanctioned", "44638", ""));

        Assert.Throws<ListFormatException>(() => new NewZealandParser().Parse(noHeader, CancellationToken.None).ToList());
        Assert.Throws<ListFormatException>(() => new NewZealandParser().Parse(noId, CancellationToken.None).ToList());
    }

    [Fact]
    public void Netherlands_ReadsPersonsAndOrganisations_ByTheirHeader()
    {
        using var file = Ods(
            ["Persons and entities whose assets have been frozen"],
            ["Surname", "First name(s)", "Alias", "Date of Birth(DD-MM-JJJJ)", "Place of Birth", "Date of ministerial decision (DD/MM/JJJJ)", "Link official notification"],
            ["Jansen", "Pieter Willem", "", "10-10-1990", "Utrecht", "7-12-2016", "Stcrt 68135"],
            ["Dijk, van", "Anna", "Umm Fictief", "1-2-1994", "Assen", "21-3-2016", "Stcrt 2016, 15791"],
            ["Stichting Fictief", "", "", "", "n/a: established in Rotterdam", "18-4-2011", "Stcrt 2011, 7209"]);

        var subjects = new NetherlandsParser().Parse(file, CancellationToken.None).ToList();

        Assert.Equal(["Stcrt 68135", "Stcrt 2016, 15791", "Stcrt 2011, 7209"], subjects.Select(subject => subject.ExternalId));
        Assert.Equal((SubjectType.Individual, "Pieter Willem Jansen"), (subjects[0].Type, subjects[0].PrimaryName));
        Assert.Equal(["Anna van Dijk", "Umm Fictief"], subjects[1].Names.Select(name => name.FullName));
        Assert.Equal((SubjectType.Entity, "Stichting Fictief"), (subjects[2].Type, subjects[2].PrimaryName));
        Assert.Equal((new DateOnly(2016, 12, 7), NetherlandsParser.Programme, "born 10-10-1990; Utrecht"), (subjects[0].ListedOn, subjects[0].Programmes, subjects[0].Remarks));
        Assert.Empty(subjects[0].Addresses);
    }

    [Fact]
    public void Netherlands_WithoutItsHeader_IsAFormatError()
    {
        using var file = Ods(["Name", "Born"], ["Jansen", "1990"]);

        Assert.Throws<ListFormatException>(() => new NetherlandsParser().Parse(file, CancellationToken.None).ToList());
    }
}
