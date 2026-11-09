using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Controllers;

/// <summary><c>GET /api/v1/examples</c>: sample inputs taken from the live lists for the page's "Try an example"
/// buttons. Always entities (companies), one in Cyrillic, one with an address, one with a typo. Chosen
/// deterministically, so the same lists give the same examples; computed once per loaded snapshot.</summary>
[ApiController]
[Route("api/v1/examples")]
public sealed class ExamplesController(ListCatalog catalog) : ControllerBase
{
    private static readonly ConditionalWeakTable<IReadOnlyList<LoadedList>, List<ExampleResponse>> Cache = [];

    [HttpGet]
    public IReadOnlyList<ExampleResponse> Get()
    {
        var lists = catalog.Lists;
        return Cache.GetValue(lists, _ => Choose(lists.Where(list => list.IsSearched).ToList()));
    }

    private static List<ExampleResponse> Choose(List<LoadedList> lists)
    {
        var entities = lists
            .SelectMany(list => list.Index!.Subjects.Where(subject => subject.Kind == SubjectKind.Entity).Select(subject => (List: list.Code, Subject: subject)))
            .OrderBy(pair => pair.Subject.ExternalId, StringComparer.Ordinal)
            .ToList();
        var examples = new List<ExampleResponse>();

        var latin = entities.FirstOrDefault(pair => IsLatin(pair.Subject.PrimaryName) && WordCount(pair.Subject.PrimaryName) is >= 2 and <= 5);
        if (latin.Subject is not null)
        {
            examples.Add(new ExampleResponse("A listed company", null, latin.Subject.PrimaryName, null, latin.List));
        }

        var cyrillic = entities.Select(pair => (pair.List, Name: pair.Subject.Names.Select(name => name.FullName).FirstOrDefault(name => HasCyrillic(name) && WordCount(name) <= 6)))
            .FirstOrDefault(pair => pair.Name is not null);
        if (cyrillic.Name is not null)
        {
            examples.Add(new ExampleResponse("In Cyrillic", null, cyrillic.Name, null, cyrillic.List));
        }

        var withAddress = entities.FirstOrDefault(pair => IsLatin(pair.Subject.PrimaryName) && pair.Subject.Addresses.Any(address => address.Raw.Length is > 5 and < 80) && pair.Subject != latin.Subject);
        if (withAddress.Subject is not null)
        {
            examples.Add(new ExampleResponse("With an address", null, withAddress.Subject.PrimaryName, withAddress.Subject.Addresses.First(address => address.Raw.Length is > 5 and < 80).Raw, withAddress.List));
        }

        var typo = entities.FirstOrDefault(pair => IsLatin(pair.Subject.PrimaryName) && pair.Subject.PrimaryName.Split(' ').Any(word => word.Length >= 8) && pair.Subject != latin.Subject && pair.Subject != withAddress.Subject);
        if (typo.Subject is not null)
        {
            examples.Add(new ExampleResponse("With a typo", null, WithTypo(typo.Subject.PrimaryName), null, typo.List));
        }

        return examples;
    }

    /// <summary>The name with one letter dropped from its longest word (after the first letter): a near miss.</summary>
    private static string WithTypo(string name)
    {
        var words = name.Split(' ');
        var longest = Array.IndexOf(words, words.MaxBy(word => word.Length));
        words[longest] = words[longest].Remove(words[longest].Length / 2, 1);
        return string.Join(' ', words);
    }

    private static bool IsLatin(string value) => value.All(c => c < 0x250);

    private static bool HasCyrillic(string value) => value.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    private static int WordCount(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
