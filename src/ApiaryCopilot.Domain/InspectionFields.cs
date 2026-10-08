using System.Text.Json;

namespace ApiaryCopilot.Domain;

/// <summary>
/// What an inspection found, keyed by the field names of <see cref="InspectionFields"/>: a boolean, a whole number,
/// a string or an array of strings per field. Only fields the inspector filled in are held. Findings the API returns
/// are in the catalogue's order, then any recorded name the catalogue no longer holds in ordinal order
/// (<see cref="InspectionFields.InCatalogueOrder"/>); a client that must not depend on order keys by field name.
/// </summary>
public sealed record InspectionFindings
{
    /// <summary>Findings with no field filled in.</summary>
    public static InspectionFindings Empty { get; } = new() { Values = new Dictionary<string, JsonElement>() };

    /// <summary>The recorded values, by field name, enumerated in the catalogue's order.</summary>
    public required IReadOnlyDictionary<string, JsonElement> Values { get; init; }
}

/// <summary>The shape of a value an inspection field takes.</summary>
public enum InspectionFieldKind
{
    /// <summary>Yes or no: <see langword="true"/> or <see langword="false"/>.</summary>
    Flag,

    /// <summary>A whole number from <see cref="InspectionField.Min"/> to <see cref="InspectionField.Max"/>.</summary>
    Count,

    /// <summary>One of <see cref="InspectionField.Options"/>.</summary>
    Choice,

    /// <summary>Any number of <see cref="InspectionField.Options"/>, none included.</summary>
    Choices,
}

/// <summary>One field an inspection records (FR-7): its name, what it means and the values it takes.</summary>
public sealed record InspectionField
{
    /// <summary>The field's name, as a caller writes it.</summary>
    public required string Name { get; init; }

    /// <summary>What the field records.</summary>
    public required string Description { get; init; }

    /// <summary>The shape of its value.</summary>
    public required InspectionFieldKind Kind { get; init; }

    /// <summary>The smallest count, for <see cref="InspectionFieldKind.Count"/>.</summary>
    public int Min { get; init; }

    /// <summary>The largest count, for <see cref="InspectionFieldKind.Count"/>.</summary>
    public int Max { get; init; }

    /// <summary>The values a choice may take, for <see cref="InspectionFieldKind.Choice"/> and <see cref="InspectionFieldKind.Choices"/>.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>A yes-or-no field.</summary>
    public static InspectionField Flag(string name, string description) =>
        new() { Name = name, Description = description, Kind = InspectionFieldKind.Flag };

    /// <summary>A whole-number field from <paramref name="min"/> to <paramref name="max"/>.</summary>
    public static InspectionField Count(string name, string description, int min, int max) =>
        new() { Name = name, Description = description, Kind = InspectionFieldKind.Count, Min = min, Max = max };

    /// <summary>A field that takes one of <paramref name="options"/>.</summary>
    public static InspectionField Choice(string name, string description, params string[] options) =>
        new() { Name = name, Description = description, Kind = InspectionFieldKind.Choice, Options = options };

    /// <summary>A field that takes any number of <paramref name="options"/>.</summary>
    public static InspectionField Choices(string name, string description, params string[] options) =>
        new() { Name = name, Description = description, Kind = InspectionFieldKind.Choices, Options = options };
}

/// <summary>The outcome of checking findings: the normalized findings, or the field errors that stopped it.</summary>
public sealed record InspectionFindingsCheck(InspectionFindings? Findings, IReadOnlyDictionary<string, string[]> Errors);

/// <summary>
/// The catalogue of fields an inspection records (FR-7), and the one place that defines them. Phase 1 validates this
/// set with the owner (REQUIREMENTS.md §6), so changing it is meant to be an edit to <see cref="All"/> alone: validation, the
/// stored findings, the API's published description and the unit tests that walk the catalogue all follow it, and
/// findings are stored as JSON, so no migration is needed. Removing or renaming a field leaves recorded findings
/// that name it readable but no longer accepted on new requests; widen a field's bounds or options freely, narrow
/// them knowing recorded values stay as they were.
/// </summary>
public static class InspectionFields
{
    // To add a field, add a line here: nothing else changes. Draft set for the owner's Phase 1 review.

