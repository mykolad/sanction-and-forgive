namespace SanctionAndForgive.Lists;

/// <summary>Turns one source's file into subjects, streaming: a parser never holds the whole document.</summary>
public interface IListParser
{
    /// <summary>The <see cref="ListSource.Code"/> this parser reads.</summary>
    string SourceCode { get; }

    IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken);
}

/// <summary>A file that doesn't have the shape its parser expects (wrong root element, missing ids). An import
/// stops on it, and the active list stays as it was.</summary>
public sealed class ListFormatException(string message) : Exception(message);
