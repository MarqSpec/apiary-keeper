using ApiaryCopilot.Api.SignIn;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// SignIn__* (DEPLOYMENT.md §3): unset, there is no sign-in and nothing changes; set, every setting is checked at
// startup, and a failure names the setting, never its value.
public sealed class SignInOptionsTests
{
    private const string Secret = "synthetic-unit-test-client-secret-value";

    [Fact] // boundary: with nothing set, sign-in is off and that is valid (staging and production until piece 5)
    public void Validate_ShouldPassWithSignInOff_WhenNothingIsSet()
    {
        var options = new SignInOptions();

        options.IsConfigured.Should().BeFalse();
        options.Validate().Should().BeEmpty();
    }

    [Fact] // regression risk: a complete https configuration is valid
    public void Validate_ShouldPass_WhenEverySettingIsSet()
    {
        Configured().Validate().Should().BeEmpty();
    }

    [Theory] // invariant: a half-set configuration stops the host rather than leaving sign-in silently off
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("Audience")]
    public void Validate_ShouldFailNamingTheAuthority_WhenAnotherSettingIsSetWithoutIt(string setting)
    {
        var options = new SignInOptions();
        typeof(SignInOptions).GetProperty(setting)!.SetValue(options, "set");

        options.Validate().Should().ContainSingle().Which.Should().Contain("SignIn__Authority");
    }

    [Theory] // boundary: with the authority set, each of the others is required
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("Audience")]
    public void Validate_ShouldFailNamingTheSetting_WhenItIsMissing(string setting)
    {
        var options = Configured();
        typeof(SignInOptions).GetProperty(setting)!.SetValue(options, " ");

        options.Validate().Should().ContainSingle().Which.Should().Contain($"SignIn__{setting}");
    }

    [Theory] // invariant: the issuer is reached over https unless http is explicitly allowed (local and tests only)
    [InlineData("http://keycloak.example/realms/apiary-keeper")]
    [InlineData("keycloak.example/realms/apiary-keeper")]
    [InlineData("https://keycloak.example/realms/apiary-keeper?x=1")]
    [InlineData("https://user:pass@keycloak.example/realms/apiary-keeper")]
    [InlineData("https://keycloak.example/realms/apiary-keeper#f")]
    public void Validate_ShouldFailNamingTheAuthority_WhenItIsNotAnHttpsUrl(string authority)
    {
        var options = Configured();
        options.Authority = authority;

        options.Validate().Should().ContainSingle().Which.Should().Contain("SignIn__Authority");
    }

    [Fact] // boundary: http is accepted only with RequireHttps off
    public void Validate_ShouldPass_WhenTheAuthorityIsHttpAndHttpsIsNotRequired()
    {
        var options = Configured();
        options.Authority = "http://127.0.0.1:8180/realms/apiary-keeper-dev";
        options.RequireHttps = false;

        options.Validate().Should().BeEmpty();
    }

    [Theory] // boundary: the back-channel discovery address, when set, is an https URL unless http is allowed
    [InlineData("http://keycloak.railway.internal:8080/realms/apiary-keeper/.well-known/openid-configuration", true)]
    [InlineData("not a url", false)]
    [InlineData("https://user:pass@keycloak.example/realms/apiary-keeper/.well-known/openid-configuration", false)]
    public void Validate_ShouldFailNamingTheMetadataAddress_WhenItIsNotAUsableUrl(string address, bool requireHttps)
    {
        var options = Configured();
        options.RequireHttps = requireHttps;
        options.Authority = requireHttps ? options.Authority : "http://127.0.0.1:8180/realms/apiary-keeper-dev";
        options.MetadataAddress = address;

        options.Validate().Should().ContainSingle().Which.Should().Contain("SignIn__MetadataAddress");
    }

    [Fact] // boundary: an http back-channel address is accepted with RequireHttps off (a container reaching the test Keycloak)
    public void Validate_ShouldPass_WhenTheMetadataAddressIsHttpAndHttpsIsNotRequired()
    {
        var options = Configured();
        options.RequireHttps = false;
        options.MetadataAddress = "http://apiary-it-kc-5000:8080/realms/apiary-keeper-dev/.well-known/openid-configuration";

        options.Validate().Should().BeEmpty();
    }

    [Theory] // boundary: the session's idle timeout and maximum lifetime stay within sane bounds, the maximum not below the idle
    [InlineData("00:00:59", "12:00:00")]
    [InlineData("1.00:00:01", "2.00:00:00")]
    [InlineData("00:30:00", "00:29:00")]
    [InlineData("00:30:00", "7.00:00:01")]
    public void Validate_ShouldFailNamingTheSessionSetting_WhenItIsOutOfBounds(string idle, string max)
    {
        var options = Configured();
        options.SessionIdleTimeout = TimeSpan.Parse(idle, System.Globalization.CultureInfo.InvariantCulture);
        options.SessionMaxLifetime = TimeSpan.Parse(max, System.Globalization.CultureInfo.InvariantCulture);

        options.Validate().Should().ContainSingle().Which.Should().MatchRegex("SignIn__Session(IdleTimeout|MaxLifetime)");
    }

    [Theory] // boundary: the clock skew allowed on token lifetimes is between none and five minutes
    [InlineData(-1)]
    [InlineData(301)]
    public void Validate_ShouldFailNamingTheClockSkew_WhenItIsOutOfBounds(int seconds)
    {
        var options = Configured();
        options.ClockSkew = TimeSpan.FromSeconds(seconds);

        options.Validate().Should().ContainSingle().Which.Should().Contain("SignIn__ClockSkew");
    }

    [Fact] // invariant: a failure message never carries the secret's value
    public void Validate_ShouldNeverEchoTheSecret_WhenAnotherSettingFails()
    {
        var options = Configured();
        options.Authority = "not a url " + Secret;

        options.Validate().Should().NotContain(m => m.Contains(Secret, StringComparison.Ordinal));
    }

    [Theory] // regression risk: the issuer the API checks is the authority without a trailing slash, as Keycloak writes it
    [InlineData("https://keycloak.example/realms/apiary-keeper")]
    [InlineData("https://keycloak.example/realms/apiary-keeper/")]
    public void Issuer_ShouldBeTheAuthorityWithoutATrailingSlash_WhenSet(string authority)
    {
        var options = Configured();
        options.Authority = authority;

        options.Issuer.Should().Be("https://keycloak.example/realms/apiary-keeper");
    }

    [Theory] // invariant: the session cookie takes the __Host- prefix (Secure, path /, no domain) whenever https is required
    [InlineData(true, "__Host-apiary-keeper-session")]
    [InlineData(false, "apiary-keeper-session")]
    public void SessionCookieName_ShouldCarryTheHostPrefix_WhenHttpsIsRequired(bool requireHttps, string expected)
    {
        var options = Configured();
        options.RequireHttps = requireHttps;

        options.SessionCookieName.Should().Be(expected);
    }

    private static SignInOptions Configured() => new()
    {
        Authority = "https://keycloak.example/realms/apiary-keeper",
        ClientId = "apiary-keeper-bff",
        ClientSecret = Secret,
        Audience = "apiary-keeper-api",
    };
}
