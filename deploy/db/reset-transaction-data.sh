#!/bin/sh
# BC Inventory — clear uploaded/transaction data so testing can start from a clean sheet,
# keeping every account, permission and master-data record.
#
#   CLEARED   bc.documents · bc.document_lines          the ingested customs / movement rows
#             ingest.ingestion_files · ingest.quarantine_rows
#             app.notifications · app.notification_deliveries
#
#   KEPT      auth.users · auth.role_permissions        who can log in, and what they may do
#             app.notification_subscriptions            who is alerted about what
#             app.saved_views                           saved column layouts
#             master.entities · master.sites · master.tpb_permits
#             audit.audit_events                        unless --with-audit is passed
#
# The report templates are defined in code, not in the database, so nothing here affects them.
#
# Usage:  ./reset-transaction-data.sh [--with-audit] [--yes]
set -e

cd "$(dirname "$0")"
[ -f apsaradb.env ] || { echo "FATAL: apsaradb.env not found — it holds the live database connection."; exit 1; }
. ./apsaradb.env

WITH_AUDIT=no
ASSUME_YES=no
for a in "$@"; do
  case "$a" in
    --with-audit) WITH_AUDIT=yes ;;
    --yes) ASSUME_YES=yes ;;
    *) echo "usage: $0 [--with-audit] [--yes]"; exit 2 ;;
  esac
done

for v in RDS_HOST RDS_PORT RDS_DB RDS_USER RDS_PW PGCLIENT_IMAGE; do
  eval "val=\$$v"
  case "$val" in
    ""|CHANGE_ME|"<"*|*xxxxxxxx*) echo "FATAL: $v is unset or still a placeholder ('$val')."; exit 1 ;;
  esac
done

NET_ARGS="${NET_ARGS---network host}"
R() { docker run --rm $NET_ARGS -e PGPASSWORD="$RDS_PW" "$PGCLIENT_IMAGE" \
        psql -h "$RDS_HOST" -p "$RDS_PORT" -U "$RDS_USER" -d "$RDS_DB" -v ON_ERROR_STOP=1 "$@"; }

CLEARED="bc.document_lines bc.documents ingest.quarantine_rows ingest.ingestion_files app.notification_deliveries app.notifications"
KEPT="auth.users auth.role_permissions app.notification_subscriptions app.saved_views master.entities master.sites master.tpb_permits"

counts() {  # counts "<space separated tables>"
  set -- $1
  q=""
  for t in "$@"; do
    [ -n "$q" ] && q="$q union all "
    q="$q select '$t' as tbl, count(*) from $t"
  done
  R -t -A -F' = ' -c "$q order by 1;"
}

echo "TARGET  $RDS_USER@$RDS_HOST:$RDS_PORT/$RDS_DB"
echo
echo "=== will be CLEARED ==="
counts "$CLEARED"
echo "=== will be KEPT ==="
counts "$KEPT"
echo "=== audit.audit_events ==="
R -t -A -c "select count(*) from audit.audit_events;"
if [ "$WITH_AUDIT" = yes ]; then
  echo "  --with-audit: the audit trail WILL be deleted."
  echo "  This table is append-only by trigger; the script disables that trigger, deletes,"
  echo "  re-enables it, and then writes one audit row recording that the reset happened."
else
  echo "  kept (pass --with-audit to clear it too)"
fi
echo

if [ "$ASSUME_YES" != yes ]; then
  printf 'Type CLEAR to delete the transaction data: '
  read ans
  [ "$ans" = "CLEAR" ] || { echo "aborted — nothing was changed."; exit 1; }
fi

# Snapshot the keep-tables so the run can prove it did not touch them.
BEFORE_KEPT=$(counts "$KEPT")

# One statement, one transaction: every one of these tables is referenced only by others in
# the same list, so TRUNCATE needs no CASCADE and cannot reach a table not named here.
# RESTART IDENTITY so the first test upload gets id 1 rather than continuing the old numbering.
echo "clearing…"
R -q -c "truncate $(echo $CLEARED | tr ' ' ',') restart identity;"

if [ "$WITH_AUDIT" = yes ]; then
  R -q -c "alter table audit.audit_events disable trigger trg_audit_no_mutation;"
  # Re-enable in the same run whatever happens next, so a failed delete cannot leave the
  # append-only guarantee switched off.
  if R -q -c "delete from audit.audit_events;"; then
    R -q -c "alter table audit.audit_events enable trigger trg_audit_no_mutation;"
  else
    R -q -c "alter table audit.audit_events enable trigger trg_audit_no_mutation;"
    echo ">>> the audit delete failed; the trigger has been re-enabled."
    exit 1
  fi
  ENABLED=$(R -t -A -c "select tgenabled <> 'D' from pg_trigger where tgname='trg_audit_no_mutation' and not tgisinternal;")
  echo "append-only trigger re-enabled: $ENABLED (must be t)"
  [ "$ENABLED" = "t" ] || { echo ">>> STOP: the audit table is left unprotected."; exit 1; }
fi

# The reset itself is an event worth keeping, especially when the trail was just emptied.
R -q -c "insert into audit.audit_events (actor_email, actor_role, action, target_type, summary, detail)
         values ('$RDS_USER', 'system', 'admin.data.reset', 'database',
                 'Transaction data cleared for a fresh test cycle',
                 jsonb_build_object('cleared', '$CLEARED', 'auditCleared', '$WITH_AUDIT'));"

echo
echo "=== CLEARED (must all be 0) ==="
counts "$CLEARED"
echo "=== KEPT — compared against the values read before the reset ==="
AFTER_KEPT=$(counts "$KEPT")
echo "$AFTER_KEPT"
if [ "$BEFORE_KEPT" = "$AFTER_KEPT" ]; then
  echo "UNCHANGED — accounts, permissions and master data are intact."
else
  echo ">>> WARNING: a keep-table changed. Before:"
  echo "$BEFORE_KEPT"
fi
echo
echo "Done. Restart the API so it reloads with a clean slate:"
echo "  on 172.28.92.57 —  docker compose -f /opt/bc-inventory/deploy/backend/docker-compose.yml restart api"
