using SanctionAndForgive.Cli;

namespace SanctionAndForgive.Tests.Cli;

// The load test's shortened company names: legal forms go as whole phrases at either end, never as stray words.
public class LoadInputsTests
{
    [Theory]
    [InlineData("Public Works LLC", "Public Works")]
    [InlineData("Example Limited Liability Company", "Example")]
    [InlineData("LIMITED LIABILITY COMPANY INSPIRA INVEST", "INSPIRA INVEST")]
    [InlineData("OOO «Romashka»", "Romashka")]
    [InlineData("State Grid Co., Ltd.", "State Grid")]
    [InlineData("Public Joint Stock Company Aeroflot", "Aeroflot")]
    [InlineData("Example Public Joint Stock Company", "Example")]
    [InlineData("Example Closed Joint Stock Company", "Example")]
    [InlineData("Limited", "Limited")]
    public void LegalForms_AreStrippedAsWholePhrases(string name, string expected) =>
        Assert.Equal(expected, string.Join(' ', LegalForms.Strip(name)));
}
