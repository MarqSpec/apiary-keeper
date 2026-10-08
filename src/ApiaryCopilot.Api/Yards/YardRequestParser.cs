using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Api.Yards;

/// <summary>The outcome of parsing a yard request: the details, or the field errors that stopped it.</summary>
public sealed record YardParseResult(YardDetails? Details, IReadOnlyDictionary<string, string[]> Errors);

/// <summary>Parses a <see cref="YardRequest"/> into <see cref="YardDetails"/> (FR-1, FR-2); never throws.</summary>
public static class YardRequestParser
{
    /// <summary>The state codes a yard may be in, and what each means.</summary>
    public static string StateCode(UsState state) => state switch
    {
        UsState.Texas => "TX",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>The wire value of the landowner's terms.</summary>
    public static string TermsCode(LandownerTerms terms) => terms switch
    {
        LandownerTerms.Honey => "honey",
        LandownerTerms.Fee => "fee",
        LandownerTerms.Free => "free",
        _ => throw new ArgumentOutOfRangeException(nameof(terms), terms, null),
    };

    /// <summary>
    /// Parses the request. Text is trimmed, and blank optional text is null. Every invalid field is reported,
    /// keyed by its JSON path, with a message that starts <c>Validation:</c>.
    /// </summary>
    public static YardParseResult Parse(YardRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = Required(request.Name, "name", YardDetails.NameMaxLength, errors);
        var location = ParseLocation(request.Location, errors);
        var state = ParseState(request.State, errors);
        var county = Required(request.County, "county", YardDetails.CountyMaxLength, errors);
        var accessNotes = Optional(request.AccessNotes, "accessNotes", YardDetails.NotesMaxLength, errors);
        var forageNotes = Optional(request.ForageNotes, "forageNotes", YardDetails.NotesMaxLength, errors);
        var landowner = ParseLandowner(request.Landowner, errors);

        if (errors.Count > 0)
        {
            return new YardParseResult(null, errors);
        }

        return new YardParseResult(
            new YardDetails
            {
                Name = name!,
                Location = location!,
                State = state!.Value,
                County = county!,
                AccessNotes = accessNotes,
                ForageNotes = forageNotes,
                Landowner = landowner!,
            },
            errors);
    }

    private static GeoLocation? ParseLocation(LocationRequest? location, Dictionary<string, string[]> errors)
    {
        if (location is null)
        {
            errors["location"] = ["Validation: location is required, with latitude and longitude."];
            return null;
        }

        var latitude = InRange(location.Latitude, "location.latitude", 90, errors);
        var longitude = InRange(location.Longitude, "location.longitude", 180, errors);
        return latitude is null || longitude is null
            ? null
            : new GeoLocation { Latitude = latitude.Value, Longitude = longitude.Value };
    }

    private static double? InRange(double? value, string field, double limit, Dictionary<string, string[]> errors)
    {
        if (value is { } v && v >= -limit && v <= limit)
        {
            return v;
        }

        errors[field] = [$"Validation: {field} is required, in decimal degrees from -{limit} to {limit}."];
        return null;
    }

    private static UsState? ParseState(string? state, Dictionary<string, string[]> errors)
    {
        if (string.Equals(state?.Trim(), StateCode(UsState.Texas), StringComparison.OrdinalIgnoreCase))
        {
            return UsState.Texas;
        }

        errors["state"] = ["Validation: state must be TX; only Texas is supported for now."];
        return null;
    }

    private static Landowner? ParseLandowner(LandownerRequest? landowner, Dictionary<string, string[]> errors)
    {
        if (landowner is null)
        {
            errors["landowner"] = ["Validation: landowner is required, with contact and terms."];
            return null;
        }

        var contact = Required(landowner.Contact, "landowner.contact", Landowner.ContactMaxLength, errors);
        var terms = ParseTerms(landowner.Terms, errors);
        var nearbyCrops = Optional(landowner.NearbyCrops, "landowner.nearbyCrops", Landowner.NearbyCropsMaxLength, errors);
        var sprayContact = Optional(landowner.SprayContact, "landowner.sprayContact", Landowner.ContactMaxLength, errors);
        return contact is null || terms is null
            ? null
            : new Landowner { Contact = contact, Terms = terms.Value, NearbyCrops = nearbyCrops, SprayContact = sprayContact };
    }

    private static LandownerTerms? ParseTerms(string? terms, Dictionary<string, string[]> errors)
    {
        var trimmed = terms?.Trim();
        foreach (var candidate in Enum.GetValues<LandownerTerms>())
        {
            if (string.Equals(trimmed, TermsCode(candidate), StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        errors["landowner.terms"] = ["Validation: landowner.terms must be honey, fee or free."];
        return null;
    }

    private static string? Required(string? value, string field, int maxLength, Dictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            errors[field] = [$"Validation: {field} is required."];
            return null;
        }

        return WithinLength(trimmed, field, maxLength, errors);
    }

    private static string? Optional(string? value, string field, int maxLength, Dictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : WithinLength(trimmed, field, maxLength, errors);
    }

    private static string? WithinLength(string value, string field, int maxLength, Dictionary<string, string[]> errors)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        errors[field] = [$"Validation: {field} must be at most {maxLength} characters."];
        return null;
    }
}
