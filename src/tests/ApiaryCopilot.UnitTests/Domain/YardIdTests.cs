using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Domain;

public sealed class YardIdTests
{
    [Fact] // boundary: an empty id names no yard, so it is refused at construction
    public void Constructor_ShouldThrowValidationError_WhenValueIsEmpty()
    {
        var act = () => new YardId(Guid.Empty);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().StartWith("Validation:");
    }

    [Fact] // invariant: two ids are the same yard exactly when their values are equal
    public void Equals_ShouldBeTrue_WhenValuesAreEqual()
    {
        var value = Guid.Parse("5a4b3c2d-1e0f-4a9b-8c7d-6e5f4a3b2c1d");

        new YardId(value).Should().Be(new YardId(value));
    }

    [Fact] // invariant: every new yard gets its own, non-empty id
    public void New_ShouldReturnDistinctNonEmptyIds_WhenCalledTwice()
    {
        var first = YardId.New();
        var second = YardId.New();

        first.Value.Should().NotBeEmpty();
        first.Should().NotBe(second);
    }
}
