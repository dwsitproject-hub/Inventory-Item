using Dapper;
using Npgsql;

namespace BcInventory.Api;

public record CreateUserRequest(string Email, string FullName, string Role, bool AllSites, long[]? SiteIds, string Password, long[]? CompanyIds);
public record ResetPasswordRequest(string Password);
public record StatusRequest(string Status);
/// <summary>A user's sites: all sites of their companies, or the listed ones.</summary>
public record SitesRequest(bool AllSites, long[]? SiteIds);
public record SiteRequest(long CompanyId, string Name);
public record PermitRequest(long CompanyId, long? SiteId, string PermitNo);

/// <summary>Administration: users & scope + master data (FR-A1/A3/A4). No hard delete of users (PRD §6.3).</summary>
public static class Admin
{
    private static readonly string[] Roles =
        { "Super Admin", "Admin", "HO BC User", "Site BC User", "Data Steward", "Auditor" };

    /// <summary>Admin access is governed by the configurable Role Management matrix.</summary>
    public static Task<IResult?> RequireAdmin(UserScope s, string action = "view") =>
        Permissions.Require(s, "admin", action);

    public static async Task<IResult> ListUsers(NpgsqlDataSource ds, UserScope scope)
    {
        if (await RequireAdmin(scope) is { } err) return err;
        await using var con = await ds.OpenConnectionAsync();
        var users = await con.QueryAsync("""
            select u.id, u.email, u.full_name as "fullName", u.role, u.status,
                   (u.all_entities or u.role = 'Super Admin') as "allSites", u.created_at as "createdAt",
                   coalesce((select array_agg(uc.company_id order by c.name)
                             from auth.user_companies uc join master.companies c on c.id = uc.company_id
                             where uc.user_id = u.id), '{}'::bigint[]) as "companyIds",
                   coalesce((select string_agg(c.name, ', ' order by c.name)
                             from auth.user_companies uc join master.companies c on c.id = uc.company_id
                             where uc.user_id = u.id), '') as "companyNames",
                   coalesce((select array_agg(us.site_id order by s.name)
                             from auth.user_sites us join master.sites s on s.id = us.site_id
                             where us.user_id = u.id), '{}'::bigint[]) as "siteIds",
                   coalesce((select string_agg(s.name, ', ' order by s.name)
                             from auth.user_sites us join master.sites s on s.id = us.site_id
                             where us.user_id = u.id), '') as "siteNames"
            from auth.users u
            order by u.id
            """);
        return Results.Ok(new { users, roles = Roles });
    }

