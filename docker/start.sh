#!/usr/bin/env bash
set -euo pipefail

data_root="${CloudEmuera__DataPath:-/data}"
migrator="/app/migrator/CloudEmuera.Migrator.dll"
api="/app/api/CloudEmuera.Api.dll"
command="${1:-start}"
if (( $# > 0 )); then
  shift
fi

case "$command" in
  start)
    dotnet "$migrator" migrate --data-root "$data_root"
    exec dotnet "$api" "$@"
    ;;
  migrate)
    exec dotnet "$migrator" migrate --data-root "$data_root" "$@"
    ;;
  check)
    exec dotnet "$migrator" check --data-root "$data_root" "$@"
    ;;
  repair-indexes)
    exec dotnet "$migrator" repair-indexes --data-root "$data_root" "$@"
    ;;
  rebind-session-roots)
    dotnet "$migrator" migrate --data-root "$data_root"
    exec dotnet "$migrator" rebind-session-roots --data-root "$data_root" "$@"
    ;;
  *)
    exec "$command" "$@"
    ;;
esac
