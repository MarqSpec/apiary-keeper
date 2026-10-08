using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Domain;

public sealed class ColonyIdTests
{
    [Fact] // boundary: an empty id names no colony, so it is refused at construction
    public void Constructor_ShouldThrowValidationError_WhenValueIsEmpty()
    {
        var act = () => new ColonyId(Guid.Empty);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith("Validation:");
    }

    [Fact] // invariant: two ids are the same colony exactly when their values are equal
    public void Equals_ShouldBeTrue_WhenValuesAreEqual()
    {
        var value = Guid.Parse("5a4b3c2d-1e0f-4a9b-8c7d-6e5f4a3b2c1d");

        new ColonyId(value).Should().Be(new ColonyId(value));
    }

    [Fact] // invariant: every new colony gets its own, non-empty id
    public void New_ShouldReturnDistinctNonEmptyIds_WhenCalledTwice()
    {
        var first = ColonyId.New();
        var second = ColonyId.New();

        first.Value.Should().NotBeEmpty();
        first.Should().NotBe(second);
    }
}
