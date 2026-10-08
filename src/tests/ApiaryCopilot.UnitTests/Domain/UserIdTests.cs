using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Domain;

public sealed class UserIdTests
{
    [Fact] // boundary: an empty id would stamp a row with nobody
    public void Constructor_ShouldThrowValidationError_WhenValueIsEmpty()
    {
        var act = () => new UserId(Guid.Empty);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith("Validation:");
    }

    [Fact] // invariant: two ids are the same user exactly when their values are equal
    public void Equals_ShouldBeTrue_WhenValuesAreEqual()
    {
        var value = Guid.Parse("0f1e2d3c-4b5a-4968-8776-655443322110");

        new UserId(value).Should().Be(new UserId(value));
    }
}
