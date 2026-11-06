using System.Text;

namespace SanctionAndForgive.Api.Screening;

/// <summary>Reads lines from a request body holding at most <c>maxLength</c> characters of one line, so a huge line
/// can't exhaust memory: the rest of it is consumed and the line is reported as too long. Reads in chunks.</summary>
public sealed class BoundedLineReader(TextReader reader, int maxLength) : IDisposable
{
    private readonly char[] buffer = new char[16 * 1024];
    private int position;
    private int length;

    /// <summary>The next line without its line break, or null at the end. <c>TooLong</c>: the line exceeded the
    /// limit, and <c>Value</c> is empty.</summary>
    public async Task<(string Value, bool TooLong)?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
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
            var newline = Array.IndexOf(buffer, '\n', position, length - position);
            var end = newline < 0 ? length : newline;
            var take = Math.Min(end - position, Math.Max(0, maxLength - line.Length));
            line.Append(buffer, position, take);
            tooLong |= end - position > take;
            position = newline < 0 ? length : newline + 1;
            if (newline >= 0)
            {
                return Finish(line, tooLong);
            }
        }
    }

    public void Dispose() => reader.Dispose();

    private static (string, bool) Finish(StringBuilder line, bool tooLong) =>
        tooLong ? (string.Empty, true) : (line.ToString().TrimEnd('\r'), false);
}
