using System.Text.Json;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Domain;

// The inspection field catalogue is the one place the field set lives (FR-7), so most of these tests are driven by
// the catalogue itself: adding, removing or re-bounding a field changes the catalogue and they follow it. The tests
// that name a field pin the draft set the owner validates in Phase 1.
public sealed class InspectionFieldsTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Raw(string name, string json) => new() { [name] = Json(json) };

    private static InspectionField Field(string name) => InspectionFields.All.Single(f => f.Name == name);

    [Fact] // invariant: a field is named once, documented and well-formed, or the catalogue cannot drive validation
    public void All_ShouldHoldWellFormedUniquelyNamedFields_WhenTheCatalogueIsRead()
    {
        InspectionFields.All.Should().NotBeEmpty();
        InspectionFields.All.Select(f => f.Name).Should().OnlyHaveUniqueItems().And.OnlyContain(n => n.Length > 0 && char.IsLower(n[0]));
        InspectionFields.All.Should().AllSatisfy(f =>
        {
            f.Description.Should().NotBeNullOrWhiteSpace();
            if (f.Kind is InspectionFieldKind.Choice or InspectionFieldKind.Choices)
            {
                f.Options.Should().NotBeEmpty().And.OnlyHaveUniqueItems();
            }

            if (f.Kind == InspectionFieldKind.Count)
            {
                f.Min.Should().BeLessThan(f.Max);
            }
        });
    }

    [Fact] // regression risk: every field FR-7 names is in the draft set, so the owner sees each of them
    public void All_ShouldHoldEveryFieldFr7Names_WhenTheCatalogueIsRead()
    {
        InspectionFields.All.Select(f => f.Name).Should().Contain(
            ["queenSeen", "eggsSeen", "broodFrames", "broodPattern", "foodStores", "temperament", "diseaseSigns", "actionsTaken"]);
    }

    [Fact] // boundary: no findings at all is valid here; whether a note must stand in for them is the request's rule
    public void Check_ShouldReturnEmptyFindings_WhenNothingIsGiven()
    {
        var check = InspectionFields.Check(null);

        check.Errors.Should().BeEmpty();
        check.Findings!.Values.Should().BeEmpty();
    }

    [Fact] // invariant: a valid value of every kind is accepted and stored in the catalogue's own spelling
    public void Check_ShouldAcceptEveryKind_WhenEachValueIsValid()
    {
        var check = InspectionFields.Check(new Dictionary<string, JsonElement>
        {
            ["queenSeen"] = Json("true"),
            ["eggsSeen"] = Json("false"),
            ["broodFrames"] = Json("6"),
            ["broodPattern"] = Json("\" Solid \""),
            ["diseaseSigns"] = Json("[\"ROPY-BROOD\",\"deformed-wings\"]"),
        });

        check.Errors.Should().BeEmpty();
        check.Findings!.Values.Keys.Should().Equal("queenSeen", "eggsSeen", "broodFrames", "broodPattern", "diseaseSigns");
        check.Findings.Values["queenSeen"].GetBoolean().Should().BeTrue();
        check.Findings.Values["eggsSeen"].GetBoolean().Should().BeFalse();
        check.Findings.Values["broodFrames"].GetInt32().Should().Be(6);
        check.Findings.Values["broodPattern"].GetString().Should().Be("solid");
        check.Findings.Values["diseaseSigns"].EnumerateArray().Select(e => e.GetString()).Should().Equal("deformed-wings", "ropy-brood");
    }

    [Fact] // invariant: the stored findings follow the catalogue's order, not the caller's
    public void Check_ShouldKeepTheCataloguesOrder_WhenGivenFieldsInAnyOrder()
    {
        var check = InspectionFields.Check(new Dictionary<string, JsonElement>
        {
            ["temperament"] = Json("\"calm\""),
            ["queenSeen"] = Json("true"),
        });

        check.Findings!.Values.Keys.Should().Equal("queenSeen", "temperament");
    }

    [Fact] // regression risk: findings held in any order are put in the catalogue's, unknown names last by ordinal
    public void InCatalogueOrder_ShouldPutTheCataloguesFieldsFirstAndTheRestInOrdinalOrder_WhenGivenAnyOrder()
    {
        var findings = InspectionFields.InCatalogueOrder(new Dictionary<string, JsonElement>
        {
            ["zRetired"] = Json("1"),
            ["temperament"] = Json("\"calm\""),
            ["Retired"] = Json("2"),
            ["queenSeen"] = Json("true"),
        });

        findings.Values.Keys.Should().Equal("queenSeen", "temperament", "Retired", "zRetired");
        findings.Values["zRetired"].GetInt32().Should().Be(1);
    }

    [Fact] // boundary: a null is a question left unanswered, so it records nothing
    public void Check_ShouldLeaveTheFieldOut_WhenItsValueIsNull()
    {
        var check = InspectionFields.Check(Raw("queenSeen", "null"));

        check.Errors.Should().BeEmpty();
        check.Findings!.Values.Should().BeEmpty();
    }

    [Fact] // boundary: an empty list records that the signs were looked for and none were seen
    public void Check_ShouldKeepAnEmptyList_WhenNoSignsWereSeen()
    {
        var check = InspectionFields.Check(Raw("diseaseSigns", "[]"));

        check.Errors.Should().BeEmpty();
        check.Findings!.Values["diseaseSigns"].GetArrayLength().Should().Be(0);
    }

    [Fact] // invariant: a choice listed twice is stored once
    public void Check_ShouldStoreAChoiceOnce_WhenItIsListedTwice()
    {
        var check = InspectionFields.Check(Raw("actionsTaken", "[\"fed\",\"FED\"]"));

        check.Findings!.Values["actionsTaken"].EnumerateArray().Select(e => e.GetString()).Should().Equal("fed");
    }

    [Fact] // boundary: a field the catalogue does not name is refused, so a typo is never silently dropped
    public void Check_ShouldRefuseTheField_WhenTheCatalogueDoesNotNameIt()
    {
        var check = InspectionFields.Check(Raw("queenSeeen", "true"));

        check.Findings.Should().BeNull();
        check.Errors.Should().ContainKey("findings.queenSeeen")
            .WhoseValue.Single().Should().StartWith("Validation:").And.Contain("queenSeen");
    }

    [Theory] // boundary: a count is a whole number within its bounds, both ends included
    [InlineData("0", true)]
    [InlineData("100", true)]
    [InlineData("-1", false)]
    [InlineData("101", false)]
    [InlineData("6.5", false)]
    [InlineData("\"6\"", false)]
    [InlineData("true", false)]
    public void Check_ShouldAcceptACountOnlyWithinItsBounds_WhenGivenBroodFrames(string json, bool valid)
    {
        var field = Field("broodFrames");
        field.Min.Should().Be(0);
        field.Max.Should().Be(100);

        var check = InspectionFields.Check(Raw("broodFrames", json));

        (check.Errors.Count == 0).Should().Be(valid);
        if (!valid)
        {
            check.Errors["findings.broodFrames"].Single().Should().StartWith("Validation:");
        }
    }

    [Theory] // invariant: a flag is a JSON boolean, not a word or a number
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("[]")]
    public void Check_ShouldRefuseAFlag_WhenItIsNotABoolean(string json)
    {
        var check = InspectionFields.Check(Raw("queenSeen", json));

        check.Errors.Should().ContainKey("findings.queenSeen");
    }

    [Theory] // invariant: a choice is one of its listed options (case aside), a single string
    [InlineData("\"unlisted\"")]
    [InlineData("\"\"")]
    [InlineData("3")]
    [InlineData("[\"solid\"]")]
    public void Check_ShouldRefuseAChoice_WhenItIsNotOneOfItsOptions(string json)
    {
        var check = InspectionFields.Check(Raw("broodPattern", json));

        check.Errors["findings.broodPattern"].Single().Should().StartWith("Validation:").And.Contain("solid");
    }

    [Theory] // invariant: a list of choices is an array of listed options, each checked
    [InlineData("\"ropy-brood\"")]
    [InlineData("[\"unlisted\"]")]
    [InlineData("[\"ropy-brood\",3]")]
    public void Check_ShouldRefuseAListOfChoices_WhenItIsNotAnArrayOfItsOptions(string json)
    {
        var check = InspectionFields.Check(Raw("diseaseSigns", json));

        check.Errors.Should().ContainKey("findings.diseaseSigns");
    }

    [Fact] // invariant: every invalid field is reported at once, so a caller fixes them in one round
    public void Check_ShouldReportEveryInvalidField_WhenSeveralAreInvalid()
    {
        var check = InspectionFields.Check(new Dictionary<string, JsonElement>
        {
            ["broodFrames"] = Json("-3"),
            ["temperament"] = Json("\"furious\""),
            ["queenSeen"] = Json("true"),
        });

        check.Findings.Should().BeNull();
        check.Errors.Keys.Should().BeEquivalentTo(["findings.broodFrames", "findings.temperament"]);
    }

    [Fact] // regression risk: errors are keyed by the path the caller used, wherever the findings sit in the request
    public void Check_ShouldKeyErrorsByThePathGiven_WhenAPathIsGiven()
    {
        var check = InspectionFields.Check(Raw("broodFrames", "-1"), "inspection.findings");

        check.Errors.Should().ContainKey("inspection.findings.broodFrames");
    }

    [Fact] // invariant: the description a caller reads is built from the catalogue, so it cannot drift from it
    public void Describe_ShouldNameEveryFieldAndItsOptions_WhenTheCatalogueIsDescribed()
    {
        var description = InspectionFields.Describe();

        InspectionFields.All.Should().AllSatisfy(f => description.Should().Contain(f.Name));
        InspectionFields.All.SelectMany(f => f.Options).Should().AllSatisfy(o => description.Should().Contain(o));
    }
}
