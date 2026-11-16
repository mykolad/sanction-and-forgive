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
