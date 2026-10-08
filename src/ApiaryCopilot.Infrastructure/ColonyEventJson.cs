using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The stored form of a lineage event's data (the <c>ColonyEvents.Data</c> jsonb column). Every recorded event is
/// read with it for ever, so the type names and property spellings are a contract: rename nothing.
/// </summary>
internal static class ColonyEventJson
{
    private static readonly JsonSerializerOptions Options = new(ColonyEventJsonContext.Default.Options)
    {
        TypeInfoResolver = ColonyEventJsonContext.Default.WithAddedModifier(AddEventKinds),
    };

    public static string Serialize(ColonyEventData data) => JsonSerializer.Serialize(data, Options);

    public static ColonyEventData Deserialize(string json) =>
        JsonSerializer.Deserialize<ColonyEventData>(json, Options)
        ?? throw new JsonException("A stored colony event has no data.");

    // The discriminator lives here, not in the domain, so the domain records stay free of serialization.
    private static void AddEventKinds(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(ColonyEventData))
        {
            return;
        }

        typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "type",
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            DerivedTypes =
            {
                new JsonDerivedType(typeof(ColonyRegistered), "registered"),
                new JsonDerivedType(typeof(SwarmCaptured), "swarm-capture"),
                new JsonDerivedType(typeof(ColonySplit), "split"),
                new JsonDerivedType(typeof(ColonyMerged), "merge"),
                new JsonDerivedType(typeof(ColonyRequeened), "requeen"),
                new JsonDerivedType(typeof(ColonyMoved), "move"),
            },
        };
    }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    Converters = [typeof(ColonyIdJsonConverter), typeof(QueenIdJsonConverter), typeof(YardIdJsonConverter)])]
[JsonSerializable(typeof(ColonyEventData))]
[JsonSerializable(typeof(ColonyRegistered))]
[JsonSerializable(typeof(SwarmCaptured))]
[JsonSerializable(typeof(ColonySplit))]
[JsonSerializable(typeof(ColonyMerged))]
[JsonSerializable(typeof(ColonyRequeened))]
[JsonSerializable(typeof(ColonyMoved))]
internal sealed partial class ColonyEventJsonContext : JsonSerializerContext;

// An id is stored as its bare GUID.
internal abstract class GuidIdJsonConverter<TId>(Func<Guid, TId> create, Func<TId, Guid> value) : JsonConverter<TId>
    where TId : struct
{
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        create(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, TId value1, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value(value1));
    }
}

internal sealed class ColonyIdJsonConverter() : GuidIdJsonConverter<ColonyId>(v => new ColonyId(v), id => id.Value);

internal sealed class QueenIdJsonConverter() : GuidIdJsonConverter<QueenId>(v => new QueenId(v), id => id.Value);

internal sealed class YardIdJsonConverter() : GuidIdJsonConverter<YardId>(v => new YardId(v), id => id.Value);
