#!/usr/bin/env bash
# BC Inventory — pull the latest code and redeploy one tier.
#   usage: ./update.sh {db|backend|frontend}
# Run on the server that hosts that tier. Safe to re-run.
set -euo pipefail

TIER="${1:-}"
case "$TIER" in
  db|backend|frontend) ;;
  *) echo "usage: $0 {db|backend|frontend}" >&2; exit 2 ;;
esac

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIR="$REPO/deploy/$TIER"
[ -f "$DIR/docker-compose.yml" ] || { echo "no compose file at $DIR" >&2; exit 1; }

# .env holds the DB password / JWT key and is gitignored — refuse to start without it
if [ "$TIER" != "frontend" ] && [ ! -f "$DIR/.env" ]; then
  echo "missing $DIR/.env — copy .env.example and fill it in first" >&2
  exit 1
fi

echo "==> repository: $REPO"
BEFORE="$(git -C "$REPO" rev-parse --short HEAD)"

if git -C "$REPO" remote get-url origin >/dev/null 2>&1; then
  if ! git -C "$REPO" diff --quiet || ! git -C "$REPO" diff --cached --quiet; then
    echo "!! tracked files have local edits — commit, stash or revert them first:" >&2
    git -C "$REPO" status --short | head -20 >&2
    exit 1
  fi
  echo "==> pulling $(git -C "$REPO" rev-parse --abbrev-ref HEAD)"
  git -C "$REPO" pull --ff-only
else
  echo "==> no git remote (file-transfer install) — skipping pull"
fi

AFTER="$(git -C "$REPO" rev-parse --short HEAD)"
if [ "$BEFORE" = "$AFTER" ]; then
  echo "==> already at $AFTER — rebuilding anyway"
else
  echo "==> $BEFORE -> $AFTER"
  git -C "$REPO" log --oneline "$BEFORE..$AFTER" | sed 's/^/    /'
fi

echo "==> rebuilding '$TIER'"
cd "$DIR"
docker compose up -d --build

echo "==> waiting for health"
sleep 12
docker compose ps
echo "==> recent log"
docker compose logs --tail 25
echo "==> done — now running $AFTER"
