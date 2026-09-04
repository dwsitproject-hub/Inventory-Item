using System.Text.Json;
using Dapper;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.IdentityModel.Tokens.Jwt;

namespace BcInventory.Api;

public record SsoCallbackRequest(string Code, string CodeVerifier, string? Nonce, string? RedirectUri);

/// <summary>
/// Single sign-on via the DWS Hub (OIDC authorization code + PKCE, strict mode).
///
/// The Hub proves identity; BC Inventory decides authorization. The browser runs the PKCE
/// authorize redirect; on return it hands us the code and its verifier, we exchange them at the
/// Hub, verify the returned id_token against the Hub's JWKS (RS256), then map the Hub user to a
/// pre-existing local account and issue an ordinary BC Inventory session. Roles, entity scope,
/// the audit trail and the per-request session revalidation (AR-01) are unchanged — SSO only
/// replaces the password step. There is no auto-provisioning: a customs account needs an
/// admin-assigned role and entity scope, so an unknown Hub user is refused, not created.
/// </summary>
public static class Sso
{
    private static IConfiguration _cfg = default!;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static bool Enabled => string.Equals(_cfg["Sso:Enabled"], "true", StringComparison.OrdinalIgnoreCase)
                                  && !string.IsNullOrWhiteSpace(_cfg["Sso:Issuer"])
                                  && !string.IsNullOrWhiteSpace(_cfg["Sso:ClientId"]);

    private static string Issuer => (_cfg["Sso:Issuer"] ?? "").TrimEnd('/');
    private static string ClientId => _cfg["Sso:ClientId"] ?? "";
    private static string RedirectUri => _cfg["Sso:RedirectUri"] ?? "";
    private static string Scope => _cfg["Sso:Scope"] ?? "openid profile email";

    public static void Configure(IConfiguration cfg) => _cfg = cfg;

    // ---- discovery + JWKS, cached ----
    private record Discovery(string issuer, string authorization_endpoint, string token_endpoint, string jwks_uri);
    private static Discovery? _disc;
    private static DateTime _discAt;
    private static JsonWebKeySet? _jwks;
    private static DateTime _jwksAt;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private static async Task<Discovery> GetDiscoveryAsync()
    {
        if (_disc is not null && DateTime.UtcNow - _discAt < TimeSpan.FromMinutes(15)) return _disc;
        await _lock.WaitAsync();
        try
        {
            if (_disc is not null && DateTime.UtcNow - _discAt < TimeSpan.FromMinutes(15)) return _disc;
            var json = await _http.GetStringAsync($"{Issuer}/api/sso/.well-known/openid-configuration");
            var d = JsonSerializer.Deserialize<Discovery>(json)
                    ?? throw new InvalidOperationException("Hub discovery document was empty.");
            // The discovery issuer is what we validate id_token 'iss' against; it must match the
            // configured base so a swapped discovery host cannot redirect trust elsewhere.
            if (!string.Equals(d.issuer.TrimEnd('/'), Issuer, StringComparison.Ordinal))
                throw new InvalidOperationException($"Hub discovery issuer '{d.issuer}' does not match configured Sso:Issuer '{Issuer}'.");
            _disc = d; _discAt = DateTime.UtcNow;
            return d;
        }
        finally { _lock.Release(); }
    }

    private static async Task<JsonWebKeySet> GetJwksAsync(bool force = false)
    {
        if (!force && _jwks is not null && DateTime.UtcNow - _jwksAt < TimeSpan.FromMinutes(10)) return _jwks;
        var d = await GetDiscoveryAsync();
        var json = await _http.GetStringAsync(d.jwks_uri);
        _jwks = new JsonWebKeySet(json); _jwksAt = DateTime.UtcNow;
        return _jwks;
    }

    // ---- endpoints ----

