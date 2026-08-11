using Dapper;
using Npgsql;

namespace BcInventory.Api;

public static class Dashboard
{
    public static async Task<IResult> Summary(NpgsqlDataSource ds, UserScope scope)
    {
        await using var con = await ds.OpenConnectionAsync();
        var scopeWhere = scope.AllEntities ? "" : " where entity_id = " + scope.EntityId;

        var kpis = await con.QueryFirstAsync($"""
            select
              (select count(*) from bc.documents{scopeWhere}) as documents,
              (select count(*) from bc.document_lines{scopeWhere}) as lines,
              (select count(*) from ingest.ingestion_files where status in ('loaded','partial')) as files_loaded,
              (select count(*) from ingest.quarantine_rows) as quarantined
            """);

        var files = (await con.QueryAsync("""
            select id, file_name as "fileName", template, source, status,
                   rows_total as "rowsTotal", rows_loaded as "rowsLoaded", rows_quarantined as "rowsQuarantined",
                   received_at as "receivedAt"
            from ingest.ingestion_files order by received_at desc limit 6
            """)).ToList();

        var trend = (await con.QueryAsync($"""
            select to_char(date_trunc('month', doc_date), 'YYYY-MM') as month, template, count(*) as lines
            from bc.document_lines
            where doc_date is not null{(scope.AllEntities ? "" : " and entity_id = " + scope.EntityId)}
            group by 1, 2 order by 1
            """)).ToList();

        return Results.Ok(new
        {
            kpis = new
            {
                documents = (long)kpis.documents,
                lines = (long)kpis.lines,
                filesLoaded = (long)kpis.files_loaded,
                quarantined = (long)kpis.quarantined
            },
            latestIngestions = files,
            trend
        });
    }
}
