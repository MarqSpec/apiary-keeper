using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApiaryCopilot.Api.SignIn;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.Api.Invitations;

/// <summary>A user to create and invite: the address, the name shown, the one role and the account (D-22).</summary>
public sealed record KeycloakInvitation(string Email, string Name, MemberRole Role, Guid AccountId);

/// <summary>The outcome of an invitation through Keycloak's admin interface.</summary>
public abstract record InviteOutcome
{
    private InviteOutcome()
    {
    }

    /// <summary>Keycloak created the user, gave it its role and sent the invitation; its id is the <c>sub</c> its tokens will carry.</summary>
    public sealed record Invited(string UserId) : InviteOutcome;

    /// <summary>Keycloak already has a user with that address or user name; nothing was created.</summary>
    public sealed record AlreadyExists : InviteOutcome;

    /// <summary>A step failed (<c>token</c>, <c>create</c>, <c>role</c> or <c>invitation</c>); a user it created was removed.</summary>
    public sealed record Failed(string Step, string Reason) : InviteOutcome;
}

/// <summary>The API's calls to Keycloak's admin interface as its service account (D-22).</summary>
public interface IKeycloakAdmin
{
    /// <summary>Whether the service account is configured; unconfigured, nothing can be invited.</summary>
    bool IsConfigured { get; }

    /// <summary>Creates the user with its account and role and sends Keycloak's invitation email.</summary>
    Task<InviteOutcome> InviteAsync(KeycloakInvitation invitation, CancellationToken cancellationToken);

