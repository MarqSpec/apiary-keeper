using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

// The default until authentication supplies the caller's account: no account, so queries return nothing.
internal sealed class NoCurrentAccount : ICurrentAccount
{
    public AccountId? AccountId => null;
}
