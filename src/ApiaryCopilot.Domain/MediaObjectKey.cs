using System.Security.Cryptography;

namespace ApiaryCopilot.Domain;

/// <summary>
/// Where an uploaded photo or video lives in the media bucket (FR-8, D-13):
/// <c>accounts/{account id}/inspections/{inspection id}/{token}</c>. The prefix scopes it to its account
/// (NFR-8) and the inspection it may be attached to; the token is 128 random bits as 32 lower-case hex characters, so a
/// key cannot be guessed from the ids. Only that one spelling parses; nothing from the client, such as a file name,
/// ever becomes part of a key.
/// </summary>
public sealed record MediaObjectKey
{
    private const string AccountsSegment = "accounts";
    private const string InspectionsSegment = "inspections";
    private const int TokenBytes = 16;

    private MediaObjectKey(AccountId accountId, InspectionId inspectionId, string token)
    {
        AccountId = accountId;
        InspectionId = inspectionId;
        Value = $"{AccountsSegment}/{accountId.Value:D}/{InspectionsSegment}/{inspectionId.Value:D}/{token}";
    }

    /// <summary>The account the object belongs to.</summary>
    public AccountId AccountId { get; }

    /// <summary>The inspection the object may be attached to.</summary>
    public InspectionId InspectionId { get; }

    /// <summary>The key itself.</summary>
    public string Value { get; }

    /// <summary>A new, unguessable key for an object of <paramref name="inspectionId"/> in <paramref name="accountId"/>.</summary>
    public static MediaObjectKey New(AccountId accountId, InspectionId inspectionId) =>
        new(accountId, inspectionId, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes)));

    /// <summary>The key <paramref name="text"/> spells, or <see langword="null"/> unless it is exactly a key's one spelling.</summary>
    public static MediaObjectKey? Parse(string? text)
    {
        var parts = text?.Split('/');
        if (parts is not [AccountsSegment, var account, InspectionsSegment, var inspection, var token]
            || CanonicalGuid(account) is not { } accountId
            || CanonicalGuid(inspection) is not { } inspectionId
            || !IsToken(token))
        {
            return null;
        }

        return new MediaObjectKey(new AccountId(accountId), new InspectionId(inspectionId), token);
    }

    /// <summary>Whether the key was issued for <paramref name="inspectionId"/> of <paramref name="accountId"/>.</summary>
    public bool BelongsTo(AccountId accountId, InspectionId inspectionId) =>
        AccountId == accountId && InspectionId == inspectionId;

    /// <inheritdoc />
    public override string ToString() => Value;

    // Object keys are case-sensitive, so only the lower-case "D" form is the key; any other spelling names another object.
    private static Guid? CanonicalGuid(string text) =>
        Guid.TryParseExact(text, "D", out var value) && value != Guid.Empty && value.ToString("D") == text ? value : null;

    private static bool IsToken(string text) =>
        text.Length == TokenBytes * 2 && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
