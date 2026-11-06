using System.Security.Cryptography;
using System.Text;
using SanctionAndForgive.Matching.Addresses;
using SanctionAndForgive.Matching.Names;
using SanctionAndForgive.Matching.Text;

namespace SanctionAndForgive.Matching;

/// <summary>What the user entered. Every field is optional; at least one is required.</summary>
public sealed record ScreeningRequest(string? ReceiverName, string? CompanyName, string? Address);

/// <summary>A list as the screener sees it. <paramref name="Index"/> is null when the list may not be searched:
/// it failed to load, it's older than its allowed age, or its index isn't ready. Its cells then say Not checked.</summary>
public sealed record ScreenedList(string Code, string Name, DateTimeOffset? AsOf, ListIndex? Index);

public enum EntryKind
{
    Receiver,
    Company,
    Address,
}

/// <summary>The overall outcome, worst first: a Match or Review anywhere wins; nothing found but a list unchecked
/// is Incomplete, never NoMatch.</summary>
public enum OverallOutcome
{
    Match,
    Review,
    Incomplete,
    NoMatch,
}

/// <summary>One list's answer for one entry. Not checked: no percentage and no outcome, never 0%.</summary>
/// <param name="Best">The best subject on the list, whatever its percentage (the page shows it from 20%).</param>
/// <param name="Others">Up to four more subjects at Review or above.</param>
public sealed record Cell(string List, bool Checked, int? Percent, Outcome? Outcome, SubjectMatch? Best, IReadOnlyList<SubjectMatch> Others);

/// <param name="Compared">What was compared, in words: "Name only: no address entered".</param>
public sealed record Entry(EntryKind Kind, string Input, string Compared, IReadOnlyList<Cell> Cells);

public sealed record ScreeningResult(OverallOutcome Outcome, string MatcherVersion, IReadOnlyList<Entry> Entries);

/// <summary>Screens each entry against each list separately (section 5.7). Deterministic: the same input, lists and
/// matcher version always give the same result. Nothing here stores or logs the input.</summary>
public sealed class Screener
{
    private readonly MatchingOptions options;
    private readonly MatcherData data = MatcherData.Default;

    public Screener(MatchingOptions options)
    {
        this.options = options;
        MatcherVersion = ComputeVersion(data, options);
    }

    /// <summary>A hash of the dictionaries and the options: changes whenever matching could change.</summary>
    public string MatcherVersion { get; }

    /// <summary>Throws <see cref="ArgumentException"/> if every field is empty.</summary>
    public ScreeningResult Screen(ScreeningRequest request, IReadOnlyList<ScreenedList> lists)
    {
        var receiver = Clean(request.ReceiverName);
        var company = Clean(request.CompanyName);
        var address = Clean(request.Address);
        if (receiver is null && company is null && address is null)
        {
            throw new ArgumentException("Enter a receiver, a company or an address.", nameof(request));
        }

        var tokenizer = new Tokenizer(data, cache: null);
        var parser = new NameParser(data);
        var addressForm = address is null ? null : new AddressParser(data, tokenizer).Parse(address, sourceCountryCode: null);

        var entries = new List<Entry>();
        foreach (var (kind, name) in new[] { (EntryKind.Receiver, receiver), (EntryKind.Company, company) })
        {
            if (name is null)
            {
                continue;
            }

            var tokens = tokenizer.Tokenize(name, joinInitials: true);
            var prepared = new PreparedName(parser.Entity(tokens), parser.Person(tokens), addressForm);
            var compared = addressForm is null ? "Name only: no address entered" : "Name, with the address where the list has one";
            entries.Add(new Entry(kind, name, compared, [.. lists.Select(list => ToCell(list, index => index.SearchName(prepared, options)))]));
        }

        if (addressForm is not null)
        {
            entries.Add(new Entry(EntryKind.Address, address!, "Address only", [.. lists.Select(list => ToCell(list, index => index.SearchAddress(addressForm, options)))]));
        }

        return new ScreeningResult(Overall(entries), MatcherVersion, entries);
    }

    private Cell ToCell(ScreenedList list, Func<ListIndex, List<SubjectMatch>> search)
    {
        if (list.Index is null)
        {
            return new Cell(list.Code, Checked: false, Percent: null, Outcome: null, Best: null, Others: []);
        }

        var matches = search(list.Index);
        var best = matches.FirstOrDefault();
        var others = matches.Skip(1).Where(match => match.Outcome != Outcome.NoMatch).Take(options.HitsPerCell - 1).ToList();
        var percent = best?.Percent ?? 0;
        return new Cell(list.Code, Checked: true, percent, Bands.ToOutcome(percent, options), best, others);
    }

    private static OverallOutcome Overall(List<Entry> entries)
    {
        var cells = entries.SelectMany(entry => entry.Cells).ToList();
        return cells.Any(cell => cell.Outcome == Outcome.Match) ? OverallOutcome.Match
            : cells.Any(cell => cell.Outcome == Outcome.Review) ? OverallOutcome.Review
            : cells.Any(cell => !cell.Checked) ? OverallOutcome.Incomplete
            : OverallOutcome.NoMatch;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ComputeVersion(MatcherData data, MatchingOptions options)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(data.Hash + "|" + options));
        return Convert.ToHexStringLower(bytes)[..12];
    }
}
