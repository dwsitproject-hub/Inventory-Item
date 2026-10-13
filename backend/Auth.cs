using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dapper;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace BcInventory.Api;

public record LoginRequest(string Email, string Password);
public record UserScope(long UserId, string Email, string FullName, string Role,
    // Site scope: AllSites = every site of the user's companies; otherwise only SiteIds.
    bool AllSites, long[] SiteIds,
    // Multi-tenant company scope. Super Admin spans all companies (AllCompanies); every other user
    // is restricted to CompanyIds. Data queries filter on both (see Sites.RowFilter).
    bool AllCompanies, long[] CompanyIds);

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
            "select id, email, full_name, role, password_hash, all_entities, status from auth.users where lower(email) = lower(@e)",
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

        UserScope scope = await Sites.ScopeOf(con, user);
        Audit.Log("auth.login", scope, "user", scope.UserId.ToString(), "Signed in", null, ip);
        return Results.Ok(SessionResponse(cfg, scope));
    }

    /// <summary>Role that spans every company and every site by definition.</summary>
    public const string SuperAdmin = "Super Admin";

    /// <summary>
    /// Mint a BC Inventory session for an authenticated account, and shape the login response.
    /// Shared by password login and SSO federation (Sso.cs): however identity was proven, the
    /// session, its claims and its lifetime are the same, so authorization, audit and the AR-01
    /// per-request revalidation all behave identically.
    /// </summary>
    public static object SessionResponse(IConfiguration cfg, UserScope user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));
        var token = new JwtSecurityToken(
            issuer: "bc-inventory", audience: "bc-inventory",
            claims: new[]
            {
                new Claim("sub", user.UserId.ToString()),
                new Claim("email", user.Email),
                new Claim("name", user.FullName),
                new Claim("role", user.Role),
                new Claim("allSites", user.AllSites.ToString().ToLowerInvariant()),
                // Issued-at, so a later password reset or disable can retire this token (AR-01).
                new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            },
            // AR-10: shorter by default, and configurable per environment. A captured token is
            // useful for this long, so the window is a security decision rather than a constant.
            expires: DateTime.UtcNow.AddMinutes(int.TryParse(cfg["Jwt:LifetimeMinutes"], out var m) && m is > 0 and <= 720 ? m : 120),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new
        {
            accessToken = new JwtSecurityTokenHandler().WriteToken(token),
            user = new
            {
                email = user.Email,
                fullName = user.FullName,
                role = user.Role,
                allSites = user.AllSites,
                siteIds = user.SiteIds
            }
        };
    }

    /// <summary>Key under which the per-request middleware stores the freshly resolved scope.</summary>
    public const string ScopeItemKey = "bc.scope";

    private static IHttpContextAccessor? _http;
    public static void Configure(IHttpContextAccessor http) => _http = http;

    /// <summary>
    /// The signed-in user's CURRENT role and scope. Inside a request this is the state the
    /// session middleware just read from the database, not what the token asserted when it was
    /// issued — so a demotion or a narrowed site scope applies without waiting for expiry.
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
            C("allSites") == "true", Array.Empty<long>(),
            C("role") == SuperAdmin, Array.Empty<long>());
    }

    public static async Task<IResult> Me(NpgsqlDataSource ds, ClaimsPrincipal principal)
    {
        var scope = Scope(principal);
        await using var con = await ds.OpenConnectionAsync();
        // The sites this user may see — the Reports / Inventory Movement site filter offers these.
        var sites = await Sites.Mine(con, scope);
        var perms = await Permissions.Effective(scope.Role);
        // Header logo: only when the user belongs to exactly one company that has a logo; null for
        // Super Admin or a multi-company user (the header shows no company logo then).
        var (companyId, companyName, companyLogo) = await Companies.HeaderLogo(con, scope);
        return Results.Ok(new
        {
            user = new { scope.Email, scope.FullName, scope.Role, scope.AllSites, siteIds = scope.SiteIds,
                         scope.AllCompanies, companyIds = scope.CompanyIds },
            permissions = perms.ToDictionary(k => k.Key, v => new { v.Value.View, v.Value.Insert, v.Value.Edit, v.Value.Delete, v.Value.Export }),
            sites,
            company = new { id = companyId, name = companyName, logo = companyLogo }
        });
    }
}
