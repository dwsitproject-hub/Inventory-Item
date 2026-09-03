using System.Collections.Concurrent;

namespace BcInventory.Api;

/// <summary>
/// Throttles sign-in attempts (AR-03).
///
/// BCrypt at work factor 11 makes offline cracking expensive, but it does nothing about an
/// attacker guessing online against a known address — and the sign-in page used to name two
/// valid accounts. This adds the missing cost: after a handful of failures an account, and
/// separately a source address, stop being answered for a while.
///
/// Counters are per-process and in memory. That is honest rather than ideal: it survives a
/// burst from one attacker against one API instance, which is the shape of the risk here, and
/// resets on restart. A shared store would be needed if the API were ever load balanced.
/// </summary>
public static class LoginThrottle
{
    private const int MaxPerAccount = 5;      // failures before an account stops answering
    private const int MaxPerAddress = 20;     // failures before a source address is refused
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private sealed class Counter
    {
        public int Failures;
        public DateTime FirstAt = DateTime.UtcNow;
        public DateTime? BlockedUntil;
    }

    private static readonly ConcurrentDictionary<string, Counter> _byAccount = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Counter> _byAddress = new();

    private static bool Blocked(ConcurrentDictionary<string, Counter> map, string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (key.Length == 0 || !map.TryGetValue(key, out var c)) return false;
        if (c.BlockedUntil is { } until)
        {
            if (DateTime.UtcNow < until) { retryAfter = until - DateTime.UtcNow; return true; }
            map.TryRemove(key, out _);          // lockout served
        }
        else if (DateTime.UtcNow - c.FirstAt > Window)
        {
            map.TryRemove(key, out _);          // window elapsed without reaching the limit
        }
        return false;
    }

    /// <summary>How long the caller must wait, or null if the attempt may proceed.</summary>
    public static TimeSpan? RetryAfter(string email, string? ip)
    {
        if (Blocked(_byAccount, email ?? "", out var a)) return a;
        if (Blocked(_byAddress, ip ?? "", out var b)) return b;
        return null;
    }

    private static void Bump(ConcurrentDictionary<string, Counter> map, string key, int max)
    {
        if (key.Length == 0) return;
        var c = map.GetOrAdd(key, _ => new Counter());
        if (DateTime.UtcNow - c.FirstAt > Window && c.BlockedUntil is null)
        {
            c.Failures = 0;
            c.FirstAt = DateTime.UtcNow;
        }
        if (++c.Failures >= max) c.BlockedUntil = DateTime.UtcNow.Add(Lockout);
    }

    public static void Failed(string email, string? ip)
    {
        Bump(_byAccount, email ?? "", MaxPerAccount);
        Bump(_byAddress, ip ?? "", MaxPerAddress);
    }

    /// <summary>A success clears the account's counter; the address counter is left to expire.</summary>
    public static void Succeeded(string email)
    {
        if (!string.IsNullOrEmpty(email)) _byAccount.TryRemove(email, out _);
    }
}
