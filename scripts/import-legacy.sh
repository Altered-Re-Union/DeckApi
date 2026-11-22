#!/usr/bin/env bash
# Imports a backup of the old PHP decks-api database into the DeckApi database.
# Same steps as import-legacy.ps1: restore into a throwaway postgres:17 container, run
# `DeckApi import-legacy`, remove the container. Re-runnable.
#
# Usage:
#   scripts/import-legacy.sh <backup.dump|backup.sql|backup.sql.gz> "<target connection string>" [--dry-run]
# Env: UniquesSearch__BaseUrl (card catalog when the target has no snapshot yet), PORT (default 55432).
set -euo pipefail

backup="$(realpath "$1")"
target="$2"
dry_run="${3:-}"
port="${PORT:-55432}"
container="deckapi-legacy-import-$$"
password="import"
script_dir="$(cd "$(dirname "$0")" && pwd)"

cleanup() { docker rm -f "$container" >/dev/null 2>&1 || true; }
trap cleanup EXIT

docker run -d --name "$container" -e POSTGRES_PASSWORD="$password" -e POSTGRES_DB=legacy -p "$port:5432" postgres:17-alpine >/dev/null
echo "Waiting for PostgreSQL..."
for _ in $(seq 60); do
  docker exec "$container" pg_isready -h 127.0.0.1 -U postgres -d legacy >/dev/null 2>&1 && break
  sleep 1
done

docker cp "$backup" "$container:/tmp/backup"
echo "Restoring $backup..."
case "$backup" in
  *.sql.gz) docker exec "$container" sh -c "gunzip -c /tmp/backup | psql -q -U postgres -d legacy" >/dev/null ;;
  *.sql)    docker exec "$container" psql -q -U postgres -d legacy -f /tmp/backup >/dev/null ;;
  *)        docker exec "$container" pg_restore --no-owner --no-privileges -U postgres -d legacy /tmp/backup ;;
esac

export ConnectionStrings__decks="$target"
export UniquesSearch__BaseUrl="${UniquesSearch__BaseUrl:-http://127.0.0.1:9}"
args=(import-legacy --source "Host=localhost;Port=$port;Database=legacy;Username=postgres;Password=$password" --migrate)
[ "$dry_run" = "--dry-run" ] && args+=(--dry-run)
dotnet run --project "$script_dir/../src/DeckApi/DeckApi.csproj" -c Release -- "${args[@]}"
