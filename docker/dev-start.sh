#!/usr/bin/env sh
set -eu

dotnet run --project src/CloudEmuera.Migrator --no-restore -- migrate --data-root /data
exec dotnet /workspace/src/CloudEmuera.Api/bin/Debug/net10.0/CloudEmuera.Api.dll
