using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dapper;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace BcInventory.Api;

public record LoginRequest(string Email, string Password);
public record UserScope(long UserId, string Email, string FullName, string Role, bool AllEntities, long? EntityId, long? SiteId);

public static class Auth
{
    public static async Task<IResult> Login(NpgsqlDataSource ds, IConfiguration cfg, LoginRequest req, string? ip = null)
    {
        var email = req.Email ?? "";

        // AR-03: refuse before touching the database, so a locked-out attacker cannot even use
        // the hashing cost as an oracle. The message deliberately says no more than it must.
        if (LoginThrottle.RetryAfter(email, ip) is { } wait)
        {
            Audit.Log("auth.login_blocked", null, "user", email, "Sign-in refused: too many attempts",
                new { retryAfterSeconds = (int)wait.TotalSeconds }, ip, actorEmailOverride: email);
            return Results.Problem(statusCode: 429, title: "AUTH-005",
                detail: $"Too many sign-in attempts. Try again in {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))} minute(s).");
        }

        await using var con = await ds.OpenConnectionAsync();
        var user = await con.QueryFirstOrDefaultAsync(
            "select id, email, full_name, role, password_hash, all_entities, entity_id, site_id, status from auth.users where lower(email) = lower(@e)",
            new { e = email });

        if (user is null || user.status != "active" || !BCrypt.Net.BCrypt.Verify(req.Password ?? "", (string)user.password_hash))
        {
            LoginThrottle.Failed(email, ip);
            Audit.Log("auth.login_failed", null, "user", req.Email, "Failed sign-in attempt",
                new { reason = user is null ? "unknown email" : user.status != "active" ? "account disabled" : "bad password" },
                ip, actorEmailOverride: req.Email);
            return Results.Problem(statusCode: 401, title: "AUTH-001", detail: "Invalid credentials.");
        }

        LoginThrottle.Succeeded(email);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));
        var token = new JwtSecurityToken(
            issuer: "bc-inventory", audience: "bc-inventory",
            claims: new[]
            {
                new Claim("sub", ((long)user.id).ToString()),
                new Claim("email", (string)user.email),
                new Claim("name", (string)user.full_name),
                new Claim("role", (string)user.role),
                new Claim("allEntities", ((bool)user.all_entities).ToString().ToLowerInvariant()),
                new Claim("entityId", user.entity_id?.ToString() ?? ""),
                new Claim("siteId", user.site_id?.ToString() ?? ""),
                // Issued-at, so a later password reset or disable can retire this token (AR-01).
                new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            },
            // AR-10: shorter by default, and configurable per environment. A captured token is
            // useful for this long, so the window is a security decision rather than a constant.
            expires: DateTime.UtcNow.AddMinutes(int.TryParse(cfg["Jwt:LifetimeMinutes"], out var m) && m is > 0 and <= 720 ? m : 120),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        Audit.Log("auth.login", new UserScope((long)user.id, (string)user.email, (string)user.full_name,
                (string)user.role, (bool)user.all_entities, (long?)user.entity_id, (long?)user.site_id),
            "user", ((long)user.id).ToString(), "Signed in", null, ip);

        return Results.Ok(new
        {
            accessToken = new JwtSecurityTokenHandler().WriteToken(token),
            user = new
            {
                email = (string)user.email,
                fullName = (string)user.full_name,
                role = (string)user.role,
                allEntities = (bool)user.all_entities,
                entityId = (long?)user.entity_id,
                siteId = (long?)user.site_id
            }
        });
    }

    /// <summary>Key under which the per-request middleware stores the freshly resolved scope.</summary>
    public const string ScopeItemKey = "bc.scope";

    private static IHttpContextAccessor? _http;
    public static void Configure(IHttpContextAccessor http) => _http = http;

    /// <summary>
    /// The signed-in user's CURRENT role and scope. Inside a request this is the state the
    /// session middleware just read from the database, not what the token asserted when it was
    /// issued — so a demotion or a narrowed entity scope applies without waiting for expiry.
    /// The claim-based fallback exists only for code paths outside a request.
    /// </summary>
    public static UserScope Scope(ClaimsPrincipal principal)
    {
        if (_http?.HttpContext is { } ctx && ctx.Items.TryGetValue(ScopeItemKey, out var v) && v is UserScope fresh)
            return fresh;
        return FromClaims(principal);
    }

    private static UserScope FromClaims(ClaimsPrincipal principal)
    {
        string C(string t) => principal.FindFirstValue(t) ?? "";
        return new UserScope(
            long.TryParse(C("sub"), out var id) ? id : 0,
            C("email"), C("name"), C("role"),
            C("allEntities") == "true",
            long.TryParse(C("entityId"), out var e) ? e : null,
            long.TryParse(C("siteId"), out var s) ? s : null);
    }

    public static async Task<IResult> Me(NpgsqlDataSource ds, ClaimsPrincipal principal)
    {
        var scope = Scope(principal);
        await using var con = await ds.OpenConnectionAsync();
        var entities = (await con.QueryAsync("select id, code, name from master.entities order by name")).ToList();
        var sites = (await con.QueryAsync("""select id, entity_id as "entityId", name from master.sites order by name""")).ToList();
        var perms = await Permissions.Effective(scope.Role);
        return Results.Ok(new
        {
            user = new { scope.Email, scope.FullName, scope.Role, scope.AllEntities, scope.EntityId, scope.SiteId },
            permissions = perms.ToDictionary(k => k.Key, v => new { v.Value.View, v.Value.Insert, v.Value.Edit }),
            entities,
            sites
        });
    }
}
