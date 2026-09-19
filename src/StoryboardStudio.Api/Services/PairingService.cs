using StoryboardStudio.Core;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace StoryboardStudio.Api.Services;

public sealed class PairingService(TimeProvider timeProvider, IConfiguration configuration)
{
    public const string CookieName = "storyboard_studio_session";
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AttemptWindow> attempts = new(StringComparer.Ordinal);
    private string code = CreateCode();
    private DateTimeOffset codeExpiresAt = timeProvider.GetUtcNow().AddMinutes(10);

    public bool LanEnabled => configuration.GetValue("Studio:AllowLan", false);

    public PairingStatusSummary Status(bool loopback, bool paired)
    {
        lock (gate)
        {
            EnsureFreshCode();
            return new(LanEnabled, loopback, paired, loopback || paired ? Environment.MachineName : "Framewright workstation", loopback && LanEnabled ? code : null, loopback && LanEnabled ? codeExpiresAt : null,
                !LanEnabled ? "LAN access is off. Start the explicit tablet launcher to bind beyond loopback." : loopback ? "Share the short-lived code in person. Provider keys remain server-side." : paired ? "This browser has a temporary HttpOnly workstation session." : "Enter the short-lived code shown on the workstation.");
        }
    }

    public PairingStatusSummary Rotate(bool loopback)
    {
        if (!loopback) throw new UnauthorizedAccessException("Only the workstation can rotate pairing codes.");
        lock (gate) { code = CreateCode(); codeExpiresAt = timeProvider.GetUtcNow().AddMinutes(10); attempts.Clear(); return Status(true, false); }
    }

    public string? Claim(string suppliedCode, string clientKey)
    {
        if (!LanEnabled || string.IsNullOrWhiteSpace(suppliedCode)) return null;
        var now = timeProvider.GetUtcNow(); var window = attempts.AddOrUpdate(clientKey, _ => new(now, 1), (_, current) => now - current.Start > TimeSpan.FromMinutes(10) ? new(now, 1) : current with { Count = current.Count + 1 });
        if (window.Count > 8) return null;
        lock (gate)
        {
            EnsureFreshCode();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(suppliedCode.Trim()))) return null;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(); sessions[Hash(token)] = now.AddHours(12); code = CreateCode(); codeExpiresAt = now.AddMinutes(10); attempts.TryRemove(clientKey, out _); return token;
        }
    }

    public bool Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false; var hash = Hash(token); if (!sessions.TryGetValue(hash, out var expires)) return false; if (expires <= timeProvider.GetUtcNow()) { sessions.TryRemove(hash, out _); return false; }
        return true;
    }

    public PairingStatusSummary RevokeAll(bool loopback)
    {
        if (!loopback) throw new UnauthorizedAccessException("Only the workstation can revoke tablet sessions.");
        sessions.Clear();
        return Rotate(true);
    }

    private void EnsureFreshCode() { if (codeExpiresAt > timeProvider.GetUtcNow()) return; code = CreateCode(); codeExpiresAt = timeProvider.GetUtcNow().AddMinutes(10); attempts.Clear(); }
    private static string CreateCode() => RandomNumberGenerator.GetInt32(0, 100_000_000)
        .ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record AttemptWindow(DateTimeOffset Start, int Count);
}
