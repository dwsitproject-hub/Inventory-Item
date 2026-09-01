using Dapper;
using Npgsql;

namespace BcInventory.Api;

public record NotificationSubscriptionRow(long UserId, string Event, bool InApp, bool Email);
public record NotificationSubscriptionUpdate(NotificationSubscriptionRow[] Rows);

/// <summary>In-app + e-mail notifications (FR-N1/N3/N4/N6 subset).</summary>
public static class Notifications
{
    private static NpgsqlDataSource? _ds;
    private static IConfiguration? _cfg;

    public static void Configure(NpgsqlDataSource ds, IConfiguration cfg) { _ds = ds; _cfg = cfg; }

    /// <summary>
    /// The events a user can subscribe to. <c>DefaultRoles</c> is the routing that applies until an
    /// administrator sets something explicit — it reproduces the hard-coded recipient lists these
    /// alerts had before routing became configurable, so upgrading changes nobody's mail.
    /// </summary>
    public record EventDef(string Key, string Title, string Description, string[]? DefaultRoles);

    public static readonly EventDef[] Events =
    {
        new("upload", "File ingested",
            "A BC report file was uploaded and accepted.", null),
        new("quarantine", "Rows quarantined",
            "An upload succeeded but some rows failed validation and need review.",
            new[] { Permissions.SuperAdmin, "Admin", "Data Steward" }),
        new("error", "Ingestion failed",
            "An upload could not be parsed and nothing was stored.",
            new[] { Permissions.SuperAdmin, "Admin", "Data Steward" }),
        new("security", "Security & administration",
            "A user was created, disabled or reset, or role permissions changed.",
            new[] { Permissions.SuperAdmin, "Admin" }),
    };

    /// <summary>Roles this event reaches when a user has no explicit setting. Null DefaultRoles means everyone.</summary>
    private static string[] Defaults(string eventType) =>
        Events.FirstOrDefault(e => e.Key == eventType)?.DefaultRoles ?? Permissions.Roles;

    private record Recipient(long UserId, string Email, bool InApp, bool WantsEmail);

    /// <summary>
    /// Emit an event. Who receives it, and through which channel, comes from
    /// app.notification_subscriptions — falling back to the event's default roles for any user
    /// without an explicit row. E-mail is sent per recipient in the background; delivery status
    /// is recorded (FR-N6).
    /// </summary>
    public static async Task Emit(NpgsqlConnection con, string eventType, string title, string? body)
    {
        var defaults = Defaults(eventType);
        var recipients = (await con.QueryAsync<Recipient>("""
            select u.id as "UserId", u.email as "Email",
                   coalesce(s.in_app, u.role = any(@defaults)) as "InApp",
                   coalesce(s.email,  u.role = any(@defaults)) as "WantsEmail"
            from auth.users u
            left join app.notification_subscriptions s
                   on s.user_id = u.id and s.event_type = @eventType
            where u.status = 'active'
            """, new { eventType, defaults }))
            .Where(r => r.InApp || r.WantsEmail)
            .ToList();

        if (recipients.Count == 0) return;

        var id = await con.ExecuteScalarAsync<long>(
            "insert into app.notifications (event_type, title, body) values (@eventType, @title, @body) returning id",
            new { eventType, title, body });

        // One row per recipient of either channel: an e-mail-only recipient still needs somewhere
        // to record its delivery status. in_app decides whether the bell shows it.
        foreach (var r in recipients)
            await con.ExecuteAsync("""
                insert into app.notification_deliveries (notification_id, user_id, in_app)
                values (@id, @uid, @inApp)
                on conflict (notification_id, user_id) do nothing
                """, new { id, uid = r.UserId, inApp = r.InApp });

        // e-mail channel: background send, never blocks the request path
        var byEmail = recipients.Where(r => r.WantsEmail).ToList();
        if (byEmail.Count > 0 && _ds != null && _cfg != null && Email.Configured(_cfg))
        {
            var ds = _ds; var cfg = _cfg;
            _ = Task.Run(async () =>
            {
                foreach (var r in byEmail)
                {
                    string status = "sent"; string? err = null;
                    try { Email.Send(cfg, r.Email, title, body ?? title); }
                    catch (Exception ex) { status = "failed"; err = ex.Message; Console.WriteLine($"[email] {r.Email}: {ex.Message}"); }
                    try
                    {
                        await using var c2 = await ds.OpenConnectionAsync();
                        await c2.ExecuteAsync(
                            "update app.notification_deliveries set email_status = @status, email_error = @err where notification_id = @id and user_id = @uid",
                            new { status, err, id, uid = r.UserId });
                    }
                    catch (Exception ex) { Console.WriteLine("[email] status update failed: " + ex.Message); }
                }
            });
        }
    }

