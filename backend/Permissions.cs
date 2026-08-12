using System.Collections.Concurrent;
using Dapper;
using Npgsql;

namespace BcInventory.Api;

public record PagePermission(string Page, bool View, bool Insert, bool Edit);
public record RolePermissionUpdate(string Role, PagePermission[] Pages);

/// <summary>
/// Configurable role → page → (view / insert / edit) matrix, enforced server-side.
/// Two invariants protect the system from being locked out or escalated:
///   • Super Admin always has everything and its row cannot be edited.
///   • Only a Super Admin may change the matrix — an Admin granting itself more rights
///     would be privilege escalation.
/// </summary>
public static class Permissions
{
    public const string SuperAdmin = "Super Admin";

    public record PageDef(string Key, string Title, bool HasInsert, bool HasEdit, string InsertMeans, string EditMeans);

    /// <summary>Screens that can be governed. Actions that make no sense for a page are not offered.</summary>
    public static readonly PageDef[] Pages =
    {
        new("dashboard", "Dashboard", false, false, "", ""),
        new("reports", "Reports", false, true, "", "Export data & save views"),
        new("movement", "Inventory Movement", false, true, "", "Export data & save views"),
        new("ingestion", "Ingestion & Upload", true, true, "Upload files", "Resolve quarantined rows"),
        new("lpm", "LPM / Reconciliation", false, false, "", ""),
        new("admin", "Administration", true, true, "Create users & master data", "Modify users & master data"),
        new("audit", "Audit Log", false, true, "", "Export the audit log"),
    };

    public static readonly string[] Roles =
        { SuperAdmin, "Admin", "HO BC User", "Site BC User", "Data Steward", "Auditor" };

    /// <summary>Defaults mirror the PRD §6.4 capability matrix.</summary>
    private static readonly (string role, string page, bool v, bool i, bool e)[] Defaults =
    {
        ("Admin","dashboard",true,false,false), ("Admin","reports",true,false,true),
        ("Admin","movement",true,false,true),   ("Admin","ingestion",true,true,true),
        ("Admin","lpm",true,false,false),       ("Admin","admin",true,true,true),
        ("Admin","audit",true,false,true),

        ("HO BC User","dashboard",true,false,false), ("HO BC User","reports",true,false,true),
        ("HO BC User","movement",true,false,true),   ("HO BC User","ingestion",true,false,false),
        ("HO BC User","lpm",true,false,false),       ("HO BC User","admin",false,false,false),
        ("HO BC User","audit",false,false,false),

        ("Site BC User","dashboard",true,false,false), ("Site BC User","reports",true,false,true),
        ("Site BC User","movement",true,false,true),   ("Site BC User","ingestion",false,false,false),
        ("Site BC User","lpm",true,false,false),       ("Site BC User","admin",false,false,false),
        ("Site BC User","audit",false,false,false),

        ("Data Steward","dashboard",true,false,false), ("Data Steward","reports",true,false,true),
        ("Data Steward","movement",true,false,true),   ("Data Steward","ingestion",true,true,true),
        ("Data Steward","lpm",true,false,false),       ("Data Steward","admin",false,false,false),
        ("Data Steward","audit",false,false,false),

        // Auditor is read-only by design: view everywhere it matters, no data export (PRD §5)
        ("Auditor","dashboard",true,false,false), ("Auditor","reports",true,false,false),
        ("Auditor","movement",true,false,false),  ("Auditor","ingestion",true,false,false),
        ("Auditor","lpm",true,false,false),       ("Auditor","admin",false,false,false),
        ("Auditor","audit",true,false,true),
    };

    private static NpgsqlDataSource? _ds;
    private static readonly ConcurrentDictionary<string, PagePermission> _cache = new();
    private static DateTime _loadedAt = DateTime.MinValue;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public static void Configure(NpgsqlDataSource ds) => _ds = ds;

    public static async Task EnsureSeeded(NpgsqlDataSource ds)
    {
        await using var con = await ds.OpenConnectionAsync();
        foreach (var (role, page, v, i, e) in Defaults)
            await con.ExecuteAsync("""
                insert into auth.role_permissions (role, page, can_view, can_insert, can_edit)
                values (@role, @page, @v, @i, @e)
                on conflict (role, page) do nothing
                """, new { role, page, v, i, e });
        // Super Admin is implicit (always full) but stored so the matrix reads completely
        foreach (var p in Pages)
            await con.ExecuteAsync("""
                insert into auth.role_permissions (role, page, can_view, can_insert, can_edit)
                values (@role, @page, true, true, true)
                on conflict (role, page) do update set can_view = true, can_insert = true, can_edit = true
                """, new { role = SuperAdmin, page = p.Key });
    }

