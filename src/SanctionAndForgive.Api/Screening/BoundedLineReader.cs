using System.Text;

namespace SanctionAndForgive.Api.Screening;

/// <summary>Reads lines from a request body holding at most <c>maxBytes</c> of one line (counted as UTF-8, as the
/// client sent it), so a huge line can't exhaust memory: the rest of it is consumed and the line is reported as too
/// long. Reads in chunks.</summary>
public sealed class BoundedLineReader(TextReader reader, int maxBytes) : IDisposable
{
    private readonly char[] buffer = new char[16 * 1024];
    private int position;
    private int length;

    /// <summary>The next line without its line break, or null at the end. <c>TooLong</c>: the line exceeded the
    /// limit, and <c>Value</c> is empty.</summary>
    public async Task<(string Value, bool TooLong)?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var bytes = 0;
        var tooLong = false;
        var any = false;
        while (true)
        {
            if (position == length)
            {
                length = await reader.ReadAsync(buffer, cancellationToken);
                position = 0;
                if (length == 0)
                {
                    return any ? Finish(line, tooLong) : null;
                }
            }

            any = true;
            while (position < length)
            {
                var c = buffer[position++];
                if (c == '\n')
                {
                    return Finish(line, tooLong);
                }

                if (tooLong)
                {
                    continue;
                }

                // UTF-8 length: a surrogate pair is 4 bytes, so 2 for each half.
                bytes += c < 0x80 ? 1 : c < 0x800 || char.IsSurrogate(c) ? 2 : 3;
                if (bytes > maxBytes)
                {
                    tooLong = true;
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }
        }
    }

    public void Dispose() => reader.Dispose();

    private static (string, bool) Finish(StringBuilder line, bool tooLong) =>
        tooLong ? (string.Empty, true) : (line.ToString().TrimEnd('\r'), false);
}
