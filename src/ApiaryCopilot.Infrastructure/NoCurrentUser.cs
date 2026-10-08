using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

// The default until authentication supplies the caller: no user, so nothing can be written.
internal sealed class NoCurrentUser : ICurrentUser
{
    public UserId? UserId => null;
}
