using ApiaryCopilot.Api.Health;

namespace ApiaryCopilot.UnitTests;

public class AppVersionTests
{
    [Theory]
    [InlineData("0.1.0+3f2a9c1d", "0.1.0")]
    [InlineData("1.2.3+build.5", "1.2.3")]
    [InlineData("1.2.3-rc.1+abc", "1.2.3-rc.1")]
    public void Normalize_ShouldDropBuildMetadata_WhenVersionCarriesAPlusSuffix(string raw, string expected)
    {
        AppVersion.Normalize(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Normalize_ShouldReturnUnknown_WhenVersionIsMissing(string? raw)
    {
        AppVersion.Normalize(raw).Should().Be("unknown");
    }

    [Fact]
    public void Current_ShouldBeSemVerXYZ_WhenBuiltFromDirectoryBuildProps()
    {
        AppVersion.Current.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }
}