    /// <summary>
    /// Validate a site assignment: all sites (Super-Admin-granted only), or at least one site,
    /// each belonging to one of the user's companies. A Super-Admin-role user spans everything.
    /// </summary>
    private static async Task<IResult?> CheckSites(NpgsqlConnection con, UserScope scope, string role, bool allSites, long[] siteIds, long[] companyIds)
    {
        if (role == Auth.SuperAdmin) return null;
        if (allSites && scope.Role != Auth.SuperAdmin)
            return Results.Problem(statusCode: 403, title: "AUTH-003", detail: "Only a Super Admin can grant all-sites scope.");
        if (allSites) return null;
        if (siteIds.Length == 0)
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "Assign at least one site, or all sites of the assigned companies.");
        var outside = await con.ExecuteScalarAsync<long>(
            "select count(*) from unnest(@siteIds::bigint[]) x(id) where not exists (select 1 from master.sites s where s.id = x.id and s.company_id = any(@companyIds))",
            new { siteIds, companyIds });
        return outside == 0 ? null
            : Results.Problem(statusCode: 400, title: "VAL-001", detail: "Every site must belong to one of the user's assigned companies.");
    }

    private static Task WriteSites(NpgsqlConnection con, NpgsqlTransaction tx, long userId, long[] siteIds) =>
        con.ExecuteAsync("""
            delete from auth.user_sites where user_id = @userId;
            insert into auth.user_sites (user_id, site_id) select @userId, unnest(@siteIds::bigint[]) on conflict do nothing;
            """, new { userId, siteIds }, tx);

    public static async Task<IResult> CreateUser(NpgsqlDataSource ds, UserScope scope, CreateUserRequest req)
    {
        if (await RequireAdmin(scope, "insert") is { } err) return err;
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "Valid email required.");
        if (!Roles.Contains(req.Role))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "Unknown role.");
        if (req.Role is "Super Admin" && scope.Role != "Super Admin")
            return Results.Problem(statusCode: 403, title: "AUTH-003", detail: "Only a Super Admin can create Super Admins.");
        var companyIds = req.CompanyIds ?? Array.Empty<long>();
        var siteIds = req.AllSites ? Array.Empty<long>() : (req.SiteIds ?? Array.Empty<long>()).Distinct().ToArray();
        // Every non-Super-Admin must belong to at least one company; a Super Admin spans all.
        if (req.Role != "Super Admin" && companyIds.Length == 0)
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "Assign at least one company (only a Super Admin spans all companies).");
        if (Passwords.Check(req.Password, req.Email) is { } pwErr)
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: pwErr);

        await using var con = await ds.OpenConnectionAsync();
        var dup = await con.ExecuteScalarAsync<long?>(
            "select id from auth.users where lower(email) = lower(@e)", new { e = req.Email });
        if (dup != null)
            return Results.Problem(statusCode: 409, title: "MD-001", detail: "A user with this email already exists.");
        if (await CheckSites(con, scope, req.Role, req.AllSites, siteIds, companyIds) is { } siteErr) return siteErr;

        await using var tx = await con.BeginTransactionAsync();
        var id = await con.ExecuteScalarAsync<long>("""
            insert into auth.users (email, full_name, role, password_hash, all_entities)
            values (@Email, @FullName, @Role, @hash, @all) returning id
            """, new { req.Email, req.FullName, req.Role, hash = BCrypt.Net.BCrypt.HashPassword(req.Password, 11),
                       all = req.AllSites || req.Role == Auth.SuperAdmin }, tx);
        if (companyIds.Length > 0)
            await con.ExecuteAsync(
                "insert into auth.user_companies (user_id, company_id) select @id, unnest(@ids::bigint[]) on conflict do nothing",
                new { id, ids = companyIds }, tx);
        await WriteSites(con, tx, id, siteIds);
        await tx.CommitAsync();

        await Notifications.Emit(con, "security", $"User created — {req.Email}",
            $"Role {req.Role}, scope {(req.AllSites ? "all sites" : $"{siteIds.Length} site(s)")} · by {scope.Email}");
        Audit.Log("admin.user.create", scope, "user", id.ToString(), $"Created user {req.Email}",
            new { req.Email, req.FullName, req.Role, req.AllSites, siteIds, companyIds });
        return Results.Ok(new { id });
    }

    public static async Task<IResult> SetStatus(NpgsqlDataSource ds, UserScope scope, long id, StatusRequest req)
    {
        if (await RequireAdmin(scope, "edit") is { } err) return err;
        if (req.Status is not ("active" or "disabled"))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "status must be active|disabled.");
        if (id == scope.UserId)
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "You cannot disable your own account.");

        await using var con = await ds.OpenConnectionAsync();
        // tokens_valid_from retires every token issued before now, so disabling an account ends
        // the sessions it already has rather than waiting up to 8 hours for them to expire (AR-01).
        var email = await con.ExecuteScalarAsync<string?>(
            "update auth.users set status = @s, tokens_valid_from = now() where id = @id returning email",
            new { s = req.Status, id });
        if (email is null) return Results.Problem(statusCode: 404, title: "VAL-001", detail: "User not found.");
        Sessions.Invalidate(id);

        await Notifications.Emit(con, "security", $"User {req.Status} — {email}", $"By {scope.Email}");
        Audit.Log("admin.user.status", scope, "user", id.ToString(), $"Set {email} to {req.Status}",
            new { email, status = req.Status });
        return Results.Ok(new { id, status = req.Status });
    }

    public static async Task<IResult> ResetPassword(NpgsqlDataSource ds, UserScope scope, long id, ResetPasswordRequest req)
    {
        if (await RequireAdmin(scope, "edit") is { } err) return err;
        await using var con = await ds.OpenConnectionAsync();
        var target = await con.ExecuteScalarAsync<string?>("select email from auth.users where id = @id", new { id });
        if (target is null) return Results.Problem(statusCode: 404, title: "VAL-001", detail: "User not found.");
        if (Passwords.Check(req.Password, target) is { } pwErr)
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: pwErr);
        // A reset must end the sessions the old password left behind (AR-01).
        var email = await con.ExecuteScalarAsync<string?>(
            "update auth.users set password_hash = @h, tokens_valid_from = now() where id = @id returning email",
            new { h = BCrypt.Net.BCrypt.HashPassword(req.Password, 11), id });
        Sessions.Invalidate(id);
        await Notifications.Emit(con, "security", $"Password reset — {email}", $"By {scope.Email}");
        Audit.Log("admin.user.reset", scope, "user", id.ToString(), $"Reset password for {email}", new { email });
        return Results.Ok(new { id });
    }

    public static async Task<IResult> SetSites(NpgsqlDataSource ds, UserScope scope, long id, SitesRequest req)
    {
        if (await RequireAdmin(scope, "edit") is { } err) return err;
        await using var con = await ds.OpenConnectionAsync();
        var target = await con.QueryFirstOrDefaultAsync<(string email, string role)>(
            "select email, role from auth.users where id = @id", new { id });
        if (target.email is null) return Results.Problem(statusCode: 404, title: "VAL-001", detail: "User not found.");

        var siteIds = req.AllSites ? Array.Empty<long>() : (req.SiteIds ?? Array.Empty<long>()).Distinct().ToArray();
        var companyIds = (await con.QueryAsync<long>(
            "select company_id from auth.user_companies where user_id = @id", new { id })).ToArray();
        if (await CheckSites(con, scope, target.role, req.AllSites, siteIds, companyIds) is { } siteErr) return siteErr;

        // Session middleware re-reads scope each request; invalidate so the change lands at once.
        await using var tx = await con.BeginTransactionAsync();
        await con.ExecuteAsync("update auth.users set all_entities = @a where id = @id",
            new { a = req.AllSites || target.role == Auth.SuperAdmin, id }, tx);
        await WriteSites(con, tx, id, siteIds);
        await tx.CommitAsync();
        Sessions.Invalidate(id);
        Audit.Log("admin.user.scope", scope, "user", id.ToString(), $"Set sites for {target.email}",
            new { req.AllSites, siteIds });
        return Results.Ok(new { id });
    }

    // ---------- master data (FR-A3/A4): Company → Sites & TPB permits ----------
    public static async Task<IResult> Master(NpgsqlDataSource ds, UserScope scope)
    {
        if (await RequireAdmin(scope) is { } err) return err;
        await using var con = await ds.OpenConnectionAsync();
        var sites = await con.QueryAsync("""
            select s.id, s.company_id as "companyId", s.name,
                   (select count(*) from auth.user_sites us where us.site_id = s.id) as "users"
            from master.sites s order by s.name
            """);
        var permits = await con.QueryAsync("""
            select t.id, t.permit_no as "permitNo", t.company_id as "companyId",
                   t.site_id as "siteId", s.name as "siteName",
                   (select count(*) from bc.documents d where d.tpb_id = t.id) as "documents"
            from master.tpb_permits t
            left join master.sites s on s.id = t.site_id
            order by t.permit_no
            """);
        // Per-site report and column lists, and the reports (with their columns) that can be picked.
        var (siteReports, siteColumns) = await SiteReports.All(con);
        var reports = SiteReports.Assignable.Select(r => new
        {
            key = r.Key, title = r.Title, page = r.Page,
            fields = r.Fields.Select(f => new { name = f.Name, label = f.Display, group = f.Group, optIn = f.OptIn })
        });
        return Results.Ok(new { sites, permits, siteReports, siteColumns, reports });
    }

    private static bool LooksLikeTestData(string v) =>
        v.Contains("test", StringComparison.OrdinalIgnoreCase) || v.Contains("dummy", StringComparison.OrdinalIgnoreCase);

    public static async Task<IResult> AddSite(NpgsqlDataSource ds, UserScope scope, SiteRequest req)
    {
        if (await RequireAdmin(scope, "insert") is { } err) return err;
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "name required.");
        if (LooksLikeTestData(req.Name))
            return Results.Problem(statusCode: 409, title: "MD-001", detail: "Test entries are blocked (FR-A4).");
        await using var con = await ds.OpenConnectionAsync();
        if (!await con.ExecuteScalarAsync<bool>("select exists (select 1 from master.companies where id = @CompanyId)", req))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "A site belongs to a company — choose an existing one.");
        try
        {
            var id = await con.ExecuteScalarAsync<long>(
                "insert into master.sites (company_id, name) values (@CompanyId, @Name) returning id", req);
            Audit.Log("master.site.create", scope, "site", id.ToString(), $"Added site {req.Name}", req);
            return Results.Ok(new { id });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Problem(statusCode: 409, title: "MD-001", detail: "Duplicate site for this company (FR-A4).");
        }
    }

    public static async Task<IResult> AddPermit(NpgsqlDataSource ds, UserScope scope, PermitRequest req)
    {
        if (await RequireAdmin(scope, "insert") is { } err) return err;
        if (string.IsNullOrWhiteSpace(req.PermitNo))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "permitNo required.");
        if (LooksLikeTestData(req.PermitNo))
            return Results.Problem(statusCode: 409, title: "MD-001", detail: "Test entries are blocked (FR-A4) — the legacy 'TESTING' permit stays out.");
        await using var con = await ds.OpenConnectionAsync();
        if (req.SiteId is not null && !await con.ExecuteScalarAsync<bool>(
                "select exists (select 1 from master.sites where id = @SiteId and company_id = @CompanyId)", req))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "The chosen site is not in the chosen company.");
        try
        {
            var id = await con.ExecuteScalarAsync<long>(
                "insert into master.tpb_permits (company_id, site_id, permit_no) values (@CompanyId, @SiteId, @PermitNo) returning id", req);
            Audit.Log("master.permit.create", scope, "tpb_permit", id.ToString(), $"Added TPB permit {req.PermitNo}", req);
            return Results.Ok(new { id });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Problem(statusCode: 409, title: "MD-001", detail: "Duplicate permit number (FR-A4).");
        }
    }
}
