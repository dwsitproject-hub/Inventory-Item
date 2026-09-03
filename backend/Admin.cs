using Dapper;
using Npgsql;

namespace BcInventory.Api;

public record CreateUserRequest(string Email, string FullName, string Role, bool AllEntities, long? EntityId, long? SiteId, string Password);
public record ResetPasswordRequest(string Password);
public record StatusRequest(string Status);
public record EntityRequest(string Code, string Name);
public record SiteRequest(long EntityId, string Name);
public record PermitRequest(long EntityId, long? SiteId, string PermitNo);

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
                   u.all_entities as "allEntities", u.entity_id as "entityId", u.site_id as "siteId",
                   e.name as "entityName", s.name as "siteName", u.created_at as "createdAt"
            from auth.users u
            left join master.entities e on e.id = u.entity_id
            left join master.sites s on s.id = u.site_id
            order by u.id
            """);
        return Results.Ok(new { users, roles = Roles });
    }

    public static async Task<IResult> CreateUser(NpgsqlDataSource ds, UserScope scope, CreateUserRequest req)
    {
        if (await RequireAdmin(scope, "insert") is { } err) return err;
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "Valid email required.");
        if (!Roles.Contains(req.Role))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "Unknown role.");
        if (req.Role is "Super Admin" && scope.Role != "Super Admin")
            return Results.Problem(statusCode: 403, title: "AUTH-003", detail: "Only a Super Admin can create Super Admins.");
        if (req.AllEntities && scope.Role != "Super Admin")
            return Results.Problem(statusCode: 403, title: "AUTH-003", detail: "Only a Super Admin can grant all-entities scope.");
        if (!req.AllEntities && req.EntityId is null)
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "entityId required unless allEntities.");
        if (Passwords.Check(req.Password, req.Email) is { } pwErr)
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: pwErr);

        await using var con = await ds.OpenConnectionAsync();
        var dup = await con.ExecuteScalarAsync<long?>(
            "select id from auth.users where lower(email) = lower(@e)", new { e = req.Email });
        if (dup != null)
            return Results.Problem(statusCode: 409, title: "MD-001", detail: "A user with this email already exists.");

        var id = await con.ExecuteScalarAsync<long>("""
            insert into auth.users (email, full_name, role, password_hash, all_entities, entity_id, site_id)
            values (@Email, @FullName, @Role, @hash, @AllEntities, @EntityId, @SiteId) returning id
            """, new { req.Email, req.FullName, req.Role, hash = BCrypt.Net.BCrypt.HashPassword(req.Password, 11), req.AllEntities, req.EntityId, req.SiteId });

        await Notifications.Emit(con, "security", $"User created — {req.Email}",
            $"Role {req.Role}, scope {(req.AllEntities ? "all entities" : $"entity #{req.EntityId}")} · by {scope.Email}");
        Audit.Log("admin.user.create", scope, "user", id.ToString(), $"Created user {req.Email}",
            new { req.Email, req.FullName, req.Role, req.AllEntities, req.EntityId, req.SiteId });
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

    // ---------- master data (FR-A3/A4) ----------
    public static async Task<IResult> Master(NpgsqlDataSource ds, UserScope scope)
    {
        if (await RequireAdmin(scope) is { } err) return err;
        await using var con = await ds.OpenConnectionAsync();
        var entities = await con.QueryAsync("""select id, code, name from master.entities order by name""");
        var sites = await con.QueryAsync("""select s.id, s.entity_id as "entityId", s.name, e.name as "entityName" from master.sites s join master.entities e on e.id = s.entity_id order by e.name, s.name""");
        var permits = await con.QueryAsync("""
            select t.id, t.permit_no as "permitNo", t.entity_id as "entityId", e.name as "entityName",
                   t.site_id as "siteId", s.name as "siteName",
                   (select count(*) from bc.documents d where d.tpb_id = t.id) as "documents"
            from master.tpb_permits t
            join master.entities e on e.id = t.entity_id
            left join master.sites s on s.id = t.site_id
            order by t.permit_no
            """);
        return Results.Ok(new { entities, sites, permits });
    }

    private static bool LooksLikeTestData(string v) =>
        v.Contains("test", StringComparison.OrdinalIgnoreCase) || v.Contains("dummy", StringComparison.OrdinalIgnoreCase);

    public static async Task<IResult> AddEntity(NpgsqlDataSource ds, UserScope scope, EntityRequest req)
    {
        if (await RequireAdmin(scope, "insert") is { } err) return err;
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "code and name required.");
        if (LooksLikeTestData(req.Name) || LooksLikeTestData(req.Code))
            return Results.Problem(statusCode: 409, title: "MD-001", detail: "Test entries are blocked in production master data (FR-A4).");
        await using var con = await ds.OpenConnectionAsync();
        try
        {
            var id = await con.ExecuteScalarAsync<long>(
                "insert into master.entities (code, name) values (@Code, @Name) returning id", req);
            Audit.Log("master.entity.create", scope, "entity", id.ToString(), $"Added entity {req.Code} — {req.Name}", req);
            return Results.Ok(new { id });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Problem(statusCode: 409, title: "MD-001", detail: "Duplicate entity code or name (FR-A4).");
        }
    }

    public static async Task<IResult> AddSite(NpgsqlDataSource ds, UserScope scope, SiteRequest req)
    {
        if (await RequireAdmin(scope, "insert") is { } err) return err;
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "name required.");
        if (LooksLikeTestData(req.Name))
            return Results.Problem(statusCode: 409, title: "MD-001", detail: "Test entries are blocked (FR-A4).");
        await using var con = await ds.OpenConnectionAsync();
        try
        {
            var id = await con.ExecuteScalarAsync<long>(
                "insert into master.sites (entity_id, name) values (@EntityId, @Name) returning id", req);
            Audit.Log("master.site.create", scope, "site", id.ToString(), $"Added site {req.Name}", req);
            return Results.Ok(new { id });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Problem(statusCode: 409, title: "MD-001", detail: "Duplicate site for this entity (FR-A4).");
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
        try
        {
            var id = await con.ExecuteScalarAsync<long>(
                "insert into master.tpb_permits (entity_id, site_id, permit_no) values (@EntityId, @SiteId, @PermitNo) returning id", req);
            Audit.Log("master.permit.create", scope, "tpb_permit", id.ToString(), $"Added TPB permit {req.PermitNo}", req);
            return Results.Ok(new { id });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.Problem(statusCode: 409, title: "MD-001", detail: "Duplicate permit number (FR-A4).");
        }
    }
}
