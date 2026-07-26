using System.Net;

namespace RemSound.Core;

/// <summary>
/// ONE home for "which selected peers do we actually stream to". The app and the send-only service
/// carried near-identical private copies: prune any peer whose heartbeat has read Unreachable for
/// longer than the grace window (never stream into a dead address — a-singer's issue #8), re-arm the
/// moment it answers again. The app's extra rule — a peer we're actively RECEIVING audio from stays
/// armed even when its heartbeat can't round-trip (asymmetric path; a working stream must never be
/// cut) — is injected as the <c>keepAnyway</c> predicate; the send-only service passes none.
/// </summary>
public static class PeerArming
{
    public static IPEndPoint[] ComputeArmedEndpoints(
        IReadOnlyList<IPEndPoint> all,
        IReadOnlyList<PeerHealth> health,
        TimeSpan pruneAfter,
        Func<IPAddress, bool>? keepAnyway = null)
    {
        HashSet<string>? dead = null;
        foreach (var ph in health)
        {
            if (ph.State == PeerHealthState.Unreachable
                && ph.AgeOfLastPong is { } age
                && age > pruneAfter
                && keepAnyway?.Invoke(ph.AudioEndpoint.Address) != true)
            {
                (dead ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    .Add($"{ph.AudioEndpoint.Address}:{ph.AudioEndpoint.Port}");
            }
        }
        return dead is null ? all.ToArray() : all.Where(ep => !dead.Contains($"{ep.Address}:{ep.Port}")).ToArray();
    }

    /// <summary>Order-independent identity of an armed set. Both sides re-arm the sender only when this
    /// changes, so churn in list ORDER never causes a pointless receiver reset.</summary>
    public static string Signature(IEnumerable<IPEndPoint> endpoints) =>
        string.Join("|", endpoints.Select(ep => $"{ep.Address}:{ep.Port}").OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
}
