using SanctionAndForgive.Matching.Addresses;
using SanctionAndForgive.Matching.Names;
using SanctionAndForgive.Matching.Text;

namespace SanctionAndForgive.Matching;

/// <summary>One list's in-memory index (section 5.5): every alias in its entity and/or person reading, the token
/// weights (inverse document frequency with a floor), and the lookups that find candidates: exact fold keys, a
/// trigram index over the vocabulary for typos, spelling pairs and cognates. Immutable: a changed list gets a new
/// index, swapped in whole. Tokens are interned, so a common name part is one object.</summary>
public sealed class ListIndex
{
    private readonly MatcherData data;
    private readonly ListedSubject[] subjects;
    private readonly AliasEntry[] aliases;
    private readonly AddressForm[][] addresses;
    private readonly Dictionary<string, int> documentFrequency;
    private readonly Dictionary<string, int[]> aliasesByFold;
    private readonly Dictionary<string, string[]> foldsByTrigram;
    private readonly Dictionary<string, int[]> subjectsByAddressKey;

    private ListIndex(
        MatcherData data,
        ListedSubject[] subjects,
        AliasEntry[] aliases,
        AddressForm[][] addresses,
        Dictionary<string, int> documentFrequency,
        Dictionary<string, int[]> aliasesByFold,
        Dictionary<string, string[]> foldsByTrigram,
        Dictionary<string, int[]> subjectsByAddressKey)
    {
        this.data = data;
        this.subjects = subjects;
        this.aliases = aliases;
        this.addresses = addresses;
        this.documentFrequency = documentFrequency;
        this.aliasesByFold = aliasesByFold;
        this.foldsByTrigram = foldsByTrigram;
        this.subjectsByAddressKey = subjectsByAddressKey;
    }

    public IReadOnlyList<ListedSubject> Subjects => subjects;

    public int AliasCount => aliases.Length;

    public static ListIndex Build(IEnumerable<ListedSubject> listedSubjects)
    {
        var data = MatcherData.Default;
        var cache = new Dictionary<string, Token>(StringComparer.Ordinal);
        var tokenizer = new Tokenizer(data, cache);
        var parser = new NameParser(data);
        var addressParser = new AddressParser(data, tokenizer);

        var subjects = listedSubjects.ToArray();
        var aliases = new List<AliasEntry>();
        var addresses = new AddressForm[subjects.Length][];
        for (var s = 0; s < subjects.Length; s++)
        {
            var subject = subjects[s];
            foreach (var name in subject.Names.Append(new ListedName(subject.PrimaryName, IsWeak: false)).DistinctBy(name => name.FullName))
            {
                var tokens = tokenizer.Tokenize(name.FullName, joinInitials: true);
                if (subject.Kind != SubjectKind.Individual)
                {
                    AddAlias(aliases, new AliasEntry(s, name.FullName, name.IsWeak, parser.Entity(tokens)));
                }

                if (subject.Kind != SubjectKind.Entity)
                {
                    AddAlias(aliases, new AliasEntry(s, name.FullName, name.IsWeak, parser.Person(tokens)));
                }
            }

            addresses[s] = [.. subject.Addresses.Select(address => addressParser.Parse(address.Raw, address.CountryCode))];
        }

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        var aliasesByFold = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var a = 0; a < aliases.Count; a++)
        {
            foreach (var token in aliases[a].Form.Core.Distinct())
            {
                documentFrequency[token.Key] = documentFrequency.GetValueOrDefault(token.Key) + 1;
                foreach (var key in LookupKeys(token))
                {
                    if (!aliasesByFold.TryGetValue(key, out var list))
                    {
                        aliasesByFold[key] = list = [];
                    }

                    if (list.Count == 0 || list[^1] != a)
                    {
                        list.Add(a);
                    }
                }
            }
        }

