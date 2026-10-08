using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// The acting user every store test writes as, so its rows can be stamped (IRowStamped).
internal static class TestUsers
{
    public static readonly UserId Acting = new(Guid.Parse("33333333-3333-4333-8333-333333333333"));

    public static ICurrentUser Current(UserId? user)
    {
        var currentUser = A.Fake<ICurrentUser>();
        A.CallTo(() => currentUser.UserId).Returns(user);
        return currentUser;
    }
}