    /// <summary>Public config the SPA needs to start the PKCE authorize redirect.</summary>
    public static async Task<IResult> Info()
    {
        if (!Enabled) return Results.Ok(new { enabled = false });
        try
        {
            var d = await GetDiscoveryAsync();
            return Results.Ok(new
            {
                enabled = true,
                authorizeEndpoint = d.authorization_endpoint,
                clientId = ClientId,
                redirectUri = RedirectUri,
                scope = Scope
            });
        }
        catch (Exception ex)
        {
            // Do not break the login page if the Hub is momentarily unreachable — just hide SSO.
            Console.WriteLine("[sso] info failed: " + ex.Message);
            return Results.Ok(new { enabled = false });
        }
    }

    /// <summary>
    /// Exchange the authorization code, verify the id_token, map to a local account and issue a
    /// BC Inventory session. Returns the same shape as password login.
    /// </summary>
    public static async Task<IResult> Callback(NpgsqlDataSource ds, IConfiguration cfg, SsoCallbackRequest req, string? ip = null)
    {
        if (!Enabled)
            return Results.Problem(statusCode: 400, title: "SSO-000", detail: "Single sign-on is not enabled.");
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.CodeVerifier))
            return Results.Problem(statusCode: 400, title: "SSO-001", detail: "Missing authorization code or verifier.");

        Discovery disc;
        try { disc = await GetDiscoveryAsync(); }
        catch (Exception ex)
        {
            Console.WriteLine("[sso] discovery failed: " + ex.Message);
            return Results.Problem(statusCode: 502, title: "SSO-002", detail: "Could not reach the identity provider.");
        }

        // 1) exchange the code (PKCE, public client — no secret)
        var redirect = string.IsNullOrWhiteSpace(req.RedirectUri) ? RedirectUri : req.RedirectUri!;
        JsonElement tok;
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                grant_type = "authorization_code",
                code = req.Code,
                redirect_uri = redirect,
                client_id = ClientId,
                code_verifier = req.CodeVerifier
            });
            var resp = await _http.PostAsync(disc.token_endpoint,
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Audit.Log("auth.sso_failed", null, "user", null, "SSO token exchange rejected",
                    new { status = (int)resp.StatusCode, body = text.Length > 300 ? text[..300] : text }, ip);
                return Results.Problem(statusCode: 401, title: "SSO-003",
                    detail: "Sign-in with DWS Hub failed (the code could not be exchanged). Please try again.");
            }
            tok = JsonDocument.Parse(text).RootElement;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[sso] token exchange error: " + ex.Message);
            return Results.Problem(statusCode: 502, title: "SSO-002", detail: "Could not reach the identity provider.");
        }

        if (!tok.TryGetProperty("id_token", out var idTokenEl) || idTokenEl.GetString() is not { } idToken)
            return Results.Problem(statusCode: 401, title: "SSO-003", detail: "The identity provider returned no id_token.");

        // 2) verify the id_token against the Hub's JWKS
        JwtSecurityToken jwt;
        try
        {
            jwt = await VerifyIdTokenAsync(idToken);
        }
        catch (Exception ex)
        {
            Audit.Log("auth.sso_failed", null, "user", null, "SSO id_token verification failed",
                new { reason = ex.Message }, ip);
            return Results.Problem(statusCode: 401, title: "SSO-004",
                detail: "Sign-in with DWS Hub failed (identity could not be verified).");
        }

        var sub = jwt.Subject;
        var email = jwt.Claims.FirstOrDefault(c => c.Type is "email")?.Value;
        var name = jwt.Claims.FirstOrDefault(c => c.Type is "name")?.Value;
        if (string.IsNullOrWhiteSpace(sub))
            return Results.Problem(statusCode: 401, title: "SSO-004", detail: "The id_token has no subject.");

        // Optional nonce binding (replay protection): if the SPA sent a nonce, it must match.
        if (!string.IsNullOrEmpty(req.Nonce))
        {
            var tokenNonce = jwt.Claims.FirstOrDefault(c => c.Type == "nonce")?.Value;
            if (tokenNonce != req.Nonce)
                return Results.Problem(statusCode: 401, title: "SSO-004", detail: "Sign-in could not be verified (nonce mismatch).");
        }

        // 3) map to a pre-existing local account and issue the session. Wrapped so an unexpected
        //    failure here (e.g. the sso_sub column missing because --migrate was not applied)
        //    returns a clean, logged SSO-006 instead of a bare 500 the SPA cannot explain.
        try
        {
            await using var con = await ds.OpenConnectionAsync();
            var user = await con.QueryFirstOrDefaultAsync(
                "select id, email, full_name, role, all_entities, entity_id, site_id, status, sso_sub from auth.users where sso_sub = @sub",
                new { sub });
            if (user is null && !string.IsNullOrWhiteSpace(email))
                user = await con.QueryFirstOrDefaultAsync(
                    "select id, email, full_name, role, all_entities, entity_id, site_id, status, sso_sub from auth.users where lower(email) = lower(@e)",
                    new { e = email });

            if (user is null)
            {
                Audit.Log("auth.sso_denied", null, "user", email ?? sub, "SSO sign-in for an account that does not exist",
                    new { sub, email }, ip);
                return Results.Problem(statusCode: 403, title: "SSO-005",
                    detail: "Your DWS Hub account is not registered in BC Inventory. Ask an administrator to create your account first.");
            }
            if ((string)user.status != "active")
            {
                Audit.Log("auth.sso_denied", Auth.ScopeOf(user), "user", ((long)user.id).ToString(), "SSO sign-in for a disabled account",
                    new { sub, email }, ip);
                return Results.Problem(statusCode: 403, title: "SSO-005", detail: "This account has been disabled.");
            }

            // Bind the Hub sub on first SSO login so the link is stable even if the email later changes.
            if (user.sso_sub is null)
                await con.ExecuteAsync("update auth.users set sso_sub = @sub where id = @id", new { sub, id = (long)user.id });

            Audit.Log("auth.sso_login", Auth.ScopeOf(user), "user", ((long)user.id).ToString(),
                "Signed in via DWS Hub", new { sub }, ip);
            return Results.Ok(Auth.SessionResponse(cfg, user));
        }
        catch (Npgsql.PostgresException pex) when (pex.SqlState == "42703")
        {
            // undefined_column — almost always the sso_sub column, i.e. the schema migration for
            // SSO was not applied on this environment.
            Console.WriteLine("[sso] callback DB schema error: " + pex.Message);
            return Results.Problem(statusCode: 500, title: "SSO-006",
                detail: "BC Inventory is not fully set up for SSO on this server (a schema update is pending). Ask an administrator to apply it.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[sso] callback failed after verification: " + ex);
            return Results.Problem(statusCode: 500, title: "SSO-006",
                detail: "Sign-in with DWS Hub could not be completed due to a server error. Please contact an administrator.");
        }
    }

    /// <summary>Verify an id_token: signature via JWKS (RS256), issuer, audience, expiry, sub.</summary>
    private static async Task<JwtSecurityToken> VerifyIdTokenAsync(string idToken)
    {
        var disc = await GetDiscoveryAsync();
        var handler = new JwtSecurityTokenHandler();

        async Task<JwtSecurityToken> Validate(JsonWebKeySet jwks)
        {
            var pars = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = disc.issuer,
                ValidateAudience = true,
                ValidAudience = ClientId,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = jwks.GetSigningKeys(),
                ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },   // never accept HS256 here
            };
            handler.ValidateToken(idToken, pars, out var validated);
            return (JwtSecurityToken)validated;
        }

        try { return await Validate(await GetJwksAsync()); }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // Key rotation: the cached JWKS may be stale. Refresh once and retry.
            return await Validate(await GetJwksAsync(force: true));
        }
    }
}
