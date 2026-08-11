using System.Text.Json;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace BcInventory.Api;

public record ViewPayload(string? Name, string[] Columns, SortSpec[]? Sorts, int? PageSize);

/// <summary>Grid personalisation: implicit "last layout" per user per report + named views (FR-R7/R12).</summary>
public static class SavedViews
{
    public static async Task<IResult> List(NpgsqlDataSource ds, UserScope scope, string key)
    {
        await using var con = await ds.OpenConnectionAsync();
        var rows = (await con.QueryAsync("""
            select id, name, columns::text as "columnsJson", sorts::text as "sortsJson",
                   page_size as "pageSize", updated_at as "updatedAt"
            from app.saved_views where user_id = @uid and report_key = @key
            order by name nulls first
            """, new { uid = scope.UserId, key })).ToList();

        object Shape(dynamic r) => new
        {
            id = (long)r.id,
            name = (string?)r.name,
            columns = JsonSerializer.Deserialize<string[]>((string)r.columnsJson),
            sorts = JsonSerializer.Deserialize<SortSpec[]>((string)r.sortsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }),
            pageSize = (int)r.pageSize
        };

        return Results.Ok(new
        {
            last = rows.Where(r => r.name == null).Select(Shape).FirstOrDefault(),
            named = rows.Where(r => r.name != null).Select(Shape)
        });
    }

    public static async Task<IResult> Save(NpgsqlDataSource ds, UserScope scope, string key, ViewPayload payload)
    {
        if (Catalog.Get(key) is null)
            return Results.Problem(statusCode: 400, title: "RPT-001", detail: $"Unknown report key '{key}'.");
        if (payload.Columns is not { Length: > 0 })
            return Results.Problem(statusCode: 400, title: "VAL-001", detail: "columns required.");

        var name = string.IsNullOrWhiteSpace(payload.Name) ? null : payload.Name.Trim();
        await using var con = await ds.OpenConnectionAsync();
        var cmd = new NpgsqlCommand(name is null
            ? """
              insert into app.saved_views (user_id, report_key, name, columns, sorts, page_size)
              values (@uid, @key, null, @cols, @sorts, @size)
              on conflict (user_id, report_key) where name is null
              do update set columns = excluded.columns, sorts = excluded.sorts,
                            page_size = excluded.page_size, updated_at = now()
              returning id
              """
            : """
              insert into app.saved_views (user_id, report_key, name, columns, sorts, page_size)
              values (@uid, @key, @name, @cols, @sorts, @size)
              on conflict (user_id, report_key, name) where name is not null
              do update set columns = excluded.columns, sorts = excluded.sorts,
                            page_size = excluded.page_size, updated_at = now()
              returning id
              """, con);
        cmd.Parameters.AddWithValue("uid", scope.UserId);
        cmd.Parameters.AddWithValue("key", key);
        if (name is not null) cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.Add(new NpgsqlParameter("cols", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(payload.Columns) });
        cmd.Parameters.Add(new NpgsqlParameter("sorts", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(payload.Sorts ?? Array.Empty<SortSpec>()) });
        cmd.Parameters.AddWithValue("size", payload.PageSize ?? 25);
        var id = (long)(await cmd.ExecuteScalarAsync())!;
        return Results.Ok(new { id, name });
    }

    public static async Task<IResult> Delete(NpgsqlDataSource ds, UserScope scope, long id)
    {
        await using var con = await ds.OpenConnectionAsync();
        var n = await con.ExecuteAsync(
            "delete from app.saved_views where id = @id and user_id = @uid and name is not null",
            new { id, uid = scope.UserId });
        return n > 0 ? Results.Ok(new { deleted = true })
                     : Results.Problem(statusCode: 404, title: "VAL-001", detail: "View not found (or not yours).");
    }
}
