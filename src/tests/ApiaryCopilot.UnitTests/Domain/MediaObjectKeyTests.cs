using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Domain;

// The bucket key of an uploaded object: prefixed by its account (NFR-8) and its inspection, ending in a random token.
public sealed class MediaObjectKeyTests
{
    private static readonly AccountId Account = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly AccountId OtherAccount = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly InspectionId Inspection = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000d1"));
    private static readonly InspectionId OtherInspection = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000d2"));
    private const string Token = "0123456789abcdef0123456789abcdef"; // gitleaks:allow - synthetic key token, the shape every issued key ends in
    private const string Valid = "accounts/11111111-1111-4111-8111-111111111111/inspections/0199b2a4-0000-7000-8000-0000000000d1/" + Token;

    [Fact] // invariant (NFR-8): a new key starts with its account, then its inspection
    public void New_ShouldPrefixTheKeyWithTheAccountThenTheInspection_WhenCreated()
    {
        var key = MediaObjectKey.New(Account, Inspection);

        key.Value.Should().StartWith($"accounts/{Account}/inspections/{Inspection}/");
        key.AccountId.Should().Be(Account);
        key.InspectionId.Should().Be(Inspection);
    }

    [Fact] // invariant (token custody): the key ends in 128 random bits, so it cannot be guessed from the ids
    public void New_ShouldEndInA32HexCharacterRandomToken_WhenCreated()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => MediaObjectKey.New(Account, Inspection).Value).ToList();

        keys.Should().OnlyHaveUniqueItems();
        keys.Select(k => k[(k.LastIndexOf('/') + 1)..]).Should().AllSatisfy(token => token.Should().MatchRegex("^[0-9a-f]{32}$"));
    }

    [Fact] // invariant: a key this API issued parses back to the same account, inspection and text
    public void Parse_ShouldReturnTheKey_WhenTheTextIsAnIssuedKey()
    {
        var issued = MediaObjectKey.New(Account, Inspection);

        var parsed = MediaObjectKey.Parse(issued.Value);

        parsed.Should().Be(issued);
    }

    [Theory] // boundary (adversarial): anything but the one canonical spelling of an issued key is refused
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Token)]
    [InlineData("accounts/11111111-1111-4111-8111-111111111111/inspections/0199b2a4-0000-7000-8000-0000000000d1/")]
    [InlineData("accounts/11111111-1111-4111-8111-111111111111/inspections/0199b2a4-0000-7000-8000-0000000000d1/0123456789abcdef")]
    [InlineData("accounts/11111111-1111-4111-8111-111111111111/inspections/0199b2a4-0000-7000-8000-0000000000d1/0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("accounts/11111111-1111-4111-8111-111111111111/inspections/0199b2a4-0000-7000-8000-0000000000d1/" + Token + "/x")]
    [InlineData("accounts/11111111-1111-4111-8111-111111111111/inspections/0199b2a4-0000-7000-8000-0000000000d1/../" + Token)]
    [InlineData("/accounts/11111111-1111-4111-8111-111111111111/inspections/0199b2a4-0000-7000-8000-0000000000d1/" + Token)]
    [InlineData("accounts/11111111-1111-4111-8111-111111111111/inspections/0199B2A4-0000-7000-8000-0000000000D1/" + Token)]
    [InlineData("accounts/{11111111-1111-4111-8111-111111111111}/inspections/0199b2a4-0000-7000-8000-0000000000d1/" + Token)]
    [InlineData("accounts/00000000-0000-0000-0000-000000000000/inspections/0199b2a4-0000-7000-8000-0000000000d1/" + Token)]
    [InlineData("accounts/11111111-1111-4111-8111-111111111111/inspections/00000000-0000-0000-0000-000000000000/" + Token)]
    [InlineData("organizations/11111111-1111-4111-8111-111111111111/inspections/0199b2a4-0000-7000-8000-0000000000d1/" + Token)] // the prefix before the account-rooted schema ()
    [InlineData("tenants/11111111-1111-4111-8111-111111111111/inspections/0199b2a4-0000-7000-8000-0000000000d1/" + Token)]
    [InlineData("accounts/11111111-1111-4111-8111-111111111111/colonies/0199b2a4-0000-7000-8000-0000000000d1/" + Token)]
    [InlineData(" " + Valid)]
    public void Parse_ShouldReturnNull_WhenTheTextIsNotACanonicalKey(string? text)
    {
        MediaObjectKey.Parse(text).Should().BeNull();
    }

    [Fact] // control for the refusals above: the canonical spelling they vary does parse
    public void Parse_ShouldReturnTheKey_WhenTheTextIsCanonical()
    {
        var parsed = MediaObjectKey.Parse(Valid);

        parsed.Should().NotBeNull();
        parsed!.AccountId.Should().Be(Account);
        parsed.InspectionId.Should().Be(Inspection);
        parsed.Value.Should().Be(Valid);
    }

    [Fact] // invariant: a key belongs to exactly the account and inspection it was issued for
    public void BelongsTo_ShouldBeTrue_WhenTheAccountAndInspectionAreTheKeys()
    {
        MediaObjectKey.Parse(Valid)!.BelongsTo(Account, Inspection).Should().BeTrue();
    }

    [Fact] // invariant (adversarial, NFR-8): another account's key does not belong to the caller
    public void BelongsTo_ShouldBeFalse_WhenTheAccountIsAnother()
    {
        MediaObjectKey.Parse(Valid)!.BelongsTo(OtherAccount, Inspection).Should().BeFalse();
    }

    [Fact] // invariant: a key issued for one inspection cannot be attached to another
    public void BelongsTo_ShouldBeFalse_WhenTheInspectionIsAnother()
    {
        MediaObjectKey.Parse(Valid)!.BelongsTo(Account, OtherInspection).Should().BeFalse();
    }
}
