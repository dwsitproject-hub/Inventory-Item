using System.Collections.Concurrent;
using System.Security.Claims;
using Dapper;
using Npgsql;

namespace BcInventory.Api;

/// <summary>
/// Re-validates the account behind a bearer token on every request (AR-01).
///
/// A JWT is a snapshot of who the user was when it was issued. Trusting its role, status and
/// entity claims for the whole 8-hour lifetime meant that disabling an account, demoting a
/// role or resetting a password left the existing session working until the token expired —
/// so de-provisioning did not actually de-provision. Authorization now reads the account as it
/// is now: the token proves identity, the database decides what that identity may do.
///
/// The lookup is cached briefly per user so this costs nothing measurable against the 2-second
/// budget; the window is the maximum delay between an administrator's action and its effect,
/// and is deliberately short.
/// </summary>
public static class Sessions
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    private record Entry(UserScope Scope, DateTimeOffset TokensValidFrom, DateTime CachedAt);
    private static readonly ConcurrentDictionary<long, Entry> _cache = new();

    /// <summary>Drop a user's cached state so an administrative change applies immediately.</summary>
    public static void Invalidate(long userId) => _cache.TryRemove(userId, out _);

    public enum Outcome { Ok, UnknownUser, Disabled, TokenSuperseded }

    /// <summary>
    /// Returns the account's CURRENT scope, or the reason the request must be refused.
    /// Never returns a scope built from the token's own claims.
    /// </summary>
    public static async Task<(Outcome outcome, UserScope? scope)> Resolve(NpgsqlDataSource ds, ClaimsPrincipal principal)
    {
        if (!long.TryParse(principal.FindFirstValue("sub"), out var id) || id <= 0)
            return (Outcome.UnknownUser, null);

        if (!_cache.TryGetValue(id, out var e) || DateTime.UtcNow - e.CachedAt > Ttl)
        {
            await using var con = await ds.OpenConnectionAsync();
            var row = await con.QuerySingleOrDefaultAsync("""
                select id, email, full_name, role, status, all_entities, entity_id, site_id,
                       tokens_valid_from
                from auth.users where id = @id
                """, new { id });
            if (row is null)
            {
                _cache.TryRemove(id, out _);
                return (Outcome.UnknownUser, null);
            }
            if ((string)row.status != "active")
            {
                _cache.TryRemove(id, out _);
                return (Outcome.Disabled, null);
            }
            e = new Entry(
                new UserScope((long)row.id, (string)row.email, (string)row.full_name, (string)row.role,
                              (bool)row.all_entities, (long?)row.entity_id, (long?)row.site_id),
                new DateTimeOffset((DateTime)row.tokens_valid_from, TimeSpan.Zero),
                DateTime.UtcNow);
            _cache[id] = e;
        }

        // A password reset or a disable/enable bumps tokens_valid_from, retiring every token
        // issued before it. Compared at whole seconds because that is the resolution of iat.
        if (long.TryParse(principal.FindFirstValue("iat"), out var iat))
        {
            var issued = DateTimeOffset.FromUnixTimeSeconds(iat);
            if (issued < e.TokensValidFrom.AddSeconds(-1))
                return (Outcome.TokenSuperseded, null);
        }

        return (Outcome.Ok, e.Scope);
    }
}
