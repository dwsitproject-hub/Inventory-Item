#!/usr/bin/env sh
# Run the backend unit tests in a throwaway .NET 8 SDK container — no local SDK needed.
# Sources are copied inside the container, so no bin/obj lands in backend/ (which would leak
# into the API's Docker build context).
#   sh tests/run-tests.sh
set -e
REPO="$(cd "$(dirname "$0")/.." && pwd)"
MSYS_NO_PATHCONV=1 docker run --rm -v "$REPO:/repo:ro" mcr.microsoft.com/dotnet/sdk:8.0-alpine sh -c '
  mkdir /work && cp -r /repo/backend /repo/tests /work/ && rm -rf /work/backend/bin /work/backend/obj
  cd /work/tests/BcInventory.Api.Tests && dotnet test --nologo'
