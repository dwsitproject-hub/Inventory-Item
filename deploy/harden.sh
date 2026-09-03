#!/bin/sh
# BC Inventory — host hardening checks for the items that cannot be fixed in code.
# Read-only by default; pass --apply to tighten file permissions it can fix itself.
#
#   ./harden.sh            report only
#   ./harden.sh --apply    also chmod the secret files to 600
set -e
cd "$(dirname "$0")"

APPLY=no
[ "$1" = "--apply" ] && APPLY=yes
FAIL=0
note() { echo "  $1"; }
bad()  { echo "  [!] $1"; FAIL=$((FAIL+1)); }
ok()   { echo "  [ok] $1"; }

echo "=== AR-13 · secret files ==="
for f in backend/.env db/.env frontend/.env db/apsaradb.env backend/rollback-to-selfmanaged.sh; do
  [ -f "$f" ] || continue
  mode=$(stat -c '%a' "$f" 2>/dev/null || echo '?')
  if [ "$mode" = "600" ] || [ "$mode" = "400" ]; then
    ok "$f is $mode"
  elif [ "$APPLY" = yes ]; then
    chmod 600 "$f"; ok "$f tightened from $mode to 600"
  else
    bad "$f is $mode — readable beyond its owner. Re-run with --apply, or: chmod 600 $f"
  fi
done

echo "=== AR-13 · secrets that should never be in git ==="
if git -C .. check-ignore -q deploy/backend/.env 2>/dev/null; then ok ".env files are gitignored"
else bad "deploy/backend/.env is NOT gitignored"; fi
# Look for a JWT_KEY that carries a real value. The placeholder in .env.example matches a
# naive search and would report a secret that is not there.
leak=$(git -C .. grep -I -h -E '^JWT_KEY=.+' $(git -C .. rev-list --all 2>/dev/null) -- '*.env' 2>/dev/null         | grep -vE '=(CHANGE_ME|changeme|)$' | head -1 || true)
if [ -n "$leak" ]; then
  bad "a real JWT_KEY value appears in git history — rotate it and treat the history as compromised"
else ok "no real JWT_KEY value found in git history"; fi

echo "=== AR-01/AR-10 · session settings ==="
if [ -f backend/.env ]; then
  life=$(grep -E '^JWT_LIFETIME_MINUTES=' backend/.env | cut -d= -f2)
  [ -n "$life" ] && note "token lifetime: ${life} minutes" || note "token lifetime: default (120 minutes)"
  key=$(grep -E '^JWT_KEY=' backend/.env | cut -d= -f2-)
  case "$key" in
    ""|CHANGE_ME*) bad "JWT_KEY is unset or still the placeholder" ;;
    *) [ "${#key}" -ge 32 ] && ok "JWT_KEY is ${#key} characters" || bad "JWT_KEY is only ${#key} characters — use 32 or more" ;;
  esac
fi

echo "=== AR-06 · database account ==="
if [ -f backend/.env ]; then
  dbuser=$(grep -E '^DB_USER=' backend/.env | cut -d= -f2)
  case "$dbuser" in
    postgres|rdsadmin|"") bad "the application connects as '${dbuser:-unset}' — a privileged account. Run deploy/db/create-app-role.sql and switch DB_USER to bcapp_rw" ;;
    *) ok "application database user is '$dbuser'" ;;
  esac
fi

echo "=== AR-02 · transport ==="
# Ignore commented-out blocks: the vhost ships a ready-to-enable TLS section that is inert.
if grep -rhs "listen 443" frontend/ 2>/dev/null | grep -qvE '^\s*#'; then ok "a TLS listener is configured"
else note "no TLS listener — staging is HTTP by decision (AR-02). This must change before production."; fi

echo "=== AR-07 · container user ==="
name=$(docker ps --format '{{.Names}}' 2>/dev/null | grep -m1 'bc-inventory-api' || true)
if [ -n "$name" ]; then
  u=$(docker exec "$name" id -un 2>/dev/null || echo '')
  if [ -z "$u" ]; then bad "could not read the user inside $name — check it manually"
  elif [ "$u" = "root" ]; then bad "$name runs as root"
  else ok "$name runs as '$u'"; fi
else
  note "the API container is not running here — check on the API server"
fi

echo
if [ "$FAIL" -eq 0 ]; then echo "No issues found."; else echo "$FAIL item(s) need attention."; fi
