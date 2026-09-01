# Migrating the BC Inventory database to ApsaraDB RDS

This applies the *PostgreSQL to ApsaraDB RDS Dump/Restore Runbook* to this system. It is not a
restatement of that document — read it for the reasoning. What follows is the same sequence with
this application's real values, its real gates, and the three places where our shape differs
enough that following the generic steps literally would be wrong.

**Scope.** Staging only: the self-managed PostgreSQL 16 container on 172.28.92.60 becomes an
ApsaraDB RDS instance. The API on 172.28.92.57 is repointed. Nothing on the frontend changes.

---

## What this database actually looks like

Measured, not assumed:

| | |
|---|---|
| Engine | PostgreSQL 16.11, `postgres:16-alpine`, container `bc-inventory-postgres` |
| Reached at | `172.28.92.60:5443`, database `bcinventory`, user `bcapp` |
| Schemas | `app`, `audit`, `auth`, `bc`, `ingest`, `master` — **`public` is empty** |
| Base tables | 13 · **13 identity sequences** · 0 materialized views |
| Extensions | `plpgsql` only |
| Triggers | 1 — `audit.trg_audit_no_mutation` |
| Naive timestamp columns | **0** — every timestamp is `timestamptz` |

### Three places we diverge from the generic runbook

**1. `public` is empty, so §7.2 does not apply.** The runbook's "wipe and recreate `public`"
assumes the application lives there. Ours does not — dropping `public` would achieve nothing and,
on a non-superuser RDS account, would take extensions with it. `migrate-to-apsaradb.sh` drops the
six application schemas by name instead. The same reasoning applies to the completeness gate: the
count of base tables must span all schemas, which is 13, not the 0 that counting `public` gives.

**2. The timezone trap cannot bite us, and we check it anyway.** Our source runs `TimeZone=UTC`;
ApsaraDB defaults to `Asia/Jakarta`. On most systems that difference silently reinterprets every
stored naive timestamp, and is found weeks later. We have zero `timestamp without time zone`
columns — all `timestamptz`, which stores an absolute instant — so a mismatch cannot change the
meaning of anything already written. `preflight` still prints both, because a mismatch would still
change what `now()` and date arithmetic produce going forward. Align them if you can.

**3. Our validation layer is a trigger, and a row-count check will not see it.** `audit.audit_events`
is append-only, enforced by `trg_audit_no_mutation` raising on UPDATE or DELETE. If that trigger
does not come across, the audit trail becomes quietly editable: no error, no failed query, nothing
in a row count. `verify` compares `pg_get_triggerdef` and the function body between the two
databases. It deliberately does **not** attempt a probe UPDATE — the trigger is `FOR EACH ROW`, so
a probe matching no rows never fires it and passes whether the trigger exists or not. That check
was written the wrong way first and reported a healthy database as broken during the rehearsal.

---

## Rehearsed before staging

The whole sequence was run end to end locally: PostgreSQL 16 source → a PostgreSQL 18 target,
the same major-version jump the real move makes, carrying the real local dataset (1,128 documents,
1,496 lines, 566 audit events, 5 users, 42 role-permission rows).

All ten checks came back identical: rows, sequences, 29 index definitions, 102 columns,
41 constraints, 1 trigger, 1 function, matviews, plus both audit-trigger definitions. A negative
test — dropping the trigger on the target — was correctly reported as missing. The
`.env` rewrite was round-tripped with a password containing `&`, `|`, `#` and `\` and came back
byte-identical in both directions.

---

## Before you start

1. Create the RDS instance and the `bcinventory` database in the Alibaba Cloud console.
   Size for **CPU**, not storage — the KLIP finding was that the database was faster per query
   and the instance simply had fewer cores than the concurrent workload needed.
2. Allow 172.28.92.60 (for the restore) and 172.28.92.57 (for the application) in the instance
   whitelist.
3. Note the engine's **major version**. The client container must match it — not the source's.

```bash
cd /opt/bc-inventory/deploy/db && cp apsaradb.env.example apsaradb.env && chmod 600 apsaradb.env && nano apsaradb.env
```

Fill in every value from the console. The script refuses to run on anything still looking like a
placeholder; a pasted placeholder is the first trap in the runbook and it fails silently.

## The sequence

Run the phases in order, on 172.28.92.60, and read the output of each before starting the next.

```bash
cd /opt/bc-inventory/deploy/db && ./migrate-to-apsaradb.sh preflight
```

Compare the two `TimeZone` lines. Target `base_tables` must be `0`.

**Stop the API first** — on 172.28.92.57:

```bash
docker compose -f /opt/bc-inventory/deploy/backend/docker-compose.yml stop api
```

```bash
cd /opt/bc-inventory/deploy/db && ./migrate-to-apsaradb.sh dump
```

Gates on zero application connections, on both baseline files being non-empty, and on the dump
containing exactly one `TABLE DATA` entry per source table.

```bash
cd /opt/bc-inventory/deploy/db && ./migrate-to-apsaradb.sh restore
```

Drops the six schemas, restores with `--no-owner --no-privileges -j 4`, then runs `ANALYZE`.
The `ANALYZE` is not optional: a restored database carries no statistics, and query plans chosen
without them ran roughly 6× slower on the KLIP migration — an hour spent investigating a
performance regression that does not exist.

```bash
cd /opt/bc-inventory/deploy/db && ./migrate-to-apsaradb.sh verify
```

Every line must read `IDENTICAL`. Two differences are expected rather than defects if the target
is a newer major version, and both are already handled in the queries: PostgreSQL 18 materializes
every `NOT NULL` as a `pg_constraint` row (excluded via `contype <> 'n'`), and extension minor
upgrades add functions (extension-owned functions are excluded via `pg_depend`).

## Cutover

On 172.28.92.57, with the values from `apsaradb.env`:

```bash
cd /opt/bc-inventory/deploy/backend && RDS_HOST=... RDS_PORT=5432 RDS_DB=bcinventory RDS_USER=... RDS_PW=... ./cutover-to-apsaradb.sh
```

This backend reads its database settings from exactly one file — the compose project `.env` — so
the runbook's "edit both files" problem does not arise here. The script:

- writes `rollback-to-selfmanaged.sh` with the **current** values baked in as literals, before
  changing anything, so the reverse move never depends on a backup that may be overwritten later;
- rewrites `.env` with `awk` reading values from the environment rather than `sed` or `awk -v` —
  both mangle passwords containing `& | \`, and the resulting failure is an opaque authentication
  error, not a parse error;
- recreates with `up -d --force-recreate`. Never `start` or `restart`: those reuse the environment
  baked into the container at creation, so the API keeps using the old database while `.env` claims
  otherwise;
- prints the connection string the container actually received, password masked.

## After

- Read the API startup logs; confirm it connected and the schema check ran clean.
- Re-check row counts on the target. This app seeds accounts only into an empty user table, so the
  user count must be unchanged — verify it rather than assume it.
- Click through Reports, Inventory Movement and the audit log, and compare the figures against what
  `verify` recorded.
- Do not judge performance from the first minutes. A cold instance produces alarming numbers that
  mean nothing. If it is still slow in steady state, isolate one query and run it against both
  databases with `EXPLAIN (ANALYZE, BUFFERS)` before touching any setting.

**Keep the self-managed container running and untouched until the application has completed a full
business day on RDS**, including a BC upload and an alert e-mail — those paths are not exercised by
clicking through the UI. Rollback is `./rollback-to-selfmanaged.sh` on 172.28.92.57. Rotate the old
server's password when you retire it.
