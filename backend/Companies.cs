using Dapper;
using Npgsql;

namespace BcInventory.Api;

/// <summary>Create/update a company. Logo arrives as base64 + content type (or omitted to keep it).</summary>
public record CompanyRequest(string Name, bool Active, string? LogoBase64, string? LogoContentType);

/// <summary>Set which companies a user is assigned to.</summary>
public record SetCompaniesRequest(long[]? CompanyIds);

/// <summary>
/// Multi-tenant company (PT) management. A company owns users and uploaded data and carries the
/// logo shown in the header after login. Managing companies and assigning them to users is
/// Super-Admin-only; every non-Super-Admin user is scoped to their assigned companies.
/// </summary>
public static class Companies
{
    private static IResult? Guard(UserScope scope) =>
        scope.Role == Auth.SuperAdmin ? null
        : Results.Problem(statusCode: 403, title: "SCOPE-003", detail: "Only a Super Admin may manage companies.");

    public static async Task<IResult> List(NpgsqlDataSource ds, UserScope scope)
    {
        if (Guard(scope) is { } g) return g;
        await using var con = await ds.OpenConnectionAsync();
        var rows = await con.QueryAsync("""
            select c.id, c.name, c.active,
                   (c.logo is not null) as "hasLogo",
                   (select count(*) from auth.user_companies uc where uc.company_id = c.id) as "userCount"
            from master.companies c order by c.name
            """);
        return Results.Ok(rows);
    }