    private static async Task Load()
    {
        if (_ds is null) return;
        if (DateTime.UtcNow - _loadedAt < TimeSpan.FromSeconds(30) && !_cache.IsEmpty) return;
        await _lock.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _loadedAt < TimeSpan.FromSeconds(30) && !_cache.IsEmpty) return;
            await using var con = await _ds.OpenConnectionAsync();
            var rows = await con.QueryAsync<(string role, string page, bool v, bool i, bool e)>(
                "select role, page, can_view, can_insert, can_edit from auth.role_permissions");
            // Fill first, then publish. Clearing in place would let a concurrent reader observe
            // an empty cache and conclude "no permissions" — which is indistinguishable from a
            // real denial.
            var fresh = rows.ToDictionary(r => r.role + "|" + r.page,
                                          r => new PagePermission(r.page, r.v, r.i, r.e));
            foreach (var kv in fresh) _cache[kv.Key] = kv.Value;
            foreach (var key in _cache.Keys.Where(k => !fresh.ContainsKey(k)).ToList())
                _cache.TryRemove(key, out _);
            _loadedAt = DateTime.UtcNow;
        }
        finally { _lock.Release(); }
    }

    public static void Invalidate() => _loadedAt = DateTime.MinValue;

    public static async Task<PagePermission> For(string role, string page)
    {
        if (role == SuperAdmin) return new PagePermission(page, true, true, true);
        await Load();
        return _cache.TryGetValue(role + "|" + page, out var p) ? p : new PagePermission(page, false, false, false);
    }

    public static async Task<Dictionary<string, PagePermission>> Effective(string role)
    {
        var result = new Dictionary<string, PagePermission>();
        foreach (var p in Pages) result[p.Key] = await For(role, p.Key);
        return result;
    }

    /// <summary>Returns a 403 problem when the caller's role lacks the action, otherwise null.</summary>
    public static async Task<IResult?> Require(UserScope scope, string page, string action)
    {
        var p = await For(scope.Role, page);
        var ok = action switch
        {
            "view" => p.View,
            "insert" => p.Insert,
            "edit" => p.Edit,
            _ => false
        };
        if (ok) return null;
        var title = Pages.FirstOrDefault(x => x.Key == page)?.Title ?? page;
        return Results.Problem(statusCode: 403, title: "AUTH-003",
            detail: $"Your role ({scope.Role}) is not allowed to {action} on {title}. Ask an administrator to adjust Role Management.");
    }

    // ---------- endpoints ----------
    public static async Task<IResult> List(NpgsqlDataSource ds, UserScope scope)
    {
        if (await Require(scope, "admin", "view") is { } err) return err;

        // Read straight from the database, never the cache. The admin screen sends this matrix
        // back on save, so serving a momentarily-stale or half-loaded cache here would let a
        // save silently strip every permission it did not know about.
        await using var con = await ds.OpenConnectionAsync();
        var stored = (await con.QueryAsync<(string role, string page, bool v, bool i, bool e)>(
                "select role, page, can_view, can_insert, can_edit from auth.role_permissions"))
            .ToDictionary(r => r.role + "|" + r.page, r => new PagePermission(r.page, r.v, r.i, r.e));

        var matrix = Roles.Select(role => new
        {
            role,
            locked = role == SuperAdmin,
            pages = Pages.Select(p =>
            {
                var perm = role == SuperAdmin
                    ? new PagePermission(p.Key, true, true, true)
                    : stored.GetValueOrDefault(role + "|" + p.Key, new PagePermission(p.Key, false, false, false));
                return new { page = p.Key, perm.View, perm.Insert, perm.Edit };
            })
        });
        return Results.Ok(new
        {
            pages = Pages.Select(p => new { p.Key, p.Title, p.HasInsert, p.HasEdit, p.InsertMeans, p.EditMeans }),
            roles = matrix,
            canEdit = scope.Role == SuperAdmin
        });
    }

    public static async Task<IResult> Update(NpgsqlDataSource ds, UserScope scope, RolePermissionUpdate req)
    {
        if (scope.Role != SuperAdmin)
            return Results.Problem(statusCode: 403, title: "AUTH-003",
                detail: "Only a Super Admin can change role permissions (changing your own rights would be privilege escalation).");
        if (req.Role == SuperAdmin)
            return Results.Problem(statusCode: 400, title: "VAL-001",
                detail: "Super Admin always has full access and cannot be restricted — this prevents locking everyone out.");
        if (!Roles.Contains(req.Role))
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: $"Unknown role '{req.Role}'.");

        await using var con = await ds.OpenConnectionAsync();
        foreach (var p in req.Pages ?? Array.Empty<PagePermission>())
        {
            var def = Pages.FirstOrDefault(x => x.Key == p.Page);
            if (def is null)
                return Results.Problem(statusCode: 400, title: "VAL-001", detail: $"Unknown page '{p.Page}'.");
            // an action the page does not offer can never be granted
            var insert = def.HasInsert && p.Insert;
            var edit = def.HasEdit && p.Edit;
            // insert/edit without view would be unreachable in the UI — keep the matrix coherent
            var view = p.View || insert || edit;
            await con.ExecuteAsync("""
                insert into auth.role_permissions (role, page, can_view, can_insert, can_edit)
                values (@role, @page, @view, @insert, @edit)
                on conflict (role, page) do update
                  set can_view = excluded.can_view, can_insert = excluded.can_insert,
                      can_edit = excluded.can_edit, updated_at = now()
                """, new { role = req.Role, page = p.Page, view, insert, edit });
        }
        Invalidate();

        await Notifications.Emit(con, "security", $"Role permissions changed — {req.Role}",
            $"Updated by {scope.Email}", new[] { SuperAdmin, "Admin" });
        Audit.Log("admin.role.permissions", scope, "role", req.Role,
            $"Updated page permissions for {req.Role}", req.Pages);
        return Results.Ok(new { role = req.Role, saved = true });
    }
}
