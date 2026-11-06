using SanctionAndForgive.Matching.Text;

namespace SanctionAndForgive.Matching.Names;

/// <summary>A name reduced to what decides a match (section 5.3).</summary>
/// <param name="Core">The distinctive tokens. When every token is generic, the generic ones.</param>
/// <param name="Generic">Canonical generic words (trade, group …), compared only by the generic gate.</param>
/// <param name="LegalClasses">Canonical legal forms stripped from the ends (LLC, INC …).</param>
/// <param name="Initials">Person form only: the letters of initials (<c>Т.Г.</c>), one token each.</param>
/// <param name="LegalOnly">Entity form only: every token is an unambiguous legal form (<c>Inc. Ltd</c>).</param>
internal sealed record NameForm(Token[] Core, string[] Generic, string[] LegalClasses, Token[] Initials, bool LegalOnly, bool IsPerson)
{
    public bool IsEmpty => Core.Length == 0;
}

/// <summary>Classifies tokens into the two readings of a name: as an entity (legal forms and generic words
/// stripped from the ends) and as a person (honorifics dropped, single letters kept as initials, never read as a
/// legal form: <c>Шевченко А.О.</c> keeps <c>А.О.</c>).</summary>
internal sealed class NameParser(MatcherData data)
{
    private enum Kind
    {
        Core,
        Legal,
        AmbiguousLegal,
        Generic,
        Stop,
        Honorific,
    }

    public NameForm Entity(IReadOnlyList<RawToken> tokens)
    {
        var classified = tokens
            .Select(raw => (raw.Token, Class: Classify(raw.Token, out var legalClass, out var generic), LegalClass: legalClass, Generic: generic))
            .Where(item => item.Class != Kind.Stop)
            .ToList();

        // Honorifics are dropped only from people's names; in a company name "Dr" is just a word.

        if (classified.Count > 0 && classified.All(item => item.Class == Kind.Legal))
        {
            return new NameForm([], [], [.. classified.Select(item => item.LegalClass!).Distinct().Order()], [], LegalOnly: true, IsPerson: false);
        }

        var legalClasses = new SortedSet<string>(StringComparer.Ordinal);
        bool HasCoreBesides(int index) => classified.Where((_, i) => i != index).Any(item => item.Class is Kind.Core or Kind.Honorific);
        bool HasAnyBesides(int index) => classified.Where((_, i) => i != index).Any(item => item.Class != Kind.Legal);

        // Strip legal forms from the end, then the start, until nothing changes. An unambiguous form goes unless only
        // legal forms would remain; an ambiguous one (AB, AS, АО) only if a core token remains.
        for (var changed = true; changed && classified.Count > 1;)
        {
            changed = false;
            foreach (var index in new[] { classified.Count - 1, 0 })
            {
                var item = classified[index];
                if ((item.Class == Kind.Legal && HasAnyBesides(index)) || (item.Class == Kind.AmbiguousLegal && HasCoreBesides(index)))
                {
                    legalClasses.Add(item.LegalClass!);
                    classified.RemoveAt(index);
                    changed = true;
                    break;
                }
            }
        }

        // A legal form left in the middle carries nothing either.
        foreach (var item in classified.Where(item => item.Class == Kind.Legal))
        {
            legalClasses.Add(item.LegalClass!);
        }

        classified.RemoveAll(item => item.Class == Kind.Legal);
        var core = classified.Where(item => item.Class is Kind.Core or Kind.AmbiguousLegal or Kind.Honorific).Select(item => item.Token).ToArray();
        var generic = classified.Where(item => item.Class == Kind.Generic).ToList();

        // Every token generic ("Trading Company Inc"): the generic words are the core.
        if (core.Length == 0)
        {
            return new NameForm([.. generic.Select(item => item.Token)], [], [.. legalClasses], [], LegalOnly: false, IsPerson: false);
        }

        return new NameForm(core, [.. generic.Select(item => item.Generic!).Distinct().Order()], [.. legalClasses], [], LegalOnly: false, IsPerson: false);
    }

    public NameForm Person(IReadOnlyList<RawToken> tokens)
    {
        var core = new List<Token>();
        var initials = new List<Token>();
        foreach (var raw in tokens)
        {
            if (raw.Initials is not null)
            {
                // A run of single letters is initials, never a legal form ("И.П." isn't ИП).
                initials.AddRange(raw.Initials);
                continue;
            }

            var kind = Classify(raw.Token, out _, out _);
            if (kind is Kind.Stop or Kind.Honorific or Kind.Legal)
            {
                continue;
            }

            core.Add(raw.Token);
        }

        return new NameForm([.. core], [], [], [.. initials], LegalOnly: false, IsPerson: true);
    }

    private Kind Classify(Token token, out string? legalClass, out string? generic)
    {
        legalClass = null;
        generic = null;
        if (token.IsNumber)
        {
            return Kind.Core;
        }

        foreach (var key in Keys(token))
        {
            if (data.LegalForms.TryGetValue(key, out var legal))
            {
                legalClass = legal.Class;
                return legal.Ambiguous ? Kind.AmbiguousLegal : Kind.Legal;
            }
        }

        foreach (var key in Keys(token))
        {
            if (data.Generic.TryGetValue(key, out var canonical))
            {
                generic = canonical;
                return Kind.Generic;
            }
        }

        if (Keys(token).Any(data.StopWords.Contains))
        {
            return Kind.Stop;
        }

        return Keys(token).Any(data.Honorifics.Contains) ? Kind.Honorific : Kind.Core;
    }

    /// <summary>A token's text in its own script, then its Latin readings: <c>ооо</c> and its look-alike <c>ooo</c>,
    /// <c>трейд</c> and <c>treid</c>.</summary>
    private static IEnumerable<string> Keys(Token token) => token.Forms.Prepend(token.Text);
}
