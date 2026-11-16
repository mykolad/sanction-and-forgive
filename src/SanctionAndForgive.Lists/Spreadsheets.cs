using System.IO.Compression;
using System.Xml;

namespace SanctionAndForgive.Lists;

/// <summary>Rows of a spreadsheet file, as text: the OpenDocument (<c>.ods</c>) and Office Open XML (<c>.xlsx</c>)
/// formats some publishers use, read from their zip and XML with nothing but the platform (no spreadsheet library).
/// A row is its cells' text, trailing empty cells dropped; empty rows are skipped. A file that isn't the format, whose
/// parts unpack to more than <see cref="MaxPartBytes"/>, whose rows expand (repeated cells and rows) to more text than
/// that, or whose XML is cut short or broken anywhere, is a <see cref="ListFormatException"/>.</summary>
public static class Spreadsheets
{
    /// <summary>The most one part of the file may unpack to: far above any list, far below a zip bomb.</summary>
    public const long MaxPartBytes = 256L * 1024 * 1024;

    /// <summary>Repeated cells beyond this are dropped: a sheet repeats an empty cell up to the last column.</summary>
    private const int MaxColumns = 256;

    private const string OdsTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string OdsText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    /// <summary>The rows of an OpenDocument spreadsheet's first table. The whole document is read, so one cut short
    /// or broken after that table is refused too.</summary>
    public static List<IReadOnlyList<string>> OdsRows(Stream stream, CancellationToken cancellationToken)
    {
        using var zip = OpenZip(stream);
        using var content = OpenPart(zip, "content.xml");
        using var reader = XmlReader.Create(content, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = false });
        var rows = new List<IReadOnlyList<string>>();
        var expanded = new Expansion();
        var depth = -1;
        var done = false;
        try
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (done)
                {
                    continue;
                }

                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "table" && reader.NamespaceURI == OdsTable)
                {
                    done = depth >= 0;
                    depth = done ? depth : reader.Depth;
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "table" && reader.NamespaceURI == OdsTable && reader.Depth == depth)
                {
                    done = true;
                }
                else if (depth >= 0 && reader.NodeType == XmlNodeType.Element && reader.LocalName == "table-row" && reader.NamespaceURI == OdsTable)
                {
                    var repeat = Count(reader.GetAttribute("number-rows-repeated", OdsTable));
                    var row = OdsRow(reader, expanded);
                    if (row.Count > 0)
                    {
                        // A filled row repeated is rare; an empty one repeated to the sheet's end is common, and skipped.
                        for (var i = 1; i < Math.Min(repeat, 1000); i++)
                        {
                            expanded.Add(row.Sum(cell => (long)cell.Length));
                        }

                        for (var i = 0; i < Math.Min(repeat, 1000); i++)
                        {
                            rows.Add(row);
                        }
                    }
                }
            }
        }
        catch (XmlException exception)
        {
            throw new ListFormatException($"The spreadsheet's content.xml isn't valid XML: {exception.Message}");
        }

        if (depth < 0)
        {
            throw new ListFormatException("The spreadsheet has no table.");
        }

        return rows;
    }

    private const string XlsxMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string XlsxRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The rows of an Office Open XML workbook's sheet named <paramref name="sheetName"/>: found through the
    /// workbook and its relationships, cells placed by their reference (<c>C12</c>), shared strings resolved (phonetic
    /// runs left out), numbers as written (a date is a serial number: <see cref="ExcelDate"/>). The sheet and the
    /// shared strings are read to their ends, so one cut short or broken is refused; a shared string's text counts
    /// against the expansion limit each time a cell uses it.</summary>
    public static List<IReadOnlyList<string>> XlsxRows(Stream stream, string sheetName, CancellationToken cancellationToken)
    {
        using var zip = OpenZip(stream);
        try
        {
            var relationshipId = SheetRelationship(zip, sheetName);
            var target = RelationshipTarget(zip, relationshipId);
            var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : SharedStrings(zip);

            using var sheet = OpenPart(zip, target);
            using var reader = XmlReader.Create(sheet, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var rows = new List<IReadOnlyList<string>>();
            var expanded = new Expansion();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row" && reader.NamespaceURI == XlsxMain && !reader.IsEmptyElement)
                {
                    var row = XlsxRow(reader, shared, expanded);
                    if (row.Count > 0)
                    {
                        rows.Add(row);
                    }
                }
            }

            return rows;
        }
        catch (XmlException exception)
        {
            throw new ListFormatException($"The workbook isn't valid XML: {exception.Message}");
        }
    }

    /// <summary>An Excel date (days since 1899-12-30, the 1900 date system) as a date; null when it isn't one.</summary>
    public static DateOnly? ExcelDate(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var serial) && serial is >= 1 and < 2958466
            ? new DateOnly(1899, 12, 30).AddDays((int)Math.Floor(serial))
            : null;

    private static string SheetRelationship(ZipArchive zip, string sheetName)
    {
        using var workbook = OpenPart(zip, "xl/workbook.xml");
        using var reader = XmlReader.Create(workbook, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet" && reader.NamespaceURI == XlsxMain
                && reader.GetAttribute("name") == sheetName && reader.GetAttribute("id", XlsxRelationships) is { } id)
            {
                return id;
            }
        }

        throw new ListFormatException($"The workbook has no sheet named \"{sheetName}\".");
    }

    private static string RelationshipTarget(ZipArchive zip, string id)
    {
        using var relationships = OpenPart(zip, "xl/_rels/workbook.xml.rels");
        using var reader = XmlReader.Create(relationships, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.NamespaceURI == PackageRelationships
                && reader.GetAttribute("Id") == id && reader.GetAttribute("Target") is { } target)
            {
                return target.StartsWith('/') ? target[1..] : "xl/" + target;
            }
        }

        throw new ListFormatException($"The workbook's relationship {id} has no target.");
    }

    /// <summary>Each shared string's text: its <c>t</c> elements, but not those of a phonetic run (<c>rPh</c>).</summary>
    private static List<string> SharedStrings(ZipArchive zip)
    {
        using var part = OpenPart(zip, "xl/sharedStrings.xml");
        using var reader = XmlReader.Create(part, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var strings = new List<string>();
        var current = new System.Text.StringBuilder();
        var phonetic = 0;
        var inT = false;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.NamespaceURI == XlsxMain && reader.LocalName == "si":
                    current.Clear();
                    if (reader.IsEmptyElement)
                    {
                        strings.Add(string.Empty);
                    }

                    break;
                case XmlNodeType.EndElement when reader.NamespaceURI == XlsxMain && reader.LocalName == "si":
                    strings.Add(current.ToString());
                    break;
                case XmlNodeType.Element when reader.NamespaceURI == XlsxMain && reader.LocalName == "rPh" && !reader.IsEmptyElement:
                    phonetic++;
                    break;
                case XmlNodeType.EndElement when reader.NamespaceURI == XlsxMain && reader.LocalName == "rPh":
                    phonetic--;
                    break;
                case XmlNodeType.Element when reader.NamespaceURI == XlsxMain && reader.LocalName == "t" && !reader.IsEmptyElement:
                    inT = true;
                    break;
                case XmlNodeType.EndElement when reader.NamespaceURI == XlsxMain && reader.LocalName == "t":
                    inT = false;
                    break;
                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace or XmlNodeType.CDATA when inT && phonetic == 0:
                    current.Append(reader.Value);
                    break;
            }
        }

        return strings;
    }

    private static List<string> XlsxRow(XmlReader reader, List<string> shared, Expansion expanded)
    {
        var cells = new List<string>();
        var rowDepth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == rowDepth))
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c" || reader.NamespaceURI != XlsxMain)
            {
                continue;
            }

            var column = Column(reader.GetAttribute("r")) ?? cells.Count;
            var type = reader.GetAttribute("t");
            var text = reader.IsEmptyElement ? string.Empty : XlsxCellText(reader, type, shared);
            if (column >= MaxColumns)
            {
                continue;
            }

            while (cells.Count <= column)
            {
                cells.Add(string.Empty);
            }

            expanded.Add(text.Length);
            cells[column] = text;
        }

        return Trimmed(cells);
    }

    /// <summary>A cell's text, read to its end: its value (<c>v</c>), a shared string's text, or an inline string's
    /// <c>t</c> elements (phonetic runs left out).</summary>
    private static string XlsxCellText(XmlReader reader, string? type, List<string> shared)
    {
        var cellDepth = reader.Depth;
        var value = new System.Text.StringBuilder();
        var inline = new System.Text.StringBuilder();
        var phonetic = 0;
        string? element = null;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == cellDepth))
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.NamespaceURI == XlsxMain && reader.LocalName == "rPh" && !reader.IsEmptyElement:
                    phonetic++;
                    break;
                case XmlNodeType.EndElement when reader.NamespaceURI == XlsxMain && reader.LocalName == "rPh":
                    phonetic--;
                    break;
                case XmlNodeType.Element when reader.NamespaceURI == XlsxMain && reader.LocalName is "v" or "t" && !reader.IsEmptyElement:
                    element = reader.LocalName;
                    break;
                case XmlNodeType.EndElement when reader.NamespaceURI == XlsxMain && reader.LocalName is "v" or "t":
                    element = null;
                    break;
                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace or XmlNodeType.CDATA when element == "v":
                    value.Append(reader.Value);
                    break;
                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace or XmlNodeType.CDATA when element == "t" && phonetic == 0:
                    inline.Append(reader.Value);
                    break;
            }
        }

        return type switch
        {
            "s" => int.TryParse(value.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index < shared.Count
                ? shared[index]
                : throw new ListFormatException($"A cell refers to shared string {value}, which the workbook doesn't have."),
            "inlineStr" => inline.ToString(),
            _ => value.ToString(),
        };
    }

    /// <summary>The zero-based column of a cell reference (<c>C12</c> is 2); null without one.</summary>
    private static int? Column(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var column = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            if (c is < 'A' or > 'Z')
            {
                break;
            }

            if (++letters > 3)
            {
                return MaxColumns;
            }

            column = (column * 26) + (c - 'A' + 1);
        }

        return letters == 0 ? null : column - 1;
    }

    /// <summary>The text a sheet expands to, repeats included: a few bytes of XML can repeat a long cell across a row
    /// and the row a thousand times, so it's counted against the same limit as a part.</summary>
    private sealed class Expansion
    {
        private long total;

        public void Add(long characters)
        {
            total += characters;
            if (total > MaxPartBytes)
            {
                throw new ListFormatException($"The spreadsheet's rows expand to more than {MaxPartBytes:N0} characters.");
            }
        }
    }

    private static List<string> OdsRow(XmlReader reader, Expansion expanded)
    {
        var cells = new List<string>();
        if (reader.IsEmptyElement)
        {
            return cells;
        }

        var rowDepth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == rowDepth))
        {
            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != OdsTable || reader.LocalName is not ("table-cell" or "covered-table-cell"))
            {
                continue;
            }

            var repeat = Count(reader.GetAttribute("number-columns-repeated", OdsTable));
            var text = reader.IsEmptyElement ? string.Empty : OdsCellText(reader);
            for (var i = 0; i < repeat && cells.Count < MaxColumns; i++)
            {
                expanded.Add(text.Length);
                cells.Add(text);
            }
        }

        return Trimmed(cells);
    }

    /// <summary>A cell's paragraphs, one a line; <c>text:s</c> is spaces, <c>text:line-break</c> and <c>text:tab</c>
    /// are what they say.</summary>
    private static string OdsCellText(XmlReader reader)
    {
        var cellDepth = reader.Depth;
        var paragraphs = new List<string>();
        var current = new System.Text.StringBuilder();
        var inParagraph = false;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == cellDepth))
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.NamespaceURI == OdsText && reader.LocalName is "p" or "h":
                    inParagraph = !reader.IsEmptyElement;
                    current.Clear();
                    if (!inParagraph)
                    {
                        paragraphs.Add(string.Empty);
                    }

                    break;
                case XmlNodeType.EndElement when reader.NamespaceURI == OdsText && reader.LocalName is "p" or "h":
                    paragraphs.Add(current.ToString());
                    inParagraph = false;
                    break;
                case XmlNodeType.Element when inParagraph && reader.NamespaceURI == OdsText && reader.LocalName == "s":
                    current.Append(' ', Math.Min(Count(reader.GetAttribute("c", OdsText)), 100));
                    break;
                case XmlNodeType.Element when inParagraph && reader.NamespaceURI == OdsText && reader.LocalName is "line-break":
                    current.Append('\n');
                    break;
                case XmlNodeType.Element when inParagraph && reader.NamespaceURI == OdsText && reader.LocalName is "tab":
                    current.Append('\t');
                    break;
                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace or XmlNodeType.CDATA when inParagraph:
                    current.Append(reader.Value);
                    break;
            }
        }

        return string.Join('\n', paragraphs);
    }

    private static int Count(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count > 0 ? count : 1;

    private static List<string> Trimmed(List<string> cells)
    {
        var last = cells.FindLastIndex(cell => !string.IsNullOrWhiteSpace(cell));
        return last < 0 ? [] : cells[..(last + 1)];
    }

    private static ZipArchive OpenZip(Stream stream)
    {
        // The archive needs to seek: the downloader's stream may not.
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        try
        {
            return new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException exception)
        {
            buffer.Dispose();
            throw new ListFormatException($"The file isn't a spreadsheet (zip): {exception.Message}");
        }
    }

    private static LimitedStream OpenPart(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new ListFormatException($"The spreadsheet has no {name}.");
        if (entry.Length > MaxPartBytes)
        {
            throw new ListFormatException($"The spreadsheet's {name} unpacks to {entry.Length:N0} bytes, more than {MaxPartBytes:N0}.");
        }

        return new LimitedStream(entry.Open(), MaxPartBytes, name);
    }

    /// <summary>Stops reading at a limit, whatever the zip's directory claims the part's size is.</summary>
    private sealed class LimitedStream(Stream inner, long limit, string name) : Stream
    {
        private long read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            read += n;
            return read > limit ? throw new ListFormatException($"The spreadsheet's {name} unpacks to more than {limit:N0} bytes.") : n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
