using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Domain;

public sealed class AccountIdTests
{
    [Fact] // boundary: an empty id would match rows written without an account
    public void Constructor_ShouldThrowValidationError_WhenValueIsEmpty()
    {
        var act = () => new AccountId(Guid.Empty);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith("Validation:");
    }

    [Fact] // invariant: two ids are the same account exactly when their values are equal
    public void Equals_ShouldBeTrue_WhenValuesAreEqual()
    {
        var value = Guid.Parse("0f1e2d3c-4b5a-4968-8776-655443322110");

        new AccountId(value).Should().Be(new AccountId(value));
    }
}
