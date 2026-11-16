using SanctionAndForgive.Lists;

namespace SanctionAndForgive.Tests.Lists;

// The script measurement behind probe --scripts (docs/sources.md, "Scripts of the listed names").
public class ScriptsTests
{
    [Theory]
    [InlineData("Ivan Petrov", "Latin")]
    [InlineData("Иван Петров", "Cyrillic")]
    [InlineData("ООО Volga Trade", "Cyrillic+Latin")]
    [InlineData("محمد عبد الله", "Arabic")]
    [InlineData("中兴通讯", "Han")]
    [InlineData("ソコロフ・イワン", "Katakana")]
    [InlineData("中国ガス", "Han+Katakana")]
    [InlineData("김정은", "Hangul")]
    [InlineData("Οργάνωση Abu Nidal", "Greek+Latin")]
    [InlineData("12-34", "None")]
    public void ANamesScripts_AreItsLettersScripts_InAlphabeticalOrder(string name, string expected) =>
        Assert.Equal(expected, Scripts.InName(name));

    [Theory]
    [InlineData("Аляксандр Лукашэнка", "Russian or unmarked")]
    [InlineData("Аляксандр Рыгоравіч Лукашэнка", "Ukrainian, Belarusian or Kazakh (і)")]
    [InlineData("Мікалай Ўсцінаў", "Belarusian (ў)")]
    [InlineData("Ґалина Їжак", "Ukrainian (ґ є ї)")]
    [InlineData("Нұрсұлтан Назарбаев", "Kazakh (ә ғ қ ң ұ һ)")]
    [InlineData("Ђорђе Јовић", "Serbian (ћ ђ)")]
    public void ACyrillicName_PointsToTheAlphabetOfItsLetters(string name, string expected) =>
        Assert.Equal(expected, Scripts.CyrillicAlphabet(name));
}
