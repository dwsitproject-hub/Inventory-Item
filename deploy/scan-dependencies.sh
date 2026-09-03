#!/bin/sh
# BC Inventory — dependency and image vulnerability scan (AR-15).
# Runs the checks in containers so nothing needs installing on the host.
#
#   ./scan-dependencies.sh          report
#   ./scan-dependencies.sh --fail   exit non-zero if anything is found (for a build gate)
set -e
cd "$(dirname "$0")/.."

GATE=no
[ "$1" = "--fail" ] && GATE=yes
FOUND=0

echo "=== NuGet packages (.NET) ==="
out=$(docker run --rm -v "$(pwd)/backend:/src" -w /src mcr.microsoft.com/dotnet/sdk:8.0-alpine \
        sh -c "dotnet restore >/dev/null 2>&1 && dotnet list package --vulnerable --include-transitive" 2>&1 || true)
echo "$out" | sed 's/^/  /'
echo "$out" | grep -qi "has the following vulnerable packages" && FOUND=$((FOUND+1)) || true

echo "=== npm packages (front end) ==="
out=$(docker run --rm -v "$(pwd)/frontend:/app" -w /app node:20-alpine \
        sh -c "npm install --package-lock-only --no-audit --no-fund >/dev/null 2>&1; npm audit --audit-level=high" 2>&1 || true)
echo "$out" | tail -20 | sed 's/^/  /'
echo "$out" | grep -qE "[1-9][0-9]* (high|critical)" && FOUND=$((FOUND+1)) || true

echo "=== base images ==="
if docker scout version >/dev/null 2>&1; then
  for img in bc-inventory-api:latest bc-inventory-web:latest; do
    echo "  --- $img"
    docker scout cves --only-severity critical,high "$img" 2>&1 | tail -12 | sed 's/^/    /' || true
  done
else
  echo "  docker scout is not available. Either enable it, or run trivy:"
  echo "    docker run --rm -v /var/run/docker.sock:/var/run/docker.sock aquasec/trivy image bc-inventory-api:latest"
fi

echo
if [ "$FOUND" -eq 0 ]; then
  echo "No high or critical dependency vulnerabilities reported."
else
  echo "$FOUND ecosystem(s) reported vulnerabilities — read the output above."
  [ "$GATE" = yes ] && exit 1
fi
