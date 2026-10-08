using ApiaryCopilot.Domain;
using Npgsql;

namespace ApiaryCopilot.IntegrationTests;

// The Users.ExternalSubject column as the committed migration leaves it in the real PostgreSQL: optional, so users with no
// subject yet are many, and unique across every account, so one Keycloak subject can never be two users. The unit tier
// checks the model; only the database can show the index the migration created. Deployment=None: loopback containers.
[Trait("Deployment", "None")]
[Collection(SharedServer.Name)]
public sealed class UserSubjectSchemaTests(AppServerFixture server)
{
    [Fact] // invariant: a subject already held by a user, in any account, cannot be given to another
    public async Task Users_ShouldRefuseASecondUserWithTheSameSubject_WhenItIsInAnotherAccount()
    {
        var subject = $"synthetic-subject-{Guid.NewGuid():N}";
        await server.Database.AddUserAsync(server.OtherAccountId, new UserId(Guid.NewGuid()), subject);

        var second = () => server.Database.AddUserAsync(server.CurrentAccountId, new UserId(Guid.NewGuid()), subject);

        (await second.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact] // boundary: users with no subject yet (invited, not signed in) are not duplicates of one another
    public async Task Users_ShouldAcceptManyUsersWithoutASubject_WhenNoneHasSignedInYet()
    {
        await server.Database.AddUserAsync(server.CurrentAccountId, new UserId(Guid.NewGuid()), null);

        var another = () => server.Database.AddUserAsync(server.CurrentAccountId, new UserId(Guid.NewGuid()), null);

        await another.Should().NotThrowAsync();
    }
}
