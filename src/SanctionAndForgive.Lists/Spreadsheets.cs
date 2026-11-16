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
