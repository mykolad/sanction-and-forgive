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
    public void Ods_ACellUnderTheLimit_IsReadWhole()
    {
        var text = new string('x', 600_000);
        using var file = Ods(["Title"], [text, "y"]);

        Assert.Equal(text, Spreadsheets.OdsRows(file, CancellationToken.None)[1][0]);
    }

    [Fact]
    public void Ods_WhoseCellIsLongerThanTheLimit_IsAFormatError_AsItsRead()
    {
        // 20,000 runs of a hundred spaces: 500 kB of XML for 2 million characters in one cell.
        const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"";
        var spaces = string.Concat(Enumerable.Repeat("<text:s text:c=\"100\"/>", 20_000));
        var content = $"<office:document-content {ns}><office:body><office:spreadsheet><table:table><table:table-row><table:table-cell><text:p>a{spaces}b</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>";
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
