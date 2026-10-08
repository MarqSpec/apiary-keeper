using System.Text.Json;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// The stored form of an inspection's findings (the Inspections.Findings jsonb column): recorded once and read for
// ever, so it must read back unchanged, and it holds whatever the catalogue held when it was recorded.
public sealed class InspectionFindingsJsonTests
{
    private static InspectionFindings Findings(params (string Name, string Json)[] values) => new()
    {
        Values = values.ToDictionary(v => v.Name, v => JsonDocument.Parse(v.Json).RootElement.Clone(), StringComparer.Ordinal),
    };

    [Fact] // regression risk: every kind of value reads back exactly as it was recorded
    public void Deserialize_ShouldReturnTheRecordedValues_WhenGivenWhatSerializeWrote()
    {
        var findings = Findings(
            ("queenSeen", "true"),
            ("broodFrames", "6"),
            ("broodPattern", "\"solid\""),
            ("diseaseSigns", "[\"ropy-brood\"]"),
            ("actionsTaken", "[]"));

        var read = InspectionFindingsJson.Deserialize(InspectionFindingsJson.Serialize(findings));

        read.Values.Keys.Should().Equal(findings.Values.Keys);
        read.Values.Select(v => v.Value.GetRawText()).Should().Equal(findings.Values.Select(v => v.Value.GetRawText()));
    }

    [Fact] // invariant: the stored spelling is a contract with every inspection already recorded
    public void Serialize_ShouldWriteAPlainObjectOfValues_WhenGivenFindings()
    {
        var json = InspectionFindingsJson.Serialize(Findings(("queenSeen", "true"), ("diseaseSigns", "[]")));

        json.Should().Be("{\"queenSeen\":true,\"diseaseSigns\":[]}");
    }

    [Fact] // regression risk: a field a later catalogue no longer names still reads, so history survives a field-set change
    public void Deserialize_ShouldKeepAFieldTheCatalogueNoLongerNames_WhenItWasRecordedEarlier()
    {
        var read = InspectionFindingsJson.Deserialize("{\"retiredField\":\"x\",\"queenSeen\":false}");

        read.Values["retiredField"].GetString().Should().Be("x");
        read.Values["queenSeen"].GetBoolean().Should().BeFalse();
    }

    [Fact] // regression risk: jsonb hands keys back shortest first, so the stored order is not the catalogue's
    public void Deserialize_ShouldReturnTheCataloguesOrder_WhenTheStoredKeysAreInJsonbOrder()
    {
        // What PostgreSQL's jsonb returns: by key length, then bytes.
        var read = InspectionFindingsJson.Deserialize("{\"eggsSeen\":true,\"queenSeen\":true,\"foodStores\":\"low\",\"broodFrames\":6}");

        read.Values.Keys.Should().Equal("queenSeen", "eggsSeen", "broodFrames", "foodStores");
    }

    [Fact] // regression risk: a field the catalogue no longer names comes after the catalogue's, in a fixed order
    public void Deserialize_ShouldPutFieldsTheCatalogueNoLongerNamesLast_WhenItWasRecordedEarlier()
    {
        var read = InspectionFindingsJson.Deserialize("{\"zRetired\":1,\"queenSeen\":true,\"aRetired\":2,\"temperament\":\"calm\"}");

        read.Values.Keys.Should().Equal("queenSeen", "temperament", "aRetired", "zRetired");
    }

    [Fact] // invariant: findings with nothing filled in are stored as an empty object, and read back empty
    public void Deserialize_ShouldReturnNoValues_WhenTheFindingsAreEmpty()
    {
        InspectionFindingsJson.Serialize(InspectionFindings.Empty).Should().Be("{}");
        InspectionFindingsJson.Deserialize("{}").Values.Should().BeEmpty();
    }

    [Fact] // boundary: a stored value that is not an object is corrupt, not empty findings
    public void Deserialize_ShouldThrow_WhenTheStoredValueIsNotAnObject()
    {
        var act = () => InspectionFindingsJson.Deserialize("null");

        act.Should().Throw<JsonException>();
    }
}
