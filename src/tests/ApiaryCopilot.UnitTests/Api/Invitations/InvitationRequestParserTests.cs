using ApiaryCopilot.Api.Invitations;

namespace ApiaryCopilot.UnitTests.Api.Invitations;

// What an owner may ask for (FR-74, D-22): an email address, a name and one of helper or grower. An owner is created by
// the Keycloak administrator and a customer by the storefront, so neither is accepted here; each refusal names its field.
public sealed class InvitationRequestParserTests
{
    [Theory] // invariant: a helper or a grower is invited by address and name, the address lower-cased as Keycloak keeps it
    [InlineData("helper", MemberRole.Helper)]
    [InlineData("grower", MemberRole.Grower)]
    public void Parse_ShouldReturnTheInvitation_WhenEveryFieldIsValid(string role, MemberRole expected)
    {
        var parsed = InvitationRequestParser.Parse(new InvitationRequest { Email = "  Helper.Synthetic@Apiary-Keeper.TEST ", Name = " Synthetic Helper ", Role = role });

        parsed.Errors.Should().BeEmpty();
        parsed.Invitation.Should().Be(new ParsedInvitation("helper.synthetic@apiary-keeper.test", "Synthetic Helper", expected));
    }

    [Theory] // invariant (D-22): an owner is never invited, and a customer is the storefront's - each refusal says so
    [InlineData("owner", "administrator")]
    [InlineData("customer", "storefront")]
    [InlineData("Helper", "helper or grower")]
    [InlineData("", "helper or grower")]
    [InlineData(null, "helper or grower")]
    public void Parse_ShouldRefuseTheRole_WhenItIsNotHelperOrGrower(string? role, string reason)
    {
        var parsed = InvitationRequestParser.Parse(Valid() with { Role = role });

        parsed.Invitation.Should().BeNull();
        parsed.Errors.Should().ContainKey("role").WhoseValue.Should().ContainSingle().Which.Should().Contain(reason);
    }

    [Theory] // boundary: an address Keycloak would refuse or rewrite is refused here first
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("two@at@apiary-keeper.test")]
    [InlineData("Synthetic Helper <helper@apiary-keeper.test>")]
    [InlineData("helper@")]
    [InlineData("with space@apiary-keeper.test")]
    public void Parse_ShouldRefuseTheEmail_WhenItIsNotOneAddress(string? email)
    {
        var parsed = InvitationRequestParser.Parse(Valid() with { Email = email });

        parsed.Invitation.Should().BeNull();
        parsed.Errors.Should().ContainKey("email");
    }

    [Fact] // boundary: at most 254 characters, the longest address SMTP carries
    public void Parse_ShouldRefuseTheEmail_WhenItIsLongerThan254Characters()
    {
        var local = new string('a', 64);
        var domain = string.Join('.', Enumerable.Repeat(new string('b', 60), 3)) + ".test";
        var tooLong = $"{local}@{domain}x{new string('c', 254 - local.Length - domain.Length)}";

        InvitationRequestParser.Parse(Valid() with { Email = tooLong }).Errors.Should().ContainKey("email");
    }

    [Theory] // boundary: a name is required, at most 200 characters, with no control characters
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Synthetic\nHelper")]
    public void Parse_ShouldRefuseTheName_WhenItIsMissingOrHasControlCharacters(string? name) =>
        InvitationRequestParser.Parse(Valid() with { Name = name }).Errors.Should().ContainKey("name");

    [Fact]
    public void Parse_ShouldRefuseTheName_WhenItIsLongerThanAUsersName() =>
        InvitationRequestParser.Parse(Valid() with { Name = new string('n', 201) }).Errors.Should().ContainKey("name");

    [Fact] // invariant: every field's error is reported at once
    public void Parse_ShouldReportEveryField_WhenAllAreWrong() =>
        InvitationRequestParser.Parse(new InvitationRequest()).Errors.Keys.Should().BeEquivalentTo("email", "name", "role");

    private static InvitationRequest Valid() => new() { Email = "helper.synthetic@apiary-keeper.test", Name = "Synthetic Helper", Role = "helper" };
}