    /// <summary>Removes a user an invitation created, when the API could not record it; whether Keycloak confirmed it.</summary>
    Task<bool> WithdrawAsync(string userId, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IKeycloakAdmin"/> over Keycloak's admin REST interface with the service account's client-credentials
/// token, fetched per call. No retry: creating a user is not idempotent, so a retried request whose first answer was
/// lost would find the user already there; a failure is reported once. A step that fails after the user exists removes
/// it again. Each HTTP call is bounded by <see cref="CallTimeout"/>. Only a step and a status are logged - never a
/// token, the secret, an address or a name.
/// </summary>
public sealed partial class KeycloakAdminClient(
    HttpClient http,
    IOptions<KeycloakAdminOptions> options,
    IOptions<SignInOptions> signIn,
    ILogger<KeycloakAdminClient> logger,
    TimeProvider clock) : IKeycloakAdmin
{
    /// <summary>How long one call to Keycloak may take.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    // The invitation: Keycloak's own email, verifying the address and setting the first password.
    private static readonly string[] InvitationActions = ["VERIFY_EMAIL", "UPDATE_PASSWORD"];

    private readonly KeycloakAdminOptions _options = options.Value;

    /// <inheritdoc />
    public bool IsConfigured => _options.IsConfigured;

    /// <inheritdoc />
    public async Task<InviteOutcome> InviteAsync(KeycloakInvitation invitation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        var endpoints = Endpoints();
        var token = await TokenAsync(endpoints, cancellationToken);
        if (token.Failure is { } refused)
        {
            return Log(refused);
        }

        var created = await CreateAsync(endpoints, token.Value!, invitation, cancellationToken);
        if (created.Outcome is { } notCreated)
        {
            return Log(notCreated);
        }

        var userId = created.UserId!;
        var role = RoleName(invitation.Role);
        var roles = new[] { new RoleReference(_options.RoleIdOf(invitation.Role), role) };
        var failure = await SendAsync(HttpMethod.Post, new Uri(endpoints.Admin, $"users/{userId}/role-mappings/realm"), token.Value!,
                JsonContent.Create(roles, KeycloakAdminJsonContext.Default.RoleReferenceArray), "role", cancellationToken)
            ?? await SendAsync(HttpMethod.Put, new Uri(endpoints.Admin, string.Create(CultureInfo.InvariantCulture,
                    $"users/{userId}/execute-actions-email?lifespan={(long)_options.InvitationLifetime.TotalSeconds}")), token.Value!,
                JsonContent.Create(InvitationActions, KeycloakAdminJsonContext.Default.StringArray), "invitation", cancellationToken);
        if (failure is not null)
        {
            // The token is still good: it was issued moments ago.
            await DeleteAsync(endpoints, token.Value!, userId, CancellationToken.None);
            return Log(failure);
        }

        LogInvited(logger, role);
        return new InviteOutcome.Invited(userId);
    }

    /// <inheritdoc />
    public async Task<bool> WithdrawAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var endpoints = Endpoints();
        var token = await TokenAsync(endpoints, cancellationToken);
        return token.Value is { } value && await DeleteAsync(endpoints, value, userId, cancellationToken);
    }

    private static string RoleName(MemberRole role) => role switch
    {
        MemberRole.Helper => "helper",
        MemberRole.Grower => "grower",
        MemberRole.Customer => "customer",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not a member role."),
    };

    private static string StatusOf(HttpStatusCode status) => string.Create(CultureInfo.InvariantCulture, $"http_{(int)status}");

    private KeycloakAdminEndpoints Endpoints() =>
        KeycloakAdminOptions.EndpointsFor(signIn.Value)
        ?? throw new InvalidOperationException("The sign-in authority is not a Keycloak realm URL.");

    private InviteOutcome Log(InviteOutcome outcome)
    {
        if (outcome is InviteOutcome.Failed failed)
        {
            LogFailed(logger, failed.Step, failed.Reason);
        }

        return outcome;
    }

    private async Task<(string? Value, InviteOutcome.Failed? Failure)> TokenAsync(KeycloakAdminEndpoints endpoints, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(CallTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId!.Trim(),
                ["client_secret"] = _options.ClientSecret!,
            });
            using var response = await http.PostAsync(endpoints.Token, form, linked.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return (null, new InviteOutcome.Failed("token", StatusOf(response.StatusCode)));
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(linked.Token));
            return json.RootElement.TryGetProperty("access_token", out var accessToken)
                   && accessToken.ValueKind == JsonValueKind.String && accessToken.GetString() is { Length: > 0 } value
                ? (value, null)
                : (null, new InviteOutcome.Failed("token", "malformed_response"));
        }
        catch (JsonException)
        {
            return (null, new InviteOutcome.Failed("token", "malformed_response"));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return (null, new InviteOutcome.Failed("token", e.GetType().Name));
        }
    }

    private async Task<(string? UserId, InviteOutcome? Outcome)> CreateAsync(
        KeycloakAdminEndpoints endpoints, string token, KeycloakInvitation invitation, CancellationToken cancellationToken)
    {
        var user = new NewUser(
            invitation.Email,
            invitation.Email,
            invitation.Name,
            Enabled: true,
            EmailVerified: false,
            new Dictionary<string, string[]> { ["account_id"] = [invitation.AccountId.ToString("D")] });
        using var timeout = new CancellationTokenSource(CallTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = Request(HttpMethod.Post, new Uri(endpoints.Admin, "users"), token, JsonContent.Create(user, KeycloakAdminJsonContext.Default.NewUser));
            using var response = await http.SendAsync(request, linked.Token);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return (null, new InviteOutcome.AlreadyExists());
            }

            if (response.StatusCode != HttpStatusCode.Created)
            {
                return (null, new InviteOutcome.Failed("create", StatusOf(response.StatusCode)));
            }

            // Keycloak answers with the new user's address; its last segment is the id, which is the user's sub.
            var id = response.Headers.Location?.Segments is { Length: > 0 } segments ? Uri.UnescapeDataString(segments[^1].TrimEnd('/')) : null;
            return Guid.TryParse(id, out _)
                ? (id, null)
                : (null, new InviteOutcome.Failed("create", "malformed_response"));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return (null, new InviteOutcome.Failed("create", e.GetType().Name));
        }
    }

    private async Task<InviteOutcome.Failed?> SendAsync(
        HttpMethod method, Uri uri, string token, HttpContent content, string step, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(CallTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = Request(method, uri, token, content);
            using var response = await http.SendAsync(request, linked.Token);
            return response.IsSuccessStatusCode ? null : new InviteOutcome.Failed(step, StatusOf(response.StatusCode));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return new InviteOutcome.Failed(step, e.GetType().Name);
        }
    }

    private async Task<bool> DeleteAsync(KeycloakAdminEndpoints endpoints, string token, string userId, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(CallTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = Request(HttpMethod.Delete, new Uri(endpoints.Admin, $"users/{Uri.EscapeDataString(userId)}"), token, content: null);
            using var response = await http.SendAsync(request, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogFailed(logger, "withdraw", StatusOf(response.StatusCode));
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            LogFailed(logger, "withdraw", e.GetType().Name);
            return false;
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri uri, string token, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak's admin interface refused or failed the invitation's {Step} step: {Reason}.")]
    private static partial void LogFailed(ILogger logger, string step, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Keycloak created and invited a {Role}.")]
    private static partial void LogInvited(ILogger logger, string role);
}

/// <summary>The service account when <c>KeycloakAdmin__*</c> is unset: nothing can be invited.</summary>
public sealed class UnconfiguredKeycloakAdmin : IKeycloakAdmin
{
    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public Task<InviteOutcome> InviteAsync(KeycloakInvitation invitation, CancellationToken cancellationToken) =>
        Task.FromResult<InviteOutcome>(new InviteOutcome.Failed("token", "not_configured"));

    /// <inheritdoc />
    public Task<bool> WithdrawAsync(string userId, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>A user as Keycloak's admin interface creates it: no role, group or credential.</summary>
internal sealed record NewUser(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("firstName")] string FirstName,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("emailVerified")] bool EmailVerified,
    [property: JsonPropertyName("attributes")] Dictionary<string, string[]> Attributes);

/// <summary>A realm role by id and name, as Keycloak's role mapping takes it.</summary>
internal sealed record RoleReference(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

[JsonSerializable(typeof(NewUser))]
[JsonSerializable(typeof(RoleReference[]))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class KeycloakAdminJsonContext : JsonSerializerContext;
