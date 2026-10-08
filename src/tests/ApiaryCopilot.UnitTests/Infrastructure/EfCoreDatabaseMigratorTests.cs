using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.UnitTests.Infrastructure;

public sealed class EfCoreDatabaseMigratorTests
{
    [Fact] // boundary: a missing setting names the variable to set, before anything tries to connect
    public async Task MigrateAsync_ShouldThrowNamingTheVariable_WhenTheConnectionStringIsMissing()
    {
        var options = new DbContextOptionsBuilder<ApiaryCopilotDbContext>().UseNpgsql().Options;
        await using var db = new ApiaryCopilotDbContext(options, A.Fake<ICurrentAccount>(), A.Fake<ICurrentUser>(), TimeProvider.System);
        var sut = new EfCoreDatabaseMigrator(db);

        var act = () => sut.MigrateAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("ConnectionStrings__ApiaryCopilot");
    }
}
