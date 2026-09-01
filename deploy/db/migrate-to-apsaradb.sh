#!/bin/sh
# BC Inventory — migrate the self-managed PostgreSQL 16 database into ApsaraDB RDS.
# Runs on the DB server (172.28.92.60). Follows the PostgreSQL_to_ApsaraDB_RDS runbook,
# with the gates fixed to this application's actual shape:
#
#   6 schemas: app, audit, auth, bc, ingest, master  (public is empty — do NOT wipe only public)
#   13 base tables · 13 identity sequences · 0 materialized views
#   1 trigger: audit.trg_audit_no_mutation — the append-only guarantee. It must survive.
#   Extensions: plpgsql only. Every timestamp column is timestamptz, so a source/target
#   timezone difference cannot silently reinterpret stored data — we still check it.
#
# Usage:  ./migrate-to-apsaradb.sh preflight | dump | restore | verify
# Run the phases in that order and read the output of each before starting the next.
set -e

cd "$(dirname "$0")"
B="$(pwd)/backups"
mkdir -p "$B"

[ -f apsaradb.env ] || { echo "FATAL: apsaradb.env not found. cp apsaradb.env.example apsaradb.env"; exit 1; }
. ./apsaradb.env

# Source the self-managed password from the file that already holds it, rather than retyping it.
if [ -z "$SRC_PW" ] && [ -f .env ]; then
  SRC_PW=$(grep -E '^DB_PASSWORD=' .env | head -1 | cut -d= -f2-)
fi

for v in RDS_HOST RDS_PORT RDS_DB RDS_USER RDS_PW PGCLIENT_IMAGE SRC_HOST SRC_PORT SRC_DB SRC_USER SRC_PW; do
  eval "val=\$$v"
  case "$val" in
    ""|CHANGE_ME|changeme|"<"*|*placeholder*|*xxxxxxxx*)
      echo "FATAL: $v is unset or still a placeholder ('$val')."
      echo "Every value must be read from the console or from .env, never typed from the template."
      exit 1 ;;
  esac
done

# Host networking on the Linux staging hosts. Overridable so the same script can be
# rehearsed on Docker Desktop, which has no host network:
#   NET_ARGS="" SRC_HOST=host.docker.internal RDS_HOST=host.docker.internal ./migrate... 
NET_ARGS="${NET_ARGS---network host}"

# psql against SOURCE and TARGET. The client image matches the DESTINATION major version:
# a client older than the server can fail or subtly misbehave during restore.
S() { docker run --rm $NET_ARGS -e PGPASSWORD="$SRC_PW" -v "$B:/b" "$PGCLIENT_IMAGE" \
        psql -h "$SRC_HOST" -p "$SRC_PORT" -U "$SRC_USER" -d "$SRC_DB" -t -A -v ON_ERROR_STOP=1 "$@"; }
R() { docker run --rm $NET_ARGS -e PGPASSWORD="$RDS_PW" -v "$B:/b" "$PGCLIENT_IMAGE" \
        psql -h "$RDS_HOST" -p "$RDS_PORT" -U "$RDS_USER" -d "$RDS_DB" -t -A -v ON_ERROR_STOP=1 "$@"; }

# --- Appendix A verification queries ---------------------------------------------------
QROWS="SELECT table_schema||'.'||table_name||'='||(xpath('/row/c/text()', query_to_xml(format('SELECT COUNT(*) AS c FROM %I.%I', table_schema, table_name), false, true, '')))[1]::text FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1;"
QSEQS="SELECT schemaname||'.'||sequencename||'='||COALESCE(last_value::text,'unset') FROM pg_sequences WHERE schemaname NOT IN ('pg_catalog','information_schema') ORDER BY 1;"
QIDX="SELECT schemaname||'.'||indexname||' :: '||indexdef FROM pg_indexes WHERE schemaname NOT IN ('pg_catalog','information_schema') ORDER BY 1;"
QCOLS="SELECT table_schema||'.'||table_name||'.'||column_name||' '||data_type||' '||is_nullable FROM information_schema.columns WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1;"
# contype cast to text, and contype 'n' excluded: PostgreSQL 18 materializes every NOT NULL
# as a pg_constraint row, which 16 does not. Without both, this reads as ~200 lost constraints.
QCONS="SELECT n.nspname||'.'||c.conrelid::regclass::text||'.'||c.conname||' '||c.contype::text FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.contype::text <> 'n' ORDER BY 1;"
QTRIG="SELECT n.nspname||'.'||c.relname||'.'||t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE NOT t.tgisinternal ORDER BY 1;"
# Application functions only — extension-owned ones gain entries across minor versions.
QFUNC="SELECT n.nspname||'.'||p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid=p.oid AND d.deptype='e') ORDER BY 1;"
QMATVIEW="SELECT schemaname||'.'||matviewname FROM pg_matviews ORDER BY 1;"

