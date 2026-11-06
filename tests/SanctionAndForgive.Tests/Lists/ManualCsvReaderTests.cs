using SanctionAndForgive.Lists;

namespace SanctionAndForgive.Tests.Lists;

public class ManualCsvReaderTests
{
    [Fact]
    public void ReadsEveryColumn_QuotedFields_AndAliases()
    {
        var subjects = ManualCsvReader.Read(new StringReader(""""
            external_id,type,name,aliases,address,country,programme,listed_on,remarks
            EE-1,Entity,Northwind Shipping OÜ,"Нортвинд Шиппинг|Northwind Shipping",Tallinn,EE,National list,2026-01-15,"Fictional, ""quoted"""
            EE-2,Individual,Taras Fictionalenko,,,,,,
            """"));

        Assert.Equal(2, subjects.Count);
        var company = subjects[0];
        Assert.Equal(("EE-1", SubjectType.Entity, "Northwind Shipping OÜ"), (company.ExternalId, company.Type, company.PrimaryName));
        Assert.Equal(["Northwind Shipping OÜ", "Нортвинд Шиппинг", "Northwind Shipping"], company.Names.Select(name => name.FullName));
        Assert.Equal(NameScript.Cyrillic, company.Names[1].Script);
        Assert.Equal(new ParsedAddress("Tallinn", "EE"), Assert.Single(company.Addresses));
        Assert.Equal(new DateOnly(2026, 1, 15), company.ListedOn);
        Assert.Equal("Fictional, \"quoted\"", company.Remarks);
        Assert.Equal(SubjectType.Individual, subjects[1].Type);
        Assert.Empty(subjects[1].Addresses);
    }

    [Theory]
    [InlineData("type,name\nEntity,A", "external_id")]
    [InlineData("external_id,type,name\nX,Company,A", "type must be")]
    [InlineData("external_id,type,name,country\nX,Entity,A,Estonia", "two-letter")]
    [InlineData("external_id,type,name,listed_on\nX,Entity,A,15.01.2026", "yyyy-MM-dd")]
    [InlineData("external_id,type,name\nX,Entity,", "name is required")]
    [InlineData("external_id,type,name\nX,Entity,\"Alpha\nY,Entity,Beta", "closing quote")]
    public void InvalidInput_SaysWhatsWrong(string csv, string expected)
    {
        var exception = Assert.Throws<ListFormatException>(() => ManualCsvReader.Read(new StringReader(csv)));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }
}
