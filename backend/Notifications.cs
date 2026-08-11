using Dapper;
using Npgsql;

namespace BcInventory.Api;

/// <summary>In-app + e-mail notifications (FR-N1/N3/N4/N6 subset).</summary>
public static class Notifications
{
    private static NpgsqlDataSource? _ds;
    private static IConfiguration? _cfg;

    public static void Configure(NpgsqlDataSource ds, IConfiguration cfg) { _ds = ds; _cfg = cfg; }

    /// <summary>Emit an event to all active users, or only to the given roles. E-mail is sent per recipient in the background; delivery status is recorded (FR-N6).</summary>
    public static async Task Emit(NpgsqlConnection con, string eventType, string title, string? body, string[]? roles = null)
    {
        var id = await con.ExecuteScalarAsync<long>(
            "insert into app.notifications (event_type, title, body) values (@eventType, @title, @body) returning id",
            new { eventType, title, body });
        await con.ExecuteAsync($"""
            insert into app.notification_deliveries (notification_id, user_id)
            select @id, u.id from auth.users u
            where u.status = 'active'{(roles is { Length: > 0 } ? " and u.role = any(@roles)" : "")}
            on conflict do nothing
            """, new { id, roles });

        var recipients = (await con.QueryAsync<(long userId, string email)>("""
            select d.user_id, u.email from app.notification_deliveries d
            join auth.users u on u.id = d.user_id
            where d.notification_id = @id
            """, new { id })).ToList();

        // e-mail channel: background send, never blocks the request path
        if (_ds != null && _cfg != null && Email.Configured(_cfg))
        {
            var ds = _ds; var cfg = _cfg;
            _ = Task.Run(async () =>
            {
                foreach (var r in recipients)
                {
                    string status = "sent"; string? err = null;
                    try { Email.Send(cfg, r.email, title, body ?? title); }
                    catch (Exception ex) { status = "failed"; err = ex.Message; Console.WriteLine($"[email] {r.email}: {ex.Message}"); }
                    try
                    {
                        await using var c2 = await ds.OpenConnectionAsync();
                        await c2.ExecuteAsync(
                            "update app.notification_deliveries set email_status = @status, email_error = @err where notification_id = @id and user_id = @uid",
                            new { status, err, id, uid = r.userId });
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
            where d.user_id = @uid
            order by n.created_at desc limit 30
            """, new { uid = scope.UserId })).ToList();
        var unread = await con.ExecuteScalarAsync<long>(
            "select count(*) from app.notification_deliveries where user_id = @uid and read_at is null",
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
            "update app.notification_deliveries set read_at = now() where user_id = @uid and read_at is null",
            new { uid = scope.UserId });
        return Results.Ok(new { marked = n });
    }
}
