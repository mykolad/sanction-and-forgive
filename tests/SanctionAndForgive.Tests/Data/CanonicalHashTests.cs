using SanctionAndForgive.Data;

namespace SanctionAndForgive.Tests.Data;

public class CanonicalHashTests
{
    [Fact]
    public void SameRecords_InAnyOrder_HashTheSame()
    {
        Assert.Equal(ImportStore.CanonicalHash([Company("A", "Alpha"), Company("B", "Beta")]), ImportStore.CanonicalHash([Company("B", "Beta"), Company("A", "Alpha")]));
    }

    [Fact]
    public void ChangedContent_HashesDifferently()
    {
        Assert.NotEqual(ImportStore.CanonicalHash([Company("A", "Alpha")]), ImportStore.CanonicalHash([Company("A", "Alpha Ltd")]));
    }

    [Fact]
    public void RepeatedExternalId_IsMergedIntoOneRecord()
    {
        var first = Company("A", "Alpha");
        var second = Company("A", "Alfa");

        var merged = Assert.Single(ImportStore.Deduplicate([first, second]));

        Assert.Equal(["Alpha", "Alfa"], merged.Names.Select(name => name.FullName));
    }

    private static Subject Company(string id, string name)
    {
        var subject = new Subject { ExternalId = id, PrimaryName = name, Type = SubjectType.Entity };
        subject.Names.Add(new SubjectName { FullName = name });
        return subject;
    }
}
