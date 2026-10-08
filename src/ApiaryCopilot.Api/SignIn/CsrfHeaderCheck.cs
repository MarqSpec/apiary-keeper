using ApiaryCopilot.Api.Problems;
using Microsoft.Extensions.Primitives;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>
/// The custom-header CSRF check on writes (D-19): a request that carries the session cookie and is not a safe method
/// must also carry <c>X-CSRF: 1</c>. A browser sends a custom header only on a same-origin request, since a cross-site
/// one needs a CORS preflight the API never grants, so another site cannot make a signed-in browser write. A request
/// without the cookie - an agent's bearer call, or an anonymous one - is not this check's concern.
/// </summary>
public static class CsrfHeaderCheck
{
    /// <summary>The header a write with the session cookie must carry.</summary>
    public const string HeaderName = "X-CSRF";

    /// <summary>Its only accepted value.</summary>
    public const string HeaderValue = "1";

    /// <summary>Whether the request may go on.</summary>
    public static bool IsAllowed(string method, bool hasSessionCookie, StringValues header) =>
        !hasSessionCookie
        || HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)
        || (header.Count == 1 && string.Equals(header[0], HeaderValue, StringComparison.Ordinal));

    /// <summary>Refuses, with a 403 <c>NotAllowed</c> problem, a write that carries <paramref name="cookieName"/> without the header.</summary>
    public static IApplicationBuilder UseCsrfHeaderCheck(this IApplicationBuilder app, string cookieName) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (IsAllowed(request.Method, request.Cookies.ContainsKey(cookieName), request.Headers[HeaderName]))
            {
                await next(context);
                return;
            }

            await ProblemResults.NotAllowed($"a write with the session cookie must carry the {HeaderName}: {HeaderValue} header; the request was refused.")
                .ExecuteAsync(context);
        });
}
