using System.Text.Json;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>France's national asset-freeze register (DG Trésor, <c>Registre national des gels</c>): JSON,
/// <c>{"Publications": {"DatePublication": …, "PublicationDetail": [...]}}</c>. Each record has an <c>IdRegistre</c>,
/// a <c>Nature</c> (<c>Personne physique</c>, <c>Personne morale</c>, <c>Navire</c>: vessels are left out), a
/// <c>Nom</c>, and typed fields (<c>RegistreDetail</c>: <c>TypeChamp</c> and its <c>Valeur</c> list). A person's name
/// is <c>PRENOM</c> then <c>Nom</c>; aliases marked <c>alias faible</c> are weak. Addresses (<c>ADRESSE_PM</c>,
/// <c>ADRESSE_PP</c>) name their country in French, which the matcher reads (countries.json). Each record's page on the
/// register is its official record.</summary>
/// <remarks>About 15 MB and 8,200 records: the bytes are read once and walked with one reader.</remarks>
public sealed class FranceParser : IListParser
{
    public const string RecordUrl = "https://gels-avoirs.dgtresor.gouv.fr/Gels/RegistreDetail?idRegistre=";

    public string SourceCode => "fr-tresor";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var document = ReadDocument(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        var subjects = new List<ParsedSubject>();
        foreach (var record in Records(document.RootElement))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ToSubject(record) is { } subject)
            {
                subjects.Add(subject);
            }
        }

        return subjects;
    }

    private static JsonDocument ReadDocument(ReadOnlyMemory<byte> json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            // A truncated or malformed file must fail, not pass as a shorter list.
            throw new ListFormatException($"France's register isn't valid JSON: {exception.Message}");
        }
    }

    private static JsonElement.ArrayEnumerator Records(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Publications", out var publications) || publications.ValueKind != JsonValueKind.Object
            || !publications.TryGetProperty("PublicationDetail", out var details) || details.ValueKind != JsonValueKind.Array)
        {
            throw new ListFormatException("Expected {\"Publications\": {\"PublicationDetail\": [...]}}.");
        }

        return details.EnumerateArray();
    }

    private static ParsedSubject? ToSubject(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object)
        {
            throw new ListFormatException("A register entry isn't an object.");
        }

        var id = record.TryGetProperty("IdRegistre", out var idElement) && idElement.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? Text.Clean(idElement.ToString())
            : null;
        if (id is null)
        {
            throw new ListFormatException("A register entry has no IdRegistre.");
        }

        var type = String(record, "Nature") switch
        {
            "Personne physique" => SubjectType.Individual,
            "Personne morale" => SubjectType.Entity,
            "Navire" => (SubjectType?)null,
            _ => SubjectType.Unknown,
        };
        if (type is null)
        {
            return null;
        }

        // A field that isn't the shape expected is a format error, never skipped: an update must not lose aliases or
        // addresses while its record count stays the same.
        var fields = new List<(string Type, List<JsonElement> Values)>();
        if (record.TryGetProperty("RegistreDetail", out var detail) && detail.ValueKind != JsonValueKind.Null)
        {
            if (detail.ValueKind != JsonValueKind.Array)
            {
                throw new ListFormatException($"Register entry {id}: RegistreDetail isn't a list.");
            }

            foreach (var field in detail.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object || String(field, "TypeChamp") is not { } fieldType
                    || !field.TryGetProperty("Valeur", out var values) || values.ValueKind != JsonValueKind.Array)
                {
                    throw new ListFormatException($"Register entry {id}: a field isn't {{\"TypeChamp\": …, \"Valeur\": [...]}}.");
                }

                var items = values.EnumerateArray().ToList();
                if (items.Any(value => value.ValueKind != JsonValueKind.Object))
                {
                    throw new ListFormatException($"Register entry {id}: a {fieldType} value isn't an object.");
                }

                fields.Add((fieldType, items));
            }
        }

        IEnumerable<JsonElement> Values(string fieldType) => fields.Where(field => field.Type == fieldType).SelectMany(field => field.Values);

        var builder = new SubjectBuilder(id, type.Value)
        {
            Programmes = Text.Join("; ", Values("FONDEMENT_JURIDIQUE").Select(value => String(value, "FondementJuridiqueLabel"))),
            Remarks = Text.Join(" ", Values("MOTIFS").Select(value => String(value, "Motifs"))),
            SourceUrl = RecordUrl + Uri.EscapeDataString(id),
        };

        var surname = String(record, "Nom");
        var givenNames = Values("PRENOM").Select(value => String(value, "Prenom")).FirstOrDefault(name => Text.Clean(name) is not null);
        builder.AddName(type == SubjectType.Individual ? Text.Join(" ", givenNames, surname) : surname, isWeak: false, isPrimary: true);
        foreach (var alias in Values("ALIAS"))
        {
            var comment = String(alias, "Commentaire");
            builder.AddName(String(alias, "Alias"), isWeak: comment?.Contains("alias faible", StringComparison.OrdinalIgnoreCase) == true, isPrimary: false);
        }

        foreach (var address in Values("ADRESSE_PM").Concat(Values("ADRESSE_PP")))
        {
            builder.AddAddress(Text.Join(", ", String(address, "Adresse"), String(address, "Pays")), countryCode: null);
        }

        return builder.Build();
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
