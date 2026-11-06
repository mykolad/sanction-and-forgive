namespace SanctionAndForgive.Matching.Text;

/// <summary>Section 5.1 in order: normalise, replace multi-word legal forms with their short form, join runs of
/// single letters (<c>A.B.</c>, <c>A B</c>, <c>L.L.C.</c> → <c>ab</c>, <c>ab</c>, <c>llc</c>) keeping the letters for
/// initials, then transliterate. Tokens come from <paramref name="cache"/> when it has them (interning).</summary>
internal sealed partial class Tokenizer(MatcherData data, Dictionary<string, Token>? cache)
{
    public List<RawToken> Tokenize(string value, bool joinInitials)
    {
        var words = ReplaceLegalPhrases(Normalizer.Words(HyphenatedHonorific().Replace(value, " ")));
        var tokens = new List<RawToken>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            if (!joinInitials || !IsSingleLetter(words[i]))
            {
                tokens.Add(new RawToken(Get(words[i]), Initials: null));
                continue;
            }

            var run = new List<string> { words[i] };
            while (i + 1 < words.Count && IsSingleLetter(words[i + 1]))
            {
                run.Add(words[++i]);
            }

            tokens.Add(new RawToken(Get(string.Concat(run)), [.. run.Select(Get)]));
        }

        return tokens;
    }

    public Token Get(string text)
    {
        if (cache is null)
        {
            return data.Tokens.Create(text);
        }

        if (!cache.TryGetValue(text, out var token))
        {
            token = data.Tokens.Create(text);
            cache[text] = token;
        }

        return token;
    }

    private static bool Matches(List<string> words, int start, string[] phrase)
    {
        if (start + phrase.Length > words.Count)
        {
            return false;
        }

        for (var j = 0; j < phrase.Length; j++)
        {
            if (!string.Equals(words[start + j], phrase[j], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSingleLetter(string word) => word.Length == 1 && char.IsLetter(word[0]);

    private List<string> ReplaceLegalPhrases(List<string> words)
    {
        if (words.Count < 2)
        {
            return words;
        }

        var result = new List<string>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            var phrase = data.LegalPhrases.TryGetValue(words[i], out var candidates)
                ? candidates.FirstOrDefault(candidate => Matches(words, i, candidate.Words))
                : default;
            if (phrase.Words is null)
            {
                result.Add(words[i]);
            }
            else
            {
                result.Add(phrase.Short);
                i += phrase.Words.Length - 1;
            }
        }

        return result;
    }

    /// <summary>Russian honorific abbreviations written with a hyphen (<c>г-н</c>, <c>г-жа</c>): removed before
    /// normalising, which would split <c>г-н</c> into the initials <c>Г.Н.</c>. Genuine initials have no hyphen.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\p{L})г-(н|жа)(?!\p{L})\.?", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex HyphenatedHonorific();
}
