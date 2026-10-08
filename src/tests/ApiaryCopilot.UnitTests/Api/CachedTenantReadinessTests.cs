using ApiaryCopilot.Api.Accounts;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.UnitTests.Api;

// A found acting user is remembered briefly, per account and subject, so the lookup costs nothing per request; a refusal
// is never remembered, so a user set up later is served on the next request without a restart.
public sealed class CachedTenantReadinessTests
{
    private const string Subject = "4e97dd9a-9f79-4493-8185-795ac0034b7d";
    private static readonly DateTimeOffset Start = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly AccountId Account = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly AccountId OtherAccount = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly UserId User = new(Guid.Parse("33333333-3333-4333-8333-333333333333"));

    private readonly ITenantDirectory _directory = A.Fake<ITenantDirectory>();
    private readonly TimeProvider _clock = A.Fake<TimeProvider>();
    private readonly ReadyTenants _ready = new();
    private DateTimeOffset _now = Start;

    public CachedTenantReadinessTests()
    {
        A.CallTo(() => _clock.GetUtcNow()).ReturnsLazily(() => _now);
        A.CallTo(() => _directory.FindActingUserAsync(A<string>._, A<CancellationToken>._)).Returns(TenantLookup.Ready(User));
    }

    [Fact] // invariant: the database is asked once while a ready answer is fresh
    public async Task ResolveAsync_ShouldAskTheDirectoryOnce_WhenTheReadyAnswerIsStillFresh()
    {
        await Readiness().ResolveAsync(Account, Subject, CancellationToken.None);
        _now = Start + CachedTenantReadiness.ReadyLifetime - TimeSpan.FromSeconds(1);

        var lookup = await Readiness().ResolveAsync(Account, Subject, CancellationToken.None);

        lookup.Should().Be(TenantLookup.Ready(User));
        A.CallTo(() => _directory.FindActingUserAsync(Subject, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact] // boundary: a ready answer expires, so a row removed or a subject cleared later is noticed
    public async Task ResolveAsync_ShouldAskTheDirectoryAgain_WhenTheReadyAnswerHasExpired()
    {
        await Readiness().ResolveAsync(Account, Subject, CancellationToken.None);
        _now = Start + CachedTenantReadiness.ReadyLifetime;

        await Readiness().ResolveAsync(Account, Subject, CancellationToken.None);

        A.CallTo(() => _directory.FindActingUserAsync(Subject, A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact] // invariant: an answer for one account is never reused for another, even for the same subject
    public async Task ResolveAsync_ShouldAskTheDirectory_WhenTheSameSubjectComesWithAnotherAccount()
    {
        await Readiness().ResolveAsync(Account, Subject, CancellationToken.None);
        A.CallTo(() => _directory.FindActingUserAsync(A<string>._, A<CancellationToken>._)).Returns(TenantLookup.AccountMissing);

        var lookup = await Readiness().ResolveAsync(OtherAccount, Subject, CancellationToken.None);

        lookup.Should().Be(TenantLookup.AccountMissing);
    }

    [Fact] // invariant: an answer for one subject is never reused for another in the same account
    public async Task ResolveAsync_ShouldAskTheDirectory_WhenAnotherSubjectComesWithTheSameAccount()
    {
        await Readiness().ResolveAsync(Account, Subject, CancellationToken.None);
        A.CallTo(() => _directory.FindActingUserAsync(A<string>._, A<CancellationToken>._)).Returns(TenantLookup.SubjectUnknown);

        var lookup = await Readiness().ResolveAsync(Account, "another-synthetic-subject", CancellationToken.None);

        lookup.Should().Be(TenantLookup.SubjectUnknown);
    }

    [Theory] // invariant: a refusal is never remembered, so a user set up later is served on the next request
    [InlineData(TenantStatus.AccountMissing)]
    [InlineData(TenantStatus.SubjectUnknown)]
    public async Task ResolveAsync_ShouldPickUpTheUser_WhenItIsSetUpAfterARefusal(TenantStatus failure)
    {
        A.CallTo(() => _directory.FindActingUserAsync(A<string>._, A<CancellationToken>._))
            .Returns(new TenantLookup(failure, null)).Once().Then.Returns(TenantLookup.Ready(User));

        var first = await Readiness().ResolveAsync(Account, Subject, CancellationToken.None);
        var second = await Readiness().ResolveAsync(Account, Subject, CancellationToken.None);

        first.Status.Should().Be(failure);
        second.Should().Be(TenantLookup.Ready(User));
    }

    // One per request, as the host registers it, over the one shared cache.
    private CachedTenantReadiness Readiness() => new(_directory, _ready, _clock);
}
