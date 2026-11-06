using System.Text.Json;
using System.Text.Json.Serialization;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>US Consolidated Screening List (trade.gov JSON, <c>{"results": [...]}</c>). Only Commerce (BIS) and State
/// records are kept: the Treasury ones repeat OFAC's lists, which are sources of their own. Vessels and aircraft are
/// left out. The records have no type, so they are <see cref="SubjectType.Unknown"/>.</summary>
/// <remarks>Reads the file's bytes once and deserialises one record at a time: a 35 MB file, about 6,000 kept.</remarks>
public sealed class UsExportParser : IListParser
{
    private static readonly string[] KeptAgencies = ["Bureau of Industry and Security", "State Department"];

    public string SourceCode => "us-export";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ReadRecords(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), cancellationToken)
            .Where(record => KeptAgencies.Any(agency => record.Source?.EndsWith(agency, StringComparison.Ordinal) == true))
            .Where(record => record.Type is not ("Vessel" or "Aircraft"))
            .Select(ToSubject)
            .OfType<ParsedSubject>()
            .ToList();
    }

    private static List<UsRecord> ReadRecords(ReadOnlySpan<byte> json, CancellationToken cancellationToken)
    {
        // A truncated or reshaped file must fail, not pass as a shorter list: every array entry must be an object,
        // and the root object must close at the end of the input.
        var reader = new Utf8JsonReader(json);
        List<UsRecord>? records = null;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new ListFormatException("Expected a JSON object.");
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isResults = reader.ValueTextEquals("results"u8);
            reader.Read();
            if (!isResults)
            {
                reader.Skip();
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new ListFormatException("Expected \"results\" to be an array.");
            }

            records = [];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new ListFormatException($"Expected an object in \"results\", found {reader.TokenType}.");
                }

                records.Add(JsonSerializer.Deserialize(ref reader, UsJsonContext.Default.UsRecord)!);
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
        {
            throw new ListFormatException("The JSON document doesn't end after its root object.");
        }

        return records ?? throw new ListFormatException("No \"results\" array.");
    }

    private static ParsedSubject? ToSubject(UsRecord record)
    {
        var id = Text.Clean(record.Id) ?? throw new ListFormatException("A US export record has no id.");
        var type = record.Type switch
        {
            "Individual" => SubjectType.Individual,
            "Entity" => SubjectType.Entity,
            _ => SubjectType.Unknown,
        };
        var builder = new SubjectBuilder(id, type)
        {
            Programmes = Text.Join(", ", [record.Source, .. record.Programs ?? []]),
            ListedOn = Text.ParseDate(record.StartDate, "yyyy-MM-dd"),
            Remarks = record.Remarks,
            SourceUrl = Text.Clean(record.SourceListUrl) ?? Text.Clean(record.SourceInformationUrl),
        };

        builder.AddName(record.Name, isWeak: false, isPrimary: true);
        foreach (var alias in record.AltNames ?? [])
        {
            builder.AddName(alias, isWeak: false, isPrimary: false);
        }

        foreach (var address in record.Addresses ?? [])
        {
            builder.AddAddress(Text.Join(", ", address.Address, address.City, address.State, address.PostalCode, address.Country), address.Country);
        }

        return builder.Build();
    }
}

internal sealed record UsRecord(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("alt_names")] List<string>? AltNames,
    [property: JsonPropertyName("programs")] List<string>? Programs,
    [property: JsonPropertyName("addresses")] List<UsAddress>? Addresses,
    [property: JsonPropertyName("start_date")] string? StartDate,
    [property: JsonPropertyName("remarks")] string? Remarks,
    [property: JsonPropertyName("source_list_url")] string? SourceListUrl,
    [property: JsonPropertyName("source_information_url")] string? SourceInformationUrl);

internal sealed record UsAddress(
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("postal_code")] string? PostalCode,
    [property: JsonPropertyName("country")] string? Country);

[JsonSerializable(typeof(UsRecord))]
internal sealed partial class UsJsonContext : JsonSerializerContext;
