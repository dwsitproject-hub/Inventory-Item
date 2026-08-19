using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace BcInventory.Api;

/// <summary>
/// Immutable audit trail (FR-A7): who did what, when — report runs/exports, ingestion events,
/// master-data and user/access changes. Writes are fire-and-forget so auditing can never fail
/// a user request; the table itself rejects UPDATE/DELETE at the database level.
/// </summary>
public static class Audit
{
    private static NpgsqlDataSource? _ds;
    public static void Configure(NpgsqlDataSource ds) => _ds = ds;

    /// <summary>Read access is governed by the configurable Role Management matrix.</summary>
    public static Task<IResult?> RequireViewer(UserScope s, string action = "view") =>
        Permissions.Require(s, "audit", action);

    public static void Log(string action, UserScope? actor, string? targetType = null, string? targetId = null,
                           string? summary = null, object? detail = null, string? ip = null,
                           string? actorEmailOverride = null)
    {
        var ds = _ds;
        if (ds is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var con = await ds.OpenConnectionAsync();
                var cmd = new NpgsqlCommand("""
                    insert into audit.audit_events
                        (actor_id, actor_email, actor_role, action, target_type, target_id, summary, detail, ip, actor_entity_id)
                    values (@actorId, @actorEmail, @actorRole, @action, @targetType, @targetId, @summary, @detail, @ip, @actorEntityId)
                    """, con);
                cmd.Parameters.AddWithValue("actorId", actor is { UserId: > 0 } ? actor.UserId : DBNull.Value);
                cmd.Parameters.AddWithValue("actorEmail", (object?)(actorEmailOverride ?? actor?.Email) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("actorRole", (object?)actor?.Role ?? DBNull.Value);
                cmd.Parameters.AddWithValue("action", action);
                cmd.Parameters.AddWithValue("targetType", (object?)targetType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("targetId", (object?)targetId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("summary", (object?)summary ?? DBNull.Value);
                cmd.Parameters.Add(new NpgsqlParameter("detail", NpgsqlDbType.Jsonb)
                { Value = detail is null ? DBNull.Value : JsonSerializer.Serialize(detail) });
                cmd.Parameters.AddWithValue("ip", (object?)ip ?? DBNull.Value);
                cmd.Parameters.AddWithValue("actorEntityId", (object?)actor?.EntityId ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[audit] write failed for '{action}': {ex.Message}");
            }
        });
    }

    private record Filters(string Where, DynamicParameters Params);

    private static Filters BuildFilters(UserScope scope, string? from, string? to, string? actor,
                                        string? action, string? search)
    {
        var w = new StringBuilder("1=1");
        var p = new DynamicParameters();

        // Only Super Admin sees everything. An entity-scoped Admin/Auditor sees their own entity's
        // activity plus actions by group-level (all-entity) users — those touch their data too —
        // but never another entity's local activity.
        if (scope.Role != "Super Admin")
        {
            w.Append(" and (a.actor_entity_id = @scopeEntity or a.actor_entity_id is null or a.actor_id = @selfId)");
            p.Add("scopeEntity", scope.EntityId);
            p.Add("selfId", scope.UserId);
        }
        if (DateOnly.TryParse(from, out var dFrom)) { w.Append(" and a.occurred_at >= @dFrom"); p.Add("dFrom", dFrom.ToDateTime(TimeOnly.MinValue)); }
        if (DateOnly.TryParse(to, out var dTo)) { w.Append(" and a.occurred_at < @dTo"); p.Add("dTo", dTo.AddDays(1).ToDateTime(TimeOnly.MinValue)); }
        if (!string.IsNullOrWhiteSpace(actor)) { w.Append(" and a.actor_email ilike @actor"); p.Add("actor", "%" + actor.Trim() + "%"); }
        if (!string.IsNullOrWhiteSpace(action)) { w.Append(" and a.action = @action"); p.Add("action", action.Trim()); }
        if (!string.IsNullOrWhiteSpace(search))
        {
            w.Append(" and (a.summary ilike @q or a.target_id ilike @q or a.detail::text ilike @q)");
            p.Add("q", "%" + search.Trim() + "%");
        }
        return new Filters(w.ToString(), p);
    }

    public static async Task<IResult> Query(NpgsqlDataSource ds, UserScope scope, string? from, string? to,
                                            string? actor, string? action, string? search, int? limit, int? offset)
    {
        if (await RequireViewer(scope) is { } err) return err;
        var f = BuildFilters(scope, from, to, actor, action, search);
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var off = Math.Max(offset ?? 0, 0);

        await using var con = await ds.OpenConnectionAsync();
        var rows = (await con.QueryAsync($"""
            select a.id, a.occurred_at as "occurredAt", a.actor_email as "actorEmail", a.actor_role as "actorRole",
                   a.action, a.target_type as "targetType", a.target_id as "targetId",
                   a.summary, a.detail::text as "detailJson", a.ip
            from audit.audit_events a
            where {f.Where}
            order by a.occurred_at desc, a.id desc
            limit {size} offset {off}
            """, f.Params)).ToList();

        var total = await con.ExecuteScalarAsync<long>(
            $"select count(*) from audit.audit_events a where {f.Where}", f.Params);

        // action list for the filter dropdown, within the caller's visibility
        var actionScope = BuildFilters(scope, null, null, null, null, null);
        var actions = (await con.QueryAsync<string>(
            $"select distinct a.action from audit.audit_events a where {actionScope.Where} order by 1", actionScope.Params)).ToList();

        return Results.Ok(new { rows, total, page = new { size, offset = off }, actions });
    }

    public static async Task<IResult> Export(NpgsqlDataSource ds, UserScope scope, string? from, string? to,
                                             string? actor, string? action, string? search)
    {
        if (await RequireViewer(scope, "edit") is { } err) return err;
        var f = BuildFilters(scope, from, to, actor, action, search);

        await using var con = await ds.OpenConnectionAsync();
        var rows = (await con.QueryAsync($"""
            select a.occurred_at, a.actor_email, a.actor_role, a.action,
                   a.target_type, a.target_id, a.summary, a.ip
            from audit.audit_events a
            where {f.Where}
            order by a.occurred_at desc, a.id desc
            limit 100000
            """, f.Params)).Cast<IDictionary<string, object?>>().ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Occurred at (UTC),Actor,Role,Action,Target type,Target,Summary,IP");
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", new[]
            {
                r["occurred_at"] is DateTime d ? d.ToString("yyyy-MM-dd HH:mm:ss") : "",
                Csv(r["actor_email"]), Csv(r["actor_role"]), Csv(r["action"]),
                Csv(r["target_type"]), Csv(r["target_id"]), Csv(r["summary"]), Csv(r["ip"])
            }));

        Log("audit.export", scope, "audit", null, $"Exported {Fmt.N(rows.Count)} audit events");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return Results.File(bytes, "text/csv", $"audit_log_{DateTime.UtcNow:yyyyMMdd_HHmm}.csv");
    }

    private static string Csv(object? v)
    {
        var s = v?.ToString() ?? "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
