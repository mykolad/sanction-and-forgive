using SanctionAndForgive.Matching;
using SanctionAndForgive.Matching.Text;

namespace SanctionAndForgive.Tests.Matching;

// The building blocks the golden tests rest on: normalisation, transliteration and fold keys (sections 5.1, 5.2).
public class TextTests
{
    private static readonly MatcherData Data = MatcherData.Default;

    [Theory]
    [InlineData("Grigoriy", "Hryhorii")]
    [InlineData("Khmelnitsky", "Khmelnytskyi")]
    [InlineData("Kovalev", "Kovaleff")]
    [InlineData("Yuriy", "Iurii")]
    [InlineData("Maryana", "Mariana")]
    public void FoldKey_RomanisationSchemesMeet(string a, string b)
    {
        Assert.Equal(FoldKey.Of(a.ToLowerInvariant()), FoldKey.Of(b.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("Юрий", new[] { "yuriy", "yuryi" })]
    [InlineData("Гончар", new[] { "gonchar", "honchar" })]
    [InlineData("Хмельницький", new[] { "khmelnitskiy", "khmelnytskyi" })]
    [InlineData("Мар'яна", new[] { "maryana", "mariana" })]
    [InlineData("Ковалёв", new[] { "kovalev", "kovaliov" })]
    public void Cyrillic_GetsARussianAndAUkrainianReading(string word, string[] expected)
    {
        var token = Data.Tokens.Create(Normalizer.Words(word).Single());

        Assert.Equal(expected, token.Forms.Take(2));
    }

    [Theory]
    [InlineData("АВ", "ab")]
    [InlineData("OOО", "ooo")]
    public void LookAlikeLetters_GetAVisualReading(string word, string expected)
    {
        Assert.Contains(expected, Data.Tokens.Create(Normalizer.Words(word).Single()).Forms);
    }

    [Theory]
    [InlineData("Мар'яна Коваль", new[] { "маряна", "коваль" })]
    [InlineData("Мар’яна", new[] { "маряна" })]
    [InlineData("ООО «Ромашка»", new[] { "ооо", "ромашка" })]
    [InlineData("Volga & Sons", new[] { "volga", "and", "sons" })]
    public void Words_DeleteApostrophesInsideWords_SplitOnPunctuation(string value, string[] expected)
    {
        Assert.Equal(expected, Normalizer.Words(value));
    }

    [Fact]
    public void Diacritics_StrippedFromLatinOnly_UmlautAsBothForms()
    {
        var token = Data.Tokens.Create("müller");

        Assert.Equal(["muller", "mueller"], token.Forms);
        Assert.Equal("юрий", Data.Tokens.Create("юрий").Text);
    }
}
