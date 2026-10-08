using ApiaryCopilot.Api.Yards;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Api;

// Validation of a yard request (FR-1, FR-2): failures are categorized field errors, never exceptions (§4.3).
public sealed class YardRequestParserTests
{
    [Fact] // regression risk: a valid request maps every field, trimmed, onto the domain
    public void Parse_ShouldReturnTheDetails_WhenTheRequestIsValid()
    {
        var result = YardRequestParser.Parse(Valid() with { Name = "  Mesquite Flat  ", AccessNotes = "   " });

        result.Errors.Should().BeEmpty();
        result.Details.Should().Be(new YardDetails
        {
            Name = "Mesquite Flat",
            Location = new GeoLocation { Latitude = 30.2672, Longitude = -97.7431 },
            State = UsState.Texas,
            County = "Travis",
            AccessNotes = null,
            ForageNotes = "Huisache in February.",
            Landowner = new Landowner
            {
                Contact = "Synthetic Landowner, 555-0100",
                Terms = LandownerTerms.Fee,
                NearbyCrops = "Cotton to the east.",
                SprayContact = "Synthetic Crop Duster, 555-0101",
            },
        });
    }

    [Theory] // boundary: state and terms are matched without regard to case
    [InlineData("tx", "HONEY", LandownerTerms.Honey)]
    [InlineData("TX", "free", LandownerTerms.Free)]
    public void Parse_ShouldAcceptAnyCase_WhenStateAndTermsDifferOnlyInCase(string state, string terms, LandownerTerms expected)
    {
        var request = Valid() with { State = state, Landowner = Valid().Landowner! with { Terms = terms } };

        var result = YardRequestParser.Parse(request);

        result.Details!.Landowner.Terms.Should().Be(expected);
    }

    [Fact] // boundary: a name exactly at the maximum length is accepted
    public void Parse_ShouldAcceptTheName_WhenItIsExactlyTheMaximumLength()
    {
        var result = YardRequestParser.Parse(Valid() with { Name = new string('n', YardDetails.NameMaxLength) });

        result.Errors.Should().BeEmpty();
    }

    [Theory] // boundary: latitude and longitude at their limits are accepted
    [InlineData(90, 180)]
    [InlineData(-90, -180)]
    public void Parse_ShouldAcceptTheLocation_WhenItIsAtTheLimits(double latitude, double longitude)
    {
        var result = YardRequestParser.Parse(Valid() with { Location = new LocationRequest { Latitude = latitude, Longitude = longitude } });

        result.Errors.Should().BeEmpty();
    }

    [Theory] // boundary: each invalid field is reported under its own name, and nothing is parsed
    [MemberData(nameof(InvalidRequests))]
    public void Parse_ShouldReturnAValidationErrorForTheField_WhenTheFieldIsInvalid(string field, YardRequest request)
    {
        var result = YardRequestParser.Parse(request);

        result.Details.Should().BeNull();
        result.Errors.Keys.Should().Equal(field);
        result.Errors[field].Should().ContainSingle().Which.Should().StartWith("Validation:");
    }

    [Fact] // invariant: every invalid field is reported at once, so the caller can fix them in one go
    public void Parse_ShouldReportEveryInvalidField_WhenSeveralAreInvalid()
    {
        var result = YardRequestParser.Parse(new YardRequest());

        result.Errors.Keys.Should().BeEquivalentTo("name", "location", "state", "county", "landowner");
    }

    public static TheoryData<string, YardRequest> InvalidRequests()
    {
        var valid = Valid();
        var landowner = valid.Landowner!;
        return new TheoryData<string, YardRequest>
        {
            { "name", valid with { Name = null } },
            { "name", valid with { Name = "   " } },
            { "name", valid with { Name = new string('n', YardDetails.NameMaxLength + 1) } },
            { "location", valid with { Location = null } },
            { "location.latitude", valid with { Location = new LocationRequest { Latitude = null, Longitude = -97.7 } } },
            { "location.latitude", valid with { Location = new LocationRequest { Latitude = 90.0001, Longitude = -97.7 } } },
            { "location.latitude", valid with { Location = new LocationRequest { Latitude = -90.0001, Longitude = -97.7 } } },
            { "location.longitude", valid with { Location = new LocationRequest { Latitude = 30.2, Longitude = null } } },
            { "location.longitude", valid with { Location = new LocationRequest { Latitude = 30.2, Longitude = 180.0001 } } },
            { "location.longitude", valid with { Location = new LocationRequest { Latitude = 30.2, Longitude = -180.0001 } } },
            { "state", valid with { State = null } },
            { "state", valid with { State = "NM" } },
            { "state", valid with { State = "Texas" } },
            { "county", valid with { County = " " } },
            { "county", valid with { County = new string('c', YardDetails.CountyMaxLength + 1) } },
            { "accessNotes", valid with { AccessNotes = new string('a', YardDetails.NotesMaxLength + 1) } },
            { "forageNotes", valid with { ForageNotes = new string('f', YardDetails.NotesMaxLength + 1) } },
            { "landowner", valid with { Landowner = null } },
            { "landowner.contact", valid with { Landowner = landowner with { Contact = "" } } },
            { "landowner.contact", valid with { Landowner = landowner with { Contact = new string('c', Landowner.ContactMaxLength + 1) } } },
            { "landowner.terms", valid with { Landowner = landowner with { Terms = null } } },
            { "landowner.terms", valid with { Landowner = landowner with { Terms = "barter" } } },
            { "landowner.terms", valid with { Landowner = landowner with { Terms = "1" } } },
            { "landowner.nearbyCrops", valid with { Landowner = landowner with { NearbyCrops = new string('n', Landowner.NearbyCropsMaxLength + 1) } } },
            { "landowner.sprayContact", valid with { Landowner = landowner with { SprayContact = new string('s', Landowner.ContactMaxLength + 1) } } },
        };
    }

    private static YardRequest Valid() => new()
    {
        Name = "Mesquite Flat",
        Location = new LocationRequest { Latitude = 30.2672, Longitude = -97.7431 },
        State = "TX",
        County = "Travis",
        AccessNotes = "Gate code from the landowner.",
        ForageNotes = "Huisache in February.",
        Landowner = new LandownerRequest
        {
            Contact = "Synthetic Landowner, 555-0100",
            Terms = "fee",
            NearbyCrops = "Cotton to the east.",
            SprayContact = "Synthetic Crop Duster, 555-0101",
        },
    };
}