APPSCHEMAS="app audit auth bc ingest master"

case "$1" in

preflight)
  echo "=== SOURCE ==="
  S -c "SELECT 'version=' || substring(version() from 'PostgreSQL [0-9.]+');
        SELECT 'TimeZone=' || current_setting('TimeZone');
        SELECT 'base_tables=' || count(*) FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema');
        SELECT 'extensions=' || string_agg(extname, ',' ORDER BY extname) FROM pg_extension;
        SELECT 'naive_timestamp_cols=' || count(*) FROM information_schema.columns WHERE data_type='timestamp without time zone' AND table_schema NOT IN ('pg_catalog','information_schema');"
  echo "=== TARGET ==="
  R -c "SELECT 'version=' || substring(version() from 'PostgreSQL [0-9.]+');
        SELECT 'TimeZone=' || current_setting('TimeZone');
        SELECT 'base_tables=' || count(*) FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema');
        SELECT 'extensions=' || string_agg(extname, ',' ORDER BY extname) FROM pg_extension;
        SELECT 'can_create=' || has_database_privilege(current_user, current_database(), 'CREATE')::text;"
  echo "=== disk headroom for the dump ==="
  df -h "$B" | tail -1
  echo
  echo "Compare the two TimeZone lines — they should match. Every timestamp column in this"
  echo "schema is timestamptz (naive_timestamp_cols=0), so a mismatch cannot reinterpret"
  echo "stored data, but align them anyway so now() and date arithmetic agree."
  echo "TARGET base_tables must be 0. If it is not, something has already created objects there."
  ;;

dump)
  echo "GATE: no application connections on the source"
  ACT=$(S -c "SELECT count(*) FROM pg_stat_activity WHERE datname='$SRC_DB' AND pid <> pg_backend_pid() AND application_name NOT LIKE 'psql%';")
  echo "  non-psql connections = $ACT"
  if [ "$ACT" -ne 0 ]; then
    echo ">>> STOP: the API is still writing. On 172.28.92.57 run:"
    echo "      docker compose -f /opt/bc-inventory/deploy/backend/docker-compose.yml stop api"
    exit 1
  fi

  echo "Capturing baselines"
  S -c "$QROWS" > "$B/SOURCE_ROWS.txt"
  S -c "$QSEQS" > "$B/SOURCE_SEQS.txt"
  for f in SOURCE_ROWS SOURCE_SEQS; do
    [ -s "$B/$f.txt" ] || { echo ">>> STOP: $f.txt is empty — the baseline query aborted."; exit 1; }
  done
  echo "  rows baseline: $(wc -l < "$B/SOURCE_ROWS.txt") tables"
  echo "  seqs baseline: $(wc -l < "$B/SOURCE_SEQS.txt") sequences"

  N=$(S -c "SELECT count(*) FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema');")
  echo "  source base tables, ALL schemas = $N"

  echo "Dumping"
  rm -f "$B/cutover.dump"
  docker run --rm $NET_ARGS -e PGPASSWORD="$SRC_PW" -v "$B:/b" "$PGCLIENT_IMAGE" \
    pg_dump -h "$SRC_HOST" -p "$SRC_PORT" -U "$SRC_USER" -d "$SRC_DB" \
    -Fc --no-owner --no-privileges -f /b/cutover.dump
  ls -lh "$B/cutover.dump"

  TD=$(docker run --rm -v "$B:/b" "$PGCLIENT_IMAGE" pg_restore -l /b/cutover.dump | grep -c 'TABLE DATA')
  echo "GATE: TABLE DATA entries = $TD (need $N)"
  [ "$TD" -eq "$N" ] || { echo ">>> STOP: the dump is incomplete."; exit 1; }
  echo "OK — dump complete. Leave the source container running and untouched."
  ;;

