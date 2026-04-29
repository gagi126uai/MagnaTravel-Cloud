#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

if [ -f .env ]; then
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
fi

POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-travel_db}"
POSTGRES_DB="${POSTGRES_DB:-travel}"
POSTGRES_USER="${POSTGRES_USER:-traveluser}"
BACKUP_ROOT="${BACKUP_ROOT:-backups/postgres/manual}"

if [ -z "${POSTGRES_PASSWORD:-}" ]; then
  echo "POSTGRES_PASSWORD is required." >&2
  exit 1
fi

mkdir -p "$BACKUP_ROOT"

timestamp="$(date +%Y%m%d-%H%M%S)"
backup_file="$BACKUP_ROOT/${POSTGRES_DB}-${timestamp}.dump"

echo "Creating PostgreSQL backup: $backup_file"
docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$POSTGRES_CONTAINER" \
  pg_dump -Fc -h 127.0.0.1 -U "$POSTGRES_USER" "$POSTGRES_DB" > "$backup_file"

echo "Backup completed: $backup_file"
