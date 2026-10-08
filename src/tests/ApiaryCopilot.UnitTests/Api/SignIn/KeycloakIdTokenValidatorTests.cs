using System.Security.Claims;
using System.Security.Cryptography;
using ApiaryCopilot.Api.SignIn;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// The ID token a renewal returns is validated as the sign-in's is - the realm's key, the configured issuer, this client
// as the audience, its lifetime, RS256 only and typ ID - before it becomes the session's principal. Tokens are signed
// here with a key made per test and published as the realm's.
public sealed class KeycloakIdTokenValidatorTests : IDisposable
{
    private const string Issuer = "https://keycloak.unit.test/realms/apiary-keeper";
    private const string ClientId = "apiary-keeper-bff";
    private const string KeyId = "unit-test-signing-key";
    private const string Subject = "4e97dd9a-9f79-4493-8185-795ac0034b7d";
    private const string AccountId = "a91a7c0e-5747-4e00-8000-000000000019";

    private readonly RSA _realmKey = RSA.Create(2048);
    private readonly KeycloakIdTokenValidator _validator;

    public KeycloakIdTokenValidatorTests()
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(new RsaSecurityKey(_realmKey) { KeyId = KeyId });
        var monitor = A.Fake<IOptionsMonitor<OpenIdConnectOptions>>();
        A.CallTo(() => monitor.Get(SignInExtensions.OpenIdConnectScheme)).Returns(new OpenIdConnectOptions { Configuration = configuration });
        var signIn = new SignInOptions { Authority = Issuer, ClientId = ClientId, ClientSecret = "synthetic", Audience = "apiary-keeper-api" };
        _validator = new KeycloakIdTokenValidator(monitor, Options.Create(signIn));
    }

    public void Dispose() => _realmKey.Dispose();

    [Fact] // regression risk: a renewed ID token of this realm for this client becomes the principal: sub, account, roles
    public async Task ValidateAsync_ShouldReturnThePrincipal_WhenTheIdTokenIsValid()
    {
        var principal = await _validator.ValidateAsync(Token(), CancellationToken.None);

        principal.Should().NotBeNull();
        principal!.FindFirstValue("sub").Should().Be(Subject);
        principal.FindFirstValue("account_id").Should().Be(AccountId);
        principal.IsInRole("owner").Should().BeTrue();
    }

    public static TheoryData<string> Invalid() => new()
    {
        "another audience",
        "another issuer",
        "expired",
        "another key",
        "HS256 with a shared secret",
        "PS256 with the realm's own key",
        "an access token (typ Bearer)",
        "no account",
        "unsigned",
    };

    [Theory] // invariant: anything but a valid RS256 ID token of this realm for this client is no principal
    [MemberData(nameof(Invalid))]
    public async Task ValidateAsync_ShouldReturnNull_WhenTheTokenIsNotAValidIdToken(string why)
    {
        var token = why switch
        {
            "another audience" => Token(audience: "apiary-keeper-api"),
            "another issuer" => Token(issuer: "https://keycloak.unit.test/realms/elsewhere"),
            "expired" => Token(expires: DateTime.UtcNow.AddMinutes(-5)),
            "another key" => Token(signing: new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256)),
            "HS256 with a shared secret" => Token(signing: new SigningCredentials(
                new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)) { KeyId = KeyId }, SecurityAlgorithms.HmacSha256)),
            "PS256 with the realm's own key" => Token(signing: new SigningCredentials(new RsaSecurityKey(_realmKey) { KeyId = KeyId }, SecurityAlgorithms.RsaSsaPssSha256)),
            "an access token (typ Bearer)" => Token(typ: "Bearer"),
            "no account" => Token(account: null),
            "unsigned" => Unsigned(Token()),
            _ => throw new ArgumentOutOfRangeException(nameof(why)),
        };

        (await _validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull(why);
    }

    [Fact] // invariant: the refresh path pins the same algorithms as the sign-in: RS256 only
    public void IdTokenValidation_ShouldAllowOnlyRs256AndPinTheIssuerAndClient_WhenBuilt()
    {
        var parameters = SignInExtensions.IdTokenValidation(new SignInOptions { Authority = Issuer + "/", ClientId = ClientId });

        parameters.ValidAlgorithms.Should().Equal(SecurityAlgorithms.RsaSha256);
        parameters.ValidIssuer.Should().Be(Issuer);
        parameters.ValidAudience.Should().Be(ClientId);
        parameters.RequireSignedTokens.Should().BeTrue();
        parameters.ValidateLifetime.Should().BeTrue();
    }

    private string Token(
        string audience = ClientId,
        string issuer = Issuer,
        DateTime? expires = null,
        SigningCredentials? signing = null,
        string typ = "ID",
        string? account = AccountId)
    {
        var claims = new Dictionary<string, object> { ["sub"] = Subject, ["typ"] = typ, ["roles"] = new[] { "owner" } };
        if (account is not null)
        {
            claims["account_id"] = account;
        }

        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.AddMinutes(-10),
            NotBefore = now.AddMinutes(-10),
            Expires = expires ?? now.AddMinutes(5),
            Claims = claims,
            SigningCredentials = signing ?? new SigningCredentials(new RsaSecurityKey(_realmKey) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        });
    }

    private static string Unsigned(string token)
    {
        var parts = token.Split('.');
        return $"{Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""")}.{parts[1]}.";
    }
}
