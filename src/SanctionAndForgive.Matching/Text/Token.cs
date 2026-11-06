namespace SanctionAndForgive.Matching.Text;

/// <summary>One normalised word with every Latin reading of it. Tokens are interned per index (one object per
/// distinct text), so a common name part costs one object however many aliases contain it.</summary>
/// <param name="Text">Normalised (NFKC, case-folded, quotes and apostrophes gone), in its own script.</param>
/// <param name="Forms">Latin readings: for a Latin token, itself without diacritics (and ü as both u and ue); for a
/// Cyrillic token, one per transliteration table, plus a look-alike reading. At most 8. Empty for other scripts.</param>
/// <param name="Folds">The fold key of each form: comparison only, so romanisation schemes meet.</param>
internal sealed record Token(string Text, string[] Forms, string[] Folds, bool IsCyrillic, bool IsNumber)
{
    /// <summary>Letters (or digits) in the token.</summary>
    public int Length => Text.Length;

    /// <summary>The key for weights and postings: the first fold key, or the text itself.</summary>
    public string Key => Folds.Length > 0 ? Folds[0] : Text;

    public override string ToString() => Text;
}

/// <summary>A token as it came out of the tokenizer, before classification: the token, and for a run of single
/// letters (<c>Т.Г.</c>, <c>A B</c>) the letters as separate tokens, for initials.</summary>
internal sealed record RawToken(Token Token, Token[]? Initials);
