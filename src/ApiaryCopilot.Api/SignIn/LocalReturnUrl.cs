namespace ApiaryCopilot.Api.SignIn;

/// <summary>
/// Where sign-in sends the browser back: a path on this host, or the root. A PWA screen in the fragment (D-20) is
/// kept, so <c>/api/auth/login?returnUrl=%2F%23%2Fyards%2F7</c> comes back to that screen; anything that could leave the
/// host is dropped, so the login link is never an open redirect.
/// </summary>
public static class LocalReturnUrl
{
    /// <summary>The longest return URL carried through sign-in.</summary>
    public const int MaxLength = 2048;

    /// <summary>The return URL when it is a local path, otherwise <c>/</c>.</summary>
    public static string Sanitize(string? returnUrl) =>
        returnUrl is { Length: > 0 and <= MaxLength }
        && returnUrl[0] == '/'
        && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
        && !returnUrl.Any(char.IsControl)
            ? returnUrl
            : "/";
}
