using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Controllers;

/// <summary><c>POST /api/v1/lists/{code}/browse</c>: a page of a loaded list's entries, in name order, optionally
/// filtered by text and type. It reads the list in memory, exactly what a screening searches, never SQL. The
/// filter text comes in the body, never the URL, and is neither stored nor logged, like a screening's input.</summary>
[ApiController]
[Route("api/v1/lists/{code}/browse")]
public sealed class BrowseController(ListCatalog catalog, TimeProvider clock) : ControllerBase
{
    public const int MaxPageSize = 100;

    public const int MaxQueryLength = 100;

    /// <summary>Each index's entries in name order with their names folded for the filter, built once per index (an
    /// index never changes, and the cache goes with it).</summary>
    private static readonly ConditionalWeakTable<ListIndex, Browsable[]> Sorted = [];

    [HttpPost]
    [EnableRateLimiting(RateLimits.Screenings)]
    [ProducesResponseType<BrowseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WarmingUpResponse>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Browse(string code, BrowseRequest body)
    {
        if (!catalog.IsReady)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new WarmingUpResponse("warmingUp"));
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (body.Query?.Length > MaxQueryLength)
        {
            errors[nameof(body.Query)] = [$"At most {MaxQueryLength} characters."];
        }

        if (body.Offset < 0)
        {
            errors[nameof(body.Offset)] = ["Zero or more."];
        }

        if (body.Limit is < 1 or > MaxPageSize)
        {
            errors[nameof(body.Limit)] = [$"From 1 to {MaxPageSize}."];
        }

        SubjectKind? kind = null;
        if (body.Type is { Length: > 0 } type)
        {
            if (Enum.TryParse<SubjectKind>(type, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                kind = parsed;
            }
            else
            {
                errors[nameof(body.Type)] = ["Individual, Entity or Unknown."];
            }
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        var list = catalog.Lists.FirstOrDefault(list => string.Equals(list.Code, code, StringComparison.OrdinalIgnoreCase));
        if (list is null)
        {
            return NotFound();
        }

        var now = clock.GetUtcNow();
        IEnumerable<Browsable> entries = list.Index is { } index ? Sorted.GetValue(index, InNameOrder) : [];
        if (kind is { } wanted)
        {
            entries = entries.Where(entry => entry.Subject.Kind == wanted);
        }

        var id = body.Query?.Trim() ?? string.Empty;
        var query = Fold(id);
        if (query.Length > 0)
        {
            entries = entries.Where(entry =>
                string.Equals(entry.Subject.ExternalId, id, StringComparison.OrdinalIgnoreCase)
                || entry.Folded.Any(name => name.Contains(query, StringComparison.Ordinal)));
        }

        var matching = entries.Select(entry => entry.Subject).ToList();
        return Ok(new BrowseResponse(
            ListResponse.From(list, now),
            matching.Count,
            body.Offset,
            body.Limit,
            [.. matching.Skip(body.Offset).Take(body.Limit).Select(BrowseEntry.From)]));
    }

    /// <summary>In order of the folded primary name without leading punctuation (so "'Tango Four'" sorts under T and
    /// "«Ромашка»" under Р), then by id.</summary>
    private static Browsable[] InNameOrder(ListIndex index) =>
        [.. index.Subjects
            .Select(subject => new Browsable(subject, [.. subject.Names.Select(name => Fold(name.FullName))]))
            .OrderBy(entry => SortKey(entry.Subject.PrimaryName), StringComparer.Ordinal)
            .ThenBy(entry => entry.Subject.ExternalId, StringComparer.Ordinal)];

    private static string SortKey(string name)
    {
        var folded = Fold(name);
        var start = 0;
        while (start < folded.Length && !char.IsLetterOrDigit(folded[start]))
        {
            start++;
        }

        return folded[start..];
    }

    private sealed record Browsable(ListedSubject Subject, string[] Folded);

    /// <summary>For the filter: lower case, no diacritics (so "Mueller" isn't needed for "Müller", but "Muller" finds
    /// it), spaces collapsed. Cyrillic stays Cyrillic: this is a text filter, not matching.</summary>
    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString().TrimEnd();
    }
}

/// <param name="Query">Text to find in any of an entry's names (case and diacritics ignored), or its exact id.</param>
/// <param name="Type"><c>Individual</c>, <c>Entity</c> or <c>Unknown</c>; empty for all.</param>
public sealed record BrowseRequest(string? Query, string? Type, int Offset, int Limit);

/// <param name="Total">Entries that match the filter; <paramref name="Entries"/> is one page of them.</param>
public sealed record BrowseResponse(ListResponse List, int Total, int Offset, int Limit, IReadOnlyList<BrowseEntry> Entries);

/// <summary>One listed entry as the list gives it (and as screening compares it): every name, weak ones marked, every
/// address, the programme and the official record.</summary>
public sealed record BrowseEntry(string ExternalId, string Type, string PrimaryName, IReadOnlyList<BrowseName> Names, IReadOnlyList<ListedAddress> Addresses, string? Programme, string? SourceUrl)
{
    public static BrowseEntry From(ListedSubject subject) => new(
        subject.ExternalId,
        subject.Kind.ToString(),
        subject.PrimaryName,
        [.. subject.Names.Select(name => new BrowseName(name.FullName, name.IsWeak))],
        subject.Addresses,
        subject.Programme,
        subject.SourceUrl);
}

/// <param name="IsWeak">The list marks this alias as low quality.</param>
public sealed record BrowseName(string Name, bool IsWeak);