    public static async Task<IResult> Create(NpgsqlDataSource ds, UserScope scope, CompanyRequest req)
    {
        if (Guard(scope) is { } g) return g;
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "Company name is required.");
        var (logo, ct, err) = DecodeLogo(req.LogoBase64, req.LogoContentType);
        if (err is not null) return err;
        await using var con = await ds.OpenConnectionAsync();
        try
        {
            var id = await con.ExecuteScalarAsync<long>("""
                insert into master.companies (name, logo, logo_content_type, active)
                values (@Name, @logo, @ct, @Active) returning id
                """, new { req.Name, logo, ct, req.Active });
            Audit.Log("admin.company.create", scope, "company", id.ToString(), $"Created company {req.Name}", new { req.Name }, null);
            return Results.Ok(new { id });
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            return Results.Problem(statusCode: 409, title: "VAL-002", detail: "A company with that name already exists.");
        }
    }

    public static async Task<IResult> Update(NpgsqlDataSource ds, UserScope scope, long id, CompanyRequest req)
    {
        if (Guard(scope) is { } g) return g;
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "Company name is required.");
        var (logo, ct, err) = DecodeLogo(req.LogoBase64, req.LogoContentType);
        if (err is not null) return err;
        await using var con = await ds.OpenConnectionAsync();
        // Replace the logo only when a new one was supplied; otherwise keep the existing image.
        var n = await con.ExecuteAsync("""
            update master.companies
               set name = @Name, active = @Active,
                   logo = coalesce(@logo, logo),
                   logo_content_type = coalesce(@ct, logo_content_type)
             where id = @id
            """, new { id, req.Name, req.Active, logo, ct });
        if (n == 0) return Results.Problem(statusCode: 404, title: "VAL-003", detail: "Company not found.");
        Audit.Log("admin.company.update", scope, "company", id.ToString(), $"Updated company {req.Name}", null, null);
        return Results.Ok(new { ok = true });
    }

    /// <summary>Serve a company's logo image (authenticated; used by the Admin preview).</summary>
    public static async Task<IResult> Logo(NpgsqlDataSource ds, long id)
    {
        await using var con = await ds.OpenConnectionAsync();
        var row = await con.QueryFirstOrDefaultAsync(
            "select logo, logo_content_type as ct from master.companies where id = @id", new { id });
        if (row is null || row.logo is null) return Results.NotFound();
        return Results.File((byte[])row.logo, (string?)row.ct ?? "image/png");
    }

    // Validate a logo payload: base64 + content type, size cap, allowed image types.
    private static (byte[]? logo, string? ct, IResult? err) DecodeLogo(string? b64, string? ct)
    {
        if (string.IsNullOrWhiteSpace(b64)) return (null, null, null);   // omitted: no change / no logo
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64.Contains(',') ? b64[(b64.IndexOf(',') + 1)..] : b64); }
        catch { return (null, null, Results.Problem(statusCode: 400, title: "VAL-001", detail: "Logo is not valid base64.")); }
        if (bytes.Length > 512 * 1024)
            return (null, null, Results.Problem(statusCode: 400, title: "VAL-001", detail: "Logo must be 512 KB or smaller."));
        var t = (ct ?? "").ToLowerInvariant();
        if (t is not ("image/png" or "image/jpeg" or "image/jpg" or "image/svg+xml" or "image/webp"))
            return (null, null, Results.Problem(statusCode: 400, title: "VAL-001", detail: "Logo must be PNG, JPEG, SVG or WebP."));
        return (bytes, t, null);
    }

    // --- user ↔ company assignment ---

    public static async Task<IResult> SetUserCompanies(NpgsqlDataSource ds, UserScope scope, long userId, long[] companyIds)
    {
        if (Guard(scope) is { } g) return g;
        await using var con = await ds.OpenConnectionAsync();
        await using var tx = await con.BeginTransactionAsync();
        await con.ExecuteAsync("delete from auth.user_companies where user_id = @userId", new { userId }, tx);
        if (companyIds is { Length: > 0 })
            await con.ExecuteAsync(
                "insert into auth.user_companies (user_id, company_id) select @userId, unnest(@ids::bigint[]) on conflict do nothing",
                new { userId, ids = companyIds }, tx);
        await tx.CommitAsync();
        Sessions.Invalidate(userId);   // apply the new scope on the user's next request
        Audit.Log("admin.user.companies", scope, "user", userId.ToString(), "Updated company assignment", new { companyIds }, null);
        return Results.Ok(new { ok = true });
    }

    /// <summary>The header logo for a user: only when they belong to exactly one company with a logo.</summary>
    public static async Task<(long? id, string? name, string? dataUri)> HeaderLogo(NpgsqlConnection con, UserScope scope)
    {
        if (scope.AllCompanies || scope.CompanyIds.Length != 1) return (null, null, null);
        var row = await con.QueryFirstOrDefaultAsync(
            "select id, name, logo, logo_content_type as ct from master.companies where id = @id",
            new { id = scope.CompanyIds[0] });
        if (row is null) return (null, null, null);
        string? uri = row.logo is null ? null
            : $"data:{(string?)row.ct ?? "image/png"};base64,{Convert.ToBase64String((byte[])row.logo)}";
        return ((long)row.id, (string)row.name, uri);
    }

    /// <summary>Companies a user may pick from when uploading (their own; Super Admin = all active).</summary>
    public static async Task<List<dynamic>> Pickable(NpgsqlConnection con, UserScope scope)
    {
        var sql = scope.AllCompanies
            ? "select id, name from master.companies where active order by name"
            : "select id, name from master.companies where active and id = any(@ids) order by name";
        return (await con.QueryAsync(sql, new { ids = scope.CompanyIds })).ToList();
    }

    /// <summary>Resolve and validate the company an upload should be tagged with, for this user.</summary>
    public static (long? companyId, IResult? err) ResolveUploadCompany(UserScope scope, long? requested)
    {
        if (scope.AllCompanies)
            return requested is null
                ? (null, Results.Problem(statusCode: 400, title: "VAL-001", detail: "Select a company for this upload."))
                : (requested, null);
        if (requested is null && scope.CompanyIds.Length == 1) requested = scope.CompanyIds[0];
        if (requested is null)
            return (null, Results.Problem(statusCode: 400, title: "VAL-001", detail: "Select a company for this upload."));
        if (!scope.CompanyIds.Contains(requested.Value))
            return (null, Results.Problem(statusCode: 403, title: "SCOPE-002", detail: "Requested company outside your scope."));
        return (requested, null);
    }
}