    public static async Task<IResult> List(NpgsqlDataSource ds, UserScope scope)
    {
        await using var con = await ds.OpenConnectionAsync();
        var items = (await con.QueryAsync("""
            select n.id, n.event_type as "eventType", n.title, n.body,
                   n.created_at as "createdAt", d.read_at is not null as "read",
                   d.email_status as "emailStatus"
            from app.notification_deliveries d
            join app.notifications n on n.id = d.notification_id
            where d.user_id = @uid and d.in_app
            order by n.created_at desc limit 30
            """, new { uid = scope.UserId })).ToList();
        var unread = await con.ExecuteScalarAsync<long>(
            "select count(*) from app.notification_deliveries where user_id = @uid and in_app and read_at is null",
            new { uid = scope.UserId });
        return Results.Ok(new { unread, items });
    }

    public static async Task<IResult> MarkRead(NpgsqlDataSource ds, UserScope scope, long id)
    {
        await using var con = await ds.OpenConnectionAsync();
        await con.ExecuteAsync(
            "update app.notification_deliveries set read_at = now() where user_id = @uid and notification_id = @id and read_at is null",
            new { uid = scope.UserId, id });
        return Results.Ok(new { ok = true });
    }

    public static async Task<IResult> MarkAllRead(NpgsqlDataSource ds, UserScope scope)
    {
        await using var con = await ds.OpenConnectionAsync();
        var n = await con.ExecuteAsync(
            "update app.notification_deliveries set read_at = now() where user_id = @uid and in_app and read_at is null",
            new { uid = scope.UserId });
        return Results.Ok(new { marked = n });
    }

    // ---------- administration: who receives what ----------

    /// <summary>The routing matrix: every active-or-disabled user against every event.</summary>
    public static async Task<IResult> Subscriptions(NpgsqlDataSource ds, UserScope scope)
    {
        if (await Permissions.Require(scope, "admin", "view") is { } err) return err;

        // Read straight from the database, never a cache: the admin screen posts this matrix back
        // on save, so serving a stale copy would let a save write back settings that are not current.
        await using var con = await ds.OpenConnectionAsync();
        var stored = (await con.QueryAsync<(long userId, string eventType, bool inApp, bool email)>(
                "select user_id, event_type, in_app, email from app.notification_subscriptions"))
            .ToDictionary(r => r.userId + "|" + r.eventType, r => (r.inApp, r.email));

        var users = (await con.QueryAsync<(long id, string email, string fullName, string role, string status)>("""
            select id, email, full_name as "fullName", role, status
            from auth.users order by id
            """)).ToList();

        var matrix = users.Select(u => new
        {
            u.id,
            u.email,
            u.fullName,
            u.role,
            u.status,
            events = Events.Select(e =>
            {
                var fallback = (e.DefaultRoles ?? Permissions.Roles).Contains(u.role);
                var explicitSetting = stored.TryGetValue(u.id + "|" + e.Key, out var v);
                return new
                {
                    @event = e.Key,
                    inApp = explicitSetting ? v.inApp : fallback,
                    email = explicitSetting ? v.email : fallback,
                    isDefault = !explicitSetting
                };
            })
        });

        return Results.Ok(new
        {
            events = Events.Select(e => new
            {
                e.Key,
                e.Title,
                e.Description,
                defaultRoles = e.DefaultRoles ?? Permissions.Roles
            }),
            users = matrix,
            smtpConfigured = _cfg != null && Email.Configured(_cfg),
            canEdit = await Permissions.Require(scope, "admin", "edit") is null
        });
    }

    public static async Task<IResult> UpdateSubscriptions(NpgsqlDataSource ds, UserScope scope, NotificationSubscriptionUpdate req)
    {
        if (await Permissions.Require(scope, "admin", "edit") is { } err) return err;

        var rows = req.Rows ?? Array.Empty<NotificationSubscriptionRow>();
        foreach (var r in rows)
            if (!Events.Any(e => e.Key == r.Event))
                return Results.Problem(statusCode: 400, title: "VAL-001", detail: $"Unknown notification event '{r.Event}'.");

        await using var con = await ds.OpenConnectionAsync();
        var known = (await con.QueryAsync<long>("select id from auth.users")).ToHashSet();
        foreach (var r in rows)
            if (!known.Contains(r.UserId))
                return Results.Problem(statusCode: 400, title: "VAL-001", detail: $"Unknown user #{r.UserId}.");

        foreach (var r in rows)
            await con.ExecuteAsync("""
                insert into app.notification_subscriptions (user_id, event_type, in_app, email)
                values (@UserId, @Event, @InApp, @Email)
                on conflict (user_id, event_type) do update
                  set in_app = excluded.in_app, email = excluded.email, updated_at = now()
                """, r);

        Audit.Log("admin.notifications.routing", scope, "notifications", null,
            $"Updated notification routing for {rows.Select(r => r.UserId).Distinct().Count()} user(s)", rows);
        return Results.Ok(new { saved = rows.Length });
    }
}
