using ApiaryCopilot.Api.Colonies;
using ApiaryCopilot.Api.Frontend;
using ApiaryCopilot.Api.Health;
using ApiaryCopilot.Api.Inspections;
using ApiaryCopilot.Api.Invitations;
using ApiaryCopilot.Api.Media;
using ApiaryCopilot.Api.Accounts;
using ApiaryCopilot.Api.Proxy;
using ApiaryCopilot.Api.SignIn;
using ApiaryCopilot.Api.Yards;
using ApiaryCopilot.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

// Metrics for Prometheus at GET /metrics.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("api"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
// The document names the product by its external name, Apiary-Keeper; ApiaryCopilot is the internal codename
// ().
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Apiary-Keeper API";
    document.Info.Description = "Apiary-Keeper is an offline-first web app for owner-operator Texas apiarists that tracks yards, hives, inspections, treatments, harvests, and sales, and includes a Telegram family chat, a customer storefront, and AI agents planned for a later phase.";
    return Task.CompletedTask;
}));

// Unexpected exceptions become a bare 500 problem with no detail (CONVENTIONS.md §4.3).
builder.Services.AddProblemDetails();

// The nginx proxy's X-Forwarded-For, -Proto and -Host (D-16).
builder.Services.AddProxyForwardedHeaders();

// Sign-in through Keycloak, the BFF in the API (D-19): registered only when SignIn__Authority is set (DEPLOYMENT.md §3).
// Every apiary endpoint is authorized from the signed-in principal; with sign-in off they answer 503.
builder.Services.AddSignIn(builder.Configuration);
builder.Services.AddSignInContracts();

builder.Services.AddYards();
builder.Services.AddColonies();
builder.Services.AddInspections();

// An owner invites helpers and growers through the API's Keycloak service account (D-22): registered only when
// KeycloakAdmin__ClientId is set (DEPLOYMENT.md §3); otherwise the invitation answers 503.
builder.Services.AddInvitations(builder.Configuration);

// Photos and videos go phone to bucket on presigned URLs (D-13); the bucket settings come from Media__* (DEPLOYMENT.md §3).
builder.Services.AddMedia(builder.Configuration);

// The account from the token's account_id, and the user every row is stamped with from its sub (D-19; the D-18
// configuration stopgap removed). 
builder.Services.AddSignedInTenant();
builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString(InfrastructureServiceCollectionExtensions.ConnectionStringName));

var app = builder.Build();

// Configure the HTTP request pipeline. Forwarded headers first, before any middleware that reads the client address,
// scheme or host - the exception handler included - so they all see the real client.
app.UseProxyForwardedHeaders();

app.UseExceptionHandler();

await app.Services.MigrateDatabaseInDevelopmentAsync(app.Environment, app.Lifetime.ApplicationStopping);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// After the forwarded headers, so the callback URL and the cookies' Secure flag follow the client's scheme and host.
app.UseSignIn();

app.MapHealth();

app.MapSignIn();

app.MapYards();

app.MapColonies();

app.MapInspections();

app.MapMedia();

app.MapInvitations();

// The built PWA from wwwroot, index.html at / (D-5).
app.UseFrontend();

app.MapPrometheusScrapingEndpoint();

app.Run();
