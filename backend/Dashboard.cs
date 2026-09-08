using Dapper;
using Npgsql;

namespace BcInventory.Api;

public static class Dashboard
{
    public static async Task<IResult> Summary(NpgsqlDataSource ds, UserScope scope)
    {
        await using var con = await ds.OpenConnectionAsync();

        // Scope by the current entity + company. CompanyIds are longs, so inlining the array
        // literal is injection-safe; empty -> any('{}') matches nothing, the safe default.
        var arr = "'{" + string.Join(",", scope.CompanyIds) + "}'";
        var ent = scope.AllEntities ? "" : "entity_id = " + scope.EntityId;
        var comp = scope.AllCompanies ? "" : "company_id = any(" + arr + ")";
        var fComp = scope.AllCompanies ? "" : "f.company_id = any(" + arr + ")";
        static string Clause(string glue, params string[] parts)
        {
            var xs = parts.Where(s => s.Length > 0).ToArray();
            return xs.Length == 0 ? "" : glue + string.Join(" and ", xs);
        }
        var rowWhere = Clause(" where ", ent, comp);

        var kpis = await con.QueryFirstAsync($"""
            select
              (select count(*) from bc.documents{rowWhere}) as documents,
              (select count(*) from bc.document_lines{rowWhere}) as lines,
              (select count(*) from ingest.ingestion_files
                 where status in ('loaded','partial'){Clause(" and ", comp)}) as files_loaded,
              (select count(*) from ingest.quarantine_rows q
                 join ingest.ingestion_files f on f.id = q.ingestion_file_id{Clause(" where ", fComp)}) as quarantined
            """);

        var files = (await con.QueryAsync($"""
            select id, file_name as "fileName", template, source, status,
                   rows_total as "rowsTotal", rows_loaded as "rowsLoaded", rows_quarantined as "rowsQuarantined",
                   received_at as "receivedAt"
            from ingest.ingestion_files{Clause(" where ", comp)} order by received_at desc limit 6
            """)).ToList();

        var trend = (await con.QueryAsync($"""
            select to_char(date_trunc('month', doc_date), 'YYYY-MM') as month, template, count(*) as lines
            from bc.document_lines
            where doc_date is not null{Clause(" and ", ent, comp)}
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