        var foldsByTrigram = aliasesByFold.Keys
            .Where(fold => fold.Length >= 4)
            .SelectMany(fold => Trigrams(fold).Distinct().Select(trigram => (trigram, fold)))
            .GroupBy(pair => pair.trigram, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.fold).ToArray(), StringComparer.Ordinal);

        var subjectsByAddressKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var s = 0; s < subjects.Length; s++)
        {
            foreach (var key in addresses[s].SelectMany(AddressKeys).Distinct())
            {
                if (!subjectsByAddressKey.TryGetValue(key, out var list))
                {
                    subjectsByAddressKey[key] = list = [];
                }

                list.Add(s);
            }
        }

        return new ListIndex(
            data,
            subjects,
            [.. aliases],
            addresses,
            documentFrequency,
            aliasesByFold.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),
            foldsByTrigram,
            subjectsByAddressKey.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal));
    }

    /// <summary>Every subject that matches a name entry, best first: its best alias, with the entered address
    /// compared against its listed addresses.</summary>
    internal List<SubjectMatch> SearchName(PreparedName query, MatchingOptions options)
    {
        var comparer = new TokenComparer(data);
        var scorer = new NameScorer(comparer, options);
        var addressComparer = new AddressComparer(comparer, data);
        var best = new Dictionary<int, (NameScore Score, AliasEntry Alias)>();

        foreach (var aliasId in Candidates(query, options))
        {
            var alias = aliases[aliasId];
            var form = alias.Form.IsPerson ? query.Person : query.Entity;
            var score = scorer.Score(form, alias.Form, alias.IsWeak, token => Weight(token, options));
            if (!best.TryGetValue(alias.Subject, out var current) || score.Percent > current.Score.Percent)
            {
                best[alias.Subject] = (score, alias);
            }
        }

        var matches = new List<SubjectMatch>(best.Count);
        foreach (var (subjectIndex, (score, alias)) in best)
        {
            var address = addressComparer.Compare(query.Address, addresses[subjectIndex]);
            var percent = score.Percent;
            if (address.State == AddressState.Conflicting)
            {
                percent = Math.Min(percent, options.ConflictingAddressCap);
            }
            else if (address.State == AddressState.Corroborated)
            {
                percent = Math.Min(percent + options.CorroboratedBonus, Bands.BandMax(percent, options));
            }

            matches.Add(new SubjectMatch(subjects[subjectIndex], percent, Bands.ToOutcome(percent, options), alias.Name, [.. score.Explanation, address.Explanation], address.State));
        }

        return [.. matches.OrderByDescending(match => match.Percent).ThenBy(match => match.Subject.PrimaryName, StringComparer.Ordinal)];
    }

    /// <summary>Address-only screening: listed subjects whose address shares at least 3 parts (a number and a word)
    /// with the entered one, found through the address words and places.</summary>
    internal List<SubjectMatch> SearchAddress(AddressForm entered, MatchingOptions options)
    {
        var comparer = new AddressComparer(new TokenComparer(data), data);
        var votes = new Dictionary<int, int>();
        foreach (var key in AddressKeys(entered).Distinct())
        {
            foreach (var subject in subjectsByAddressKey.GetValueOrDefault(key) ?? [])
            {
                votes[subject] = votes.GetValueOrDefault(subject) + 1;
            }
        }

        var matches = new List<SubjectMatch>();
        foreach (var subject in votes.OrderByDescending(vote => vote.Value).Take(options.CandidateLimit).Select(vote => vote.Key))
        {
            var bestAddress = addresses[subject]
                .Select(address => (Address: address, Result: comparer.CompareAddressOnly(entered, address)))
                .MaxBy(pair => (pair.Result.Matches, pair.Result.Aligned));
            var percent = bestAddress.Result.Matches ? options.AddressOnlyPercent : Math.Min(options.NoMatchMaxPercent, bestAddress.Result.Aligned * 15);
            matches.Add(new SubjectMatch(subjects[subject], percent, Bands.ToOutcome(percent, options), bestAddress.Address.Raw, [bestAddress.Result.Explanation], AddressState.NotCompared));
        }

        return [.. matches.OrderByDescending(match => match.Percent).ThenBy(match => match.Subject.PrimaryName, StringComparer.Ordinal)];
    }

    /// <summary>A token's weight: inverse document frequency over this list's aliases, never below the floor.</summary>
    private double Weight(Token token, MatchingOptions options) =>
        Math.Max(options.MinTokenWeight, Math.Log((double)Math.Max(aliases.Length, 1) / Math.Max(documentFrequency.GetValueOrDefault(token.Key), 1)));

    /// <summary>Aliases sharing a fold key with an entry token, a spelling variant or cognate of one, or a fold key
    /// one or two edits away (found through shared trigrams, then checked). Most votes first, up to the limit.</summary>
    private IEnumerable<int> Candidates(PreparedName query, MatchingOptions options)
    {
        var comparer = new TokenComparer(data);
        var votes = new Dictionary<int, int>();
        void Vote(string key, int weight)
        {
            foreach (var alias in aliasesByFold.GetValueOrDefault(key) ?? [])
            {
                votes[alias] = votes.GetValueOrDefault(alias) + weight;
            }
        }

        foreach (var token in query.Entity.Core.Concat(query.Person.Core).Distinct())
        {
            foreach (var key in LookupKeys(token))
            {
                Vote(key, 3);
            }

            foreach (var group in comparer.CognateGroups(token))
            {
                foreach (var fold in comparer.CognateFoldsOf(group))
                {
                    Vote(fold, 2);
                }
            }

            foreach (var spelling in token.Forms.SelectMany(form => data.SpellingMembers(form)))
            {
                Vote(FoldKey.Of(spelling), 2);
            }

            foreach (var fold in token.Folds.Where(fold => fold.Length >= 4))
            {
                var near = Trigrams(fold).Distinct()
                    .SelectMany(trigram => foldsByTrigram.GetValueOrDefault(trigram) ?? [])
                    .Distinct()
                    .Where(other => other != fold && EditDistance.DamerauLevenshtein(fold, other, 3) <= 2);
                foreach (var other in near)
                {
                    Vote(other, 1);
                }
            }
        }

        return votes.OrderByDescending(vote => vote.Value).Take(options.CandidateLimit).Select(vote => vote.Key);
    }

    private static void AddAlias(List<AliasEntry> aliases, AliasEntry alias)
    {
        if (!alias.Form.IsEmpty)
        {
            aliases.Add(alias);
        }
    }

    /// <summary>A token's keys in the fold lookup: its fold keys, or its text if it has no Latin reading.</summary>
    private static IEnumerable<string> LookupKeys(Token token) => token.Folds.Length > 0 ? token.Folds : [token.Text];

    private static IEnumerable<string> AddressKeys(AddressForm address) =>
        address.Words.Where(word => word.Length >= 3).SelectMany(LookupKeys).Concat(address.Places.Select(place => $"#place:{place}"));

    private static IEnumerable<string> Trigrams(string value)
    {
        for (var i = 0; i + 3 <= value.Length; i++)
        {
            yield return value.Substring(i, 3);
        }
    }

    private sealed record AliasEntry(int Subject, string Name, bool IsWeak, NameForm Form);
}

/// <summary>An entry parsed once per request, then searched in every list.</summary>
internal sealed record PreparedName(NameForm Entity, NameForm Person, AddressForm? Address);
