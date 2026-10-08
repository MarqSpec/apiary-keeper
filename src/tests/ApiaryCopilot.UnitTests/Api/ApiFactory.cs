using System.Security.Claims;
using ApiaryCopilot.Api.SignIn;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ApiaryCopilot.UnitTests.Api;

// The real host in memory (CONVENTIONS.md §8.1 "through the interface"): no process, network or database.
// The migrator is faked, and the tenant directory finds the caller's user (TenantDirectory), so a Development host starts
// without PostgreSQL; tests fake anything else they reach. With sign-in off, a test that needs a signed-in caller passes
// one (caller), set on every request as the authenticated principal would be; with sign-in on (SignInHost) it signs in.
internal sealed class ApiFactory(
    string environment = "Production",
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? services = null,
    ClaimsPrincipal? caller = null) : WebApplicationFactory<Program>
{
    public const string CallerSubject = "4e97dd9a-9f79-4493-8185-795ac0034b7d";

    public static readonly UserId ActingUserId = new(Guid.Parse("33333333-3333-4333-8333-333333333333"));

    /// <summary>The tenant directory every host uses: by default it finds the caller as <see cref="ActingUserId"/>.</summary>
    public ITenantDirectory TenantDirectory { get; } = Ready(A.Fake<ITenantDirectory>());

    /// <summary>A signed-in caller with the given role in the given account, as either sign-in path yields it.</summary>
    public static ClaimsPrincipal SignedIn(string accountId = "11111111-1111-4111-8111-111111111111", string role = "owner", string subject = CallerSubject) =>
        ApiaryClaims.FromToken([new Claim("sub", subject), new Claim("account_id", accountId), new Claim("roles", role)])!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(s =>
        {
            s.AddScoped(_ => A.Fake<IDatabaseMigrator>());
            s.AddScoped(_ => TenantDirectory);
            if (caller is not null)
            {
                s.AddSingleton<IStartupFilter>(new CallerStartupFilter(caller));
            }

            services?.Invoke(s);
        });
    }

    private static ITenantDirectory Ready(ITenantDirectory directory)
    {
        A.CallTo(() => directory.FindActingUserAsync(A<string>._, A<CancellationToken>._)).Returns(TenantLookup.Ready(ActingUserId));
        return directory;
    }

    private sealed class CallerStartupFilter(ClaimsPrincipal caller) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.User = caller;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