restore)
  N=$(wc -l < "$B/SOURCE_ROWS.txt")
  TD=$(docker run --rm -v "$B:/b" "$PGCLIENT_IMAGE" pg_restore -l /b/cutover.dump | grep -c 'TABLE DATA')
  echo "GATE 1: TABLE DATA = $TD (need $N)"
  [ "$TD" -eq "$N" ] || { echo ">>> STOP"; exit 1; }

  echo "GATE 2: target reachable, and what is already on it"
  R -c "SELECT 1;" > /dev/null
  EXIST=$(R -c "SELECT count(*) FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema');")
  echo "  existing base tables on target = $EXIST"
  if [ "$EXIST" -ne 0 ]; then
    echo ">>> The target is not empty. Continuing DROPs these schemas: $APPSCHEMAS"
    printf "     Type YES to wipe and restore: "
    read ans
    [ "$ans" = "YES" ] || { echo ">>> STOP"; exit 1; }
  fi

  # Drop only this application's schemas. The runbook's "wipe public" step does not apply
  # here: public is empty, and dropping it on a non-superuser account takes extensions with it.
  for s in $APPSCHEMAS; do
    R -c "DROP SCHEMA IF EXISTS $s CASCADE;"
  done
  echo "  dropped: $APPSCHEMAS"

  echo "Restoring"
  set +e
  docker run --rm $NET_ARGS -e PGPASSWORD="$RDS_PW" -v "$B:/b" "$PGCLIENT_IMAGE" \
    pg_restore -h "$RDS_HOST" -p "$RDS_PORT" -U "$RDS_USER" -d "$RDS_DB" \
    --no-owner --no-privileges -j 4 /b/cutover.dump > "$B/restore.log" 2>&1
  RC=$?
  set -e
  ERRS=$(grep -ci 'pg_restore: error' "$B/restore.log" || true)
  echo "  exit=$RC  errors=$ERRS   (full log: $B/restore.log)"
  [ "$ERRS" -eq 0 ] || echo ">>> Read every error in restore.log before continuing."

  # A restored database carries no statistics. Skipping this produces roughly a 6x slowdown
  # that looks exactly like a regression and is not one.
  echo "ANALYZE"
  R -c "ANALYZE;"
  echo "OK — restored and analyzed. Next: $0 verify"
  ;;

verify)
  for n in ROWS SEQS IDX COLS CONS TRIG FUNC MATVIEW; do
    eval "q=\$Q$n"
    S -c "$q" > "$B/S_$n.txt"
    R -c "$q" > "$B/R_$n.txt"
    if diff -q "$B/S_$n.txt" "$B/R_$n.txt" > /dev/null; then
      echo "$n IDENTICAL   ($(wc -l < "$B/S_$n.txt") lines)"
    else
      echo "$n DIFFERS     -> diff $B/S_$n.txt $B/R_$n.txt"
    fi
  done
  echo
  echo "=== application-specific checks ==="
  # The append-only audit trail is enforced by a trigger, not by the application. If it did
  # not come across, audit rows become editable and nothing anywhere reports an error.
  # Compare the definitions rather than attempting a mutation: the trigger is FOR EACH ROW,
  # so a probe UPDATE matching no rows never fires it and passes whether it exists or not —
  # and a probe that did match would be a write into the freshly restored database.
  QTDEF="SELECT pg_get_triggerdef(t.oid) FROM pg_trigger t WHERE t.tgname='trg_audit_no_mutation' AND NOT t.tgisinternal;"
  QFDEF="SELECT p.prosrc FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='audit' AND p.proname='no_mutation';"
  for n in TDEF FDEF; do
    eval "q=\$Q$n"
    S -c "$q" > "$B/S_$n.txt"
    R -c "$q" > "$B/R_$n.txt"
    if [ ! -s "$B/R_$n.txt" ]; then
      echo "audit $n MISSING ON TARGET >>> the append-only guarantee is gone."
    elif diff -q "$B/S_$n.txt" "$B/R_$n.txt" > /dev/null; then
      echo "audit $n IDENTICAL"
    else
      echo "audit $n DIFFERS -> diff $B/S_$n.txt $B/R_$n.txt"
    fi
  done
  # An identity sequence left behind its table looks perfect until the first insert.
  UNSET=$(R -c "SELECT count(*) FROM pg_sequences WHERE schemaname NOT IN ('pg_catalog','information_schema') AND last_value IS NULL;")
  echo "sequences with no last_value: $UNSET (each one collides on its next insert)"
  ;;

*)
  echo "usage: $0 preflight | dump | restore | verify"
  exit 1 ;;
esac
