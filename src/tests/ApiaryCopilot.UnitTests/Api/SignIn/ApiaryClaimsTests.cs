using System.Security.Claims;
using ApiaryCopilot.Api.SignIn;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// The one principal both sign-in paths yield (ARCHITECTURE.md D-19 Option A): sub, account_id and the application roles,
// nothing else (maintainer, 2026-10-06).
public sealed class ApiaryClaimsTests
{
    private const string Subject = "4e97dd9a-9f79-4493-8185-795ac0034b7d";
    private const string AccountId = "a91a7c0e-5747-4e00-8000-000000000019";

    [Fact] // invariant: the principal holds exactly sub, account_id and the roles - the token's other claims are dropped
    public void FromToken_ShouldKeepOnlySubAccountIdAndRoles_WhenTheTokenCarriesMore()
    {
        var principal = ApiaryClaims.FromToken(
        [
            new Claim("sub", Subject), new Claim("account_id", AccountId), new Claim("roles", "owner"),
            new Claim("email", "synthetic-owner@apiary-keeper.test"), new Claim("name", "Owner Synthetic"),
            new Claim("sid", "session"), new Claim("azp", "apiary-keeper-bff"), new Claim("scope", "openid"),
        ]);

        principal.Should().NotBeNull();
        principal!.Claims.Select(c => (c.Type, c.Value)).Should().BeEquivalentTo(
            [("sub", Subject), ("account_id", AccountId), ("role", "owner")]);
        principal.Identity!.IsAuthenticated.Should().BeTrue();
        principal.Identity.AuthenticationType.Should().Be(ApiaryClaims.AuthenticationType);
        principal.IsInRole("owner").Should().BeTrue();
    }

    [Fact] // invariant: only the four application roles (D-22) reach the principal; Keycloak's own roles do not
    public void FromToken_ShouldKeepOnlyTheApplicationRoles_WhenTheTokenCarriesOthers()
    {
        var principal = ApiaryClaims.FromToken(
        [
            new Claim("sub", Subject), new Claim("account_id", AccountId),
            new Claim("roles", "helper"), new Claim("roles", "offline_access"), new Claim("roles", "default-roles-apiary-keeper-dev"),
            new Claim("roles", "grower"), new Claim("roles", "helper"), new Claim("roles", "Owner"),
        ]);

        principal!.FindAll(ApiaryClaims.Role).Select(c => c.Value).Should().BeEquivalentTo("helper", "grower");
    }

    [Fact] // boundary: a user with no application role is still the same principal, with no roles
    public void FromToken_ShouldYieldNoRoles_WhenTheTokenCarriesNone()
    {
        var principal = ApiaryClaims.FromToken([new Claim("sub", Subject), new Claim("account_id", AccountId)]);

        principal!.FindAll(ApiaryClaims.Role).Should().BeEmpty();
    }

    [Theory] // boundary: a token without a subject or an account is refused, never turned into a partial principal
    [InlineData(null, AccountId)]
    [InlineData("", AccountId)]
    [InlineData(" ", AccountId)]
    [InlineData(Subject, null)]
    [InlineData(Subject, "")]
    [InlineData(Subject, "not-a-guid")]
    [InlineData(Subject, "00000000-0000-0000-0000-000000000000")]
    public void FromToken_ShouldRefuse_WhenSubjectOrAccountIsMissingOrMalformed(string? subject, string? accountId)
    {
        var claims = new List<Claim> { new("roles", "owner") };
        if (subject is not null)
        {
            claims.Add(new Claim("sub", subject));
        }

        if (accountId is not null)
        {
            claims.Add(new Claim("account_id", accountId));
        }

        ApiaryClaims.FromToken(claims).Should().BeNull();
    }

    [Fact] // boundary: one account per user (D-22), so a token naming two accounts is refused rather than one picked
    public void FromToken_ShouldRefuse_WhenTheTokenNamesTwoAccounts()
    {
        var principal = ApiaryClaims.FromToken(
        [
            new Claim("sub", Subject), new Claim("account_id", AccountId),
            new Claim("account_id", "c057e0a1-5747-4e00-8000-000000000071"),
        ]);

        principal.Should().BeNull();
    }

    [Fact] // regression risk: the account is held in one canonical form, so both paths compare equal
    public void FromToken_ShouldHoldTheAccountInLowerCase_WhenTheTokenSpellsItInUpperCase()
    {
        var principal = ApiaryClaims.FromToken([new Claim("sub", Subject), new Claim("account_id", AccountId.ToUpperInvariant())]);

        principal!.FindFirst(ApiaryClaims.AccountId)!.Value.Should().Be(AccountId);
    }

    [Theory] // invariant: each path takes only its own kind of Keycloak token, so an ID token is never taken for an access token
    [InlineData("Bearer", true, false)]
    [InlineData("ID", false, true)]
    [InlineData("Refresh", false, false)]
    [InlineData("bearer", false, false)]
    [InlineData("id", false, false)]
    [InlineData(null, false, false)]
    public void FromAccessAndIdToken_ShouldTakeOnlyTheirOwnTokenType_WhenTheTypIsGiven(string? typ, bool access, bool id)
    {
        List<Claim> claims = [new Claim("sub", Subject), new Claim("account_id", AccountId), new Claim("roles", "owner")];
        if (typ is not null)
        {
            claims.Add(new Claim("typ", typ));
        }

        (ApiaryClaims.FromAccessToken(claims) is not null).Should().Be(access);
        (ApiaryClaims.FromIdToken(claims) is not null).Should().Be(id);
    }

    [Fact] // boundary: a token claiming two types is neither, rather than one picked
    public void FromAccessToken_ShouldRefuse_WhenTheTokenCarriesTwoTypes()
    {
        Claim[] claims = [new Claim("sub", Subject), new Claim("account_id", AccountId), new Claim("typ", "Bearer"), new Claim("typ", "ID")];

        ApiaryClaims.FromAccessToken(claims).Should().BeNull();
        ApiaryClaims.FromIdToken(claims).Should().BeNull();
    }

    [Fact] // invariant: the typ check adds nothing to the principal: still sub, account and roles only
    public void FromAccessToken_ShouldYieldTheSamePrincipalAsFromToken_WhenTheTypIsBearer()
    {
        Claim[] claims = [new Claim("sub", Subject), new Claim("account_id", AccountId), new Claim("roles", "helper"), new Claim("typ", "Bearer")];

        ApiaryClaims.FromAccessToken(claims)!.Claims.Select(c => (c.Type, c.Value))
            .Should().BeEquivalentTo([("sub", Subject), ("account_id", AccountId), ("role", "helper")]);
    }
}
