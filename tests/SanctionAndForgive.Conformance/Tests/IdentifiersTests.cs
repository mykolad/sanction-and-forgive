using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SanctionAndForgive.Conformance.Harness;

namespace SanctionAndForgive.Conformance.Tests;

// Identifiers and dates of birth (#68; version 2, the Rust reference first). The fictional lists carry none, so a
// variant of the OFAC consolidated file gives a company a tax number and a person a passport and a date of birth;
// the base files are imported again afterwards.
[Collection("3 Changes")]
public sealed partial class IdentifiersTests
{
    private const string Ofac = "ofac-cons";

    [GeneratedRegex("<entity id=\"\\d+\">\\s*<generalInfo>\\s*<entityType>(?<type>Entity|Individual)</entityType>.*?</entity>", RegexOptions.Singleline)]
    private static partial Regex EntityElement();

    [GeneratedRegex("<formattedFullName>(?<name>[^<]*)</formattedFullName>")]
    private static partial Regex FullName();

    [GeneratedRegex("<formattedFirstName>(?<first>[^<]*)</formattedFirstName>\\s*<formattedLastName>(?<last>[^<]*)</formattedLastName>")]
    private static partial Regex PersonName();

    [SinceVersionFact(2)]
    public async Task TheStatus_AdvertisesIdentifiers()
    {
        await Suite.ReadyAsync();

        var status = await Suite.GetAsync("api/v1/status");

        Assert.Contains("identifiers", status.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
    }

    [SinceVersionFact(2)]
    public async Task AnIdentifier_IsRefused_WhenTooShort_OrWithoutItsName_AndADateOfBirth_OnlyForAPerson()
    {
        await Suite.ReadyAsync();

        foreach (var body in new object[]
        {
            new { reference = "ids-1", companyName = "Northwind Trading", companyIdentifiers = new[] { "1027" } },
            new { reference = "ids-2", companyIdentifiers = new[] { "1027700132195" } },
            new { reference = "ids-3", receiverName = "Ivan Sokolov", receiverDateOfBirth = "12/03/1960" },
            new { reference = "ids-4", parties = new object[] { new { role = "shipper", name = "Northwind Trading", kind = "company", dateOfBirth = "1960" } } },
            new { reference = "ids-5", companyName = "Northwind Trading", companyIdentifiers = Enumerable.Repeat("1027700132195", 6).ToArray() },
        })
        {
            using var response = await Suite.PostAsync("api/v1/screenings", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("1027", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    /// <summary>The owner's rules (#68): an equal identifier makes a Match whatever the name; a different one never
    /// lowers a result; a date of birth is said, never a Match on its own.</summary>
    [SinceVersionFact(2)]
    public async Task AnEqualIdentifier_IsAMatch_ADifferentOneNeverLowers_AndADateOfBirthIsOnlySaid()
    {
        await Suite.ReadyAsync();
        var xml = await File.ReadAllTextAsync(Suite.FeedPath(Ofac, "base"));
        var entities = EntityElement().Matches(xml);
        var company = entities.First(m => m.Groups["type"].Value == "Entity");
        var person = entities.First(m => m.Groups["type"].Value == "Individual" && PersonName().IsMatch(m.Value));
        var companyName = FullName().Match(company.Value).Groups["name"].Value;
        var personMatch = PersonName().Match(person.Value);
        var personName = $"{personMatch.Groups["first"].Value} {personMatch.Groups["last"].Value}";

        var variant = xml
            .Replace(company.Value, company.Value.Replace("</entity>", """
                <identityDocuments><identityDocument id="900001"><type refId="1">Tax ID No.</type><documentNumber>OGRN 1027700132195</documentNumber><issuingCountry>Russia</issuingCountry></identityDocument></identityDocuments>
                </entity>
                """, StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace(person.Value, person.Value.Replace("</entity>", """
                <features><feature id="900002"><type featureTypeId="8">Birthdate</type><value>12 Mar 1960</value><valueDate><fromDateBegin>1960-03-12</fromDateBegin><toDateEnd>1960-03-12</toDateEnd></valueDate></feature></features>
                <identityDocuments><identityDocument id="900003"><type refId="2">Passport</type><documentNumber>P04581926</documentNumber></identityDocument></identityDocuments>
                </entity>
                """, StringComparison.Ordinal), StringComparison.Ordinal);
        var path = Suite.FeedPath(Ofac, "identifiers");
        await File.WriteAllTextAsync(path, variant);
        try
        {
            var import = await Suite.CliAsync($"import --source {Ofac} --file {Suite.Quote(path)}");
            Assert.True(import.ExitCode == 0, import.Output);
            await Suite.WaitForListsAsync(lists => lists[Ofac].GetProperty("version").GetInt32() > 0 && lists[Ofac].GetProperty("state").GetString() == "Current");
            JsonElement renamed = default;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                renamed = await Suite.ScreenAsync(new { reference = "ids-renamed", companyName = "Quillfeather Biscuit Atelier", companyIdentifiers = new[] { "1027700132195" } });
                if (Suite.Cell(renamed, Ofac)?.GetProperty("outcome").GetString() == "Match")
                {
                    break;
                }

                await Task.Delay(500);
            }

            // Equal: a Match on the list, the identifier its own line, whatever the name.
            var cell = Suite.Cell(renamed, Ofac)!.Value;
            Assert.Equal(("Match", 100), (cell.GetProperty("outcome").GetString(), cell.GetProperty("percent").GetInt32()));
            var best = cell.GetProperty("bestCandidate");
            Assert.Equal(companyName, best.GetProperty("listedName").GetString());
            Assert.StartsWith("Identifier: equals the listed Tax ID No. OGRN 1027700132195", best.GetProperty("alignment")[0].GetString(), StringComparison.Ordinal);
            Assert.Equal("OGRN 1027700132195", best.GetProperty("listedIdentifiers")[0].GetProperty("value").GetString());

            // Different, with the name matching: the same outcome and percentage as without it, and a line.
            var plain = Suite.Cell(await Suite.ScreenAsync(new { reference = "ids-plain", receiverName = personName }), Ofac)!.Value;
            var different = await Suite.ScreenAsync(new { reference = "ids-different", receiverName = personName, receiverIdentifiers = new[] { "X9999999" }, receiverDateOfBirth = "1961" });
            var differentCell = Suite.Cell(different, Ofac)!.Value;
            Assert.Equal(
                (plain.GetProperty("outcome").GetString(), plain.GetProperty("percent").GetInt32()),
                (differentCell.GetProperty("outcome").GetString(), differentCell.GetProperty("percent").GetInt32()));
            var lines = differentCell.GetProperty("bestCandidate").GetProperty("alignment").EnumerateArray().Select(line => line.GetString()).ToList();
            Assert.Contains(lines, line => line!.StartsWith("Identifiers: the entered ones aren't among the listed (Passport P04581926)", StringComparison.Ordinal));
            Assert.Contains("Date of birth: differs from the listed (1960-03-12)", lines);

            // A party's identifiers too, through the stream.
            var stream = await Suite.StreamAsync([new { reference = "ids-stream", parties = new object[] { new { role = "consignee", name = "Somebody Else", kind = "person", identifiers = new[] { "p 0458-1926" } } } }]);
            var result = stream.Single(line => line.GetProperty("type").GetString() == "result");
            Assert.Equal("Match", result.GetProperty("outcome").GetString());
        }
        finally
        {
            File.Delete(path);
            await Suite.RestoreBaseAsync();
        }
    }
}