    /// <summary>Every field, in the order findings are stored and described.</summary>
    public static IReadOnlyList<InspectionField> All { get; } =
    [
        InspectionField.Flag("queenSeen", "The queen was seen."),
        InspectionField.Flag("eggsSeen", "Eggs were seen."),
        InspectionField.Count("broodFrames", "How many frames of brood.", 0, 100),
        InspectionField.Choice("broodPattern", "The pattern of the brood.", "solid", "spotty", "none"),
        InspectionField.Choice("foodStores", "How much honey and pollen the colony has.", "none", "low", "adequate", "plenty"),
        InspectionField.Choice("temperament", "How the bees behaved.", "calm", "nervous", "defensive"),
        InspectionField.Choices(
            "diseaseSigns",
            "Signs of disease or pests observed; an empty list records that none were seen. The app records observations and never names a disease.",
            "deformed-wings", "sunken-cappings", "ropy-brood", "mummified-brood", "small-hive-beetle", "wax-moth", "varroa-seen", "dysentery"),
        InspectionField.Choices(
            "actionsTaken",
            "What was done during the inspection.",
            "fed", "added-box", "removed-box", "swapped-frames", "equalized", "treated", "other"),
    ];

    /// <summary>
    /// The findings with their values enumerated in the catalogue's order, then names the catalogue no longer holds in
    /// ordinal order, so a stable order does not depend on how they were stored (jsonb hands keys back shortest first).
    /// </summary>
    public static InspectionFindings InCatalogueOrder(IEnumerable<KeyValuePair<string, JsonElement>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var held = values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        var ordered = new OrderedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in All.Where(f => held.ContainsKey(f.Name)))
        {
            ordered.Add(field.Name, held[field.Name]);
        }

        foreach (var name in held.Keys.Where(k => !ordered.ContainsKey(k)).Order(StringComparer.Ordinal))
        {
            ordered.Add(name, held[name]);
        }

        return new InspectionFindings { Values = ordered };
    }

    /// <summary>
    /// Checks the caller's findings against <see cref="All"/>. Every invalid field is reported, keyed by
    /// <paramref name="path"/> and the field's name, with a message that starts <c>Validation:</c>. A field given as
    /// <c>null</c> is left out; a field the catalogue does not name is refused. On success the findings hold the
    /// normalized values in the catalogue's order.
    /// </summary>
    public static InspectionFindingsCheck Check(IReadOnlyDictionary<string, JsonElement>? raw, string path = "findings")
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        raw ??= new Dictionary<string, JsonElement>();

        foreach (var name in raw.Keys.Where(k => All.All(f => f.Name != k)).Order(StringComparer.Ordinal))
        {
            errors[$"{path}.{name}"] = [$"Validation: {path}.{name} is not an inspection field; the fields are {string.Join(", ", All.Select(f => f.Name))}."];
        }

        foreach (var field in All)
        {
            if (!raw.TryGetValue(field.Name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (Normalize(field, value) is { } normalized)
            {
                values[field.Name] = normalized;
            }
            else
            {
                errors[$"{path}.{field.Name}"] = [$"Validation: {path}.{field.Name} must be {Expectation(field)}."];
            }
        }

        return errors.Count > 0
            ? new InspectionFindingsCheck(null, errors)
            : new InspectionFindingsCheck(new InspectionFindings { Values = values }, errors);
    }

    /// <summary>Every field with what it records and the values it takes, for the API's published description.</summary>
    public static string Describe() => string.Join(
        " ",
        All.Select(f => $"{f.Name}: {f.Description} Takes {Expectation(f)}."));

    // What a value must be, written once for an error ("must be ...") and for the description ("Takes ...").
    private static string Expectation(InspectionField field) => field.Kind switch
    {
        InspectionFieldKind.Flag => "true or false",
        InspectionFieldKind.Count => $"a whole number from {field.Min} to {field.Max}",
        InspectionFieldKind.Choice => $"one of {string.Join(", ", field.Options)}",
        InspectionFieldKind.Choices => $"a list of any of {string.Join(", ", field.Options)}",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field.Kind, null),
    };

    private static JsonElement? Normalize(InspectionField field, JsonElement value) => field.Kind switch
    {
        InspectionFieldKind.Flag when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.Clone(),
        InspectionFieldKind.Count when value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var count) && count >= field.Min && count <= field.Max => value.Clone(),
        InspectionFieldKind.Choice when Option(field, value) is { } option => JsonSerializer.SerializeToElement(option),
        InspectionFieldKind.Choices => Options(field, value),
        _ => null,
    };

    private static JsonElement? Options(InspectionField field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (Option(field, item) is not { } option)
            {
                return null;
            }

            chosen.Add(option);
        }

        return JsonSerializer.SerializeToElement(field.Options.Where(chosen.Contains).ToArray());
    }

    private static string? Option(InspectionField field, JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? field.Options.FirstOrDefault(o => string.Equals(o, value.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase))
            : null;
}
