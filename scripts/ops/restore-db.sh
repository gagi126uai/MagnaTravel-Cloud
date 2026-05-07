#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

usage() {
  cat <<'USAGE'
Usage:
  scripts/ops/restore-db.sh --backup backups/postgres/daily/travel-YYYYMMDD-HHMMSS.dump [--target shadow|primary]

Defaults:
  --target shadow

Primary restore is destructive and requires:
  CONFIRM_RESTORE_PRIMARY=YES scripts/ops/restore-db.sh --backup <file> --target primary
USAGE
}

BACKUP_FILE=""
RESTORE_TARGET="${RESTORE_TARGET:-shadow}"

while [ $# -gt 0 ]; do
  case "$1" in
    --backup)
      BACKUP_FILE="${2:-}"
      shift 2
      ;;
    --target)
      RESTORE_TARGET="${2:-}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [ -z "$BACKUP_FILE" ] || [ ! -f "$BACKUP_FILE" ]; then
  echo "A valid --backup file is required." >&2
  exit 1
fi

if [ "$RESTORE_TARGET" != "shadow" ] && [ "$RESTORE_TARGET" != "primary" ]; then
  echo "--target must be shadow or primary." >&2
  exit 1
fi

# shellcheck disable=SC1091
. scripts/ops/lib/env.sh
load_env_file .env

POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-travel_db}"
POSTGRES_DB="${POSTGRES_DB:-travel}"
POSTGRES_USER="${POSTGRES_USER:-traveluser}"
RESTORE_SHADOW_DB="${RESTORE_SHADOW_DB:-${POSTGRES_DB}_shadow}"

if [ -z "${POSTGRES_PASSWORD:-}" ]; then
  echo "POSTGRES_PASSWORD is required." >&2
  exit 1
fi

if ! [[ "$POSTGRES_DB" =~ ^[A-Za-z0-9_]+$ ]] || ! [[ "$RESTORE_SHADOW_DB" =~ ^[A-Za-z0-9_]+$ ]]; then
  echo "Database names may only contain letters, numbers and underscore." >&2
  exit 1
fi

if [ "$RESTORE_TARGET" = "primary" ]; then
  if [ "${CONFIRM_RESTORE_PRIMARY:-}" != "YES" ]; then
    echo "Refusing destructive restore. Set CONFIRM_RESTORE_PRIMARY=YES to restore the primary database." >&2
    exit 1
  fi

  target_db="$POSTGRES_DB"
  echo "Stopping app services before primary restore..."
  docker compose stop api worker >/dev/null
else
  target_db="$RESTORE_SHADOW_DB"
  echo "Preparing shadow database: $target_db"
  docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$POSTGRES_CONTAINER" \
    psql -h 127.0.0.1 -U "$POSTGRES_USER" -d postgres -v ON_ERROR_STOP=1 \
    -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$target_db';" >/dev/null
  docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$POSTGRES_CONTAINER" \
    psql -h 127.0.0.1 -U "$POSTGRES_USER" -d postgres -v ON_ERROR_STOP=1 \
    -c "DROP DATABASE IF EXISTS \"$target_db\";" >/dev/null
  docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$POSTGRES_CONTAINER" \
    createdb -h 127.0.0.1 -U "$POSTGRES_USER" "$target_db"
fi

if [ "$RESTORE_TARGET" = "primary" ]; then
  echo "Cleaning public schema in primary database..."
  docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$POSTGRES_CONTAINER" \
    psql -h 127.0.0.1 -U "$POSTGRES_USER" -d "$target_db" -v ON_ERROR_STOP=1 \
    -c 'DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;' >/dev/null
fi

echo "Restoring $BACKUP_FILE into $target_db..."
docker exec -i -e PGPASSWORD="$POSTGRES_PASSWORD" "$POSTGRES_CONTAINER" \
  pg_restore --no-owner --no-acl --if-exists --clean -h 127.0.0.1 -U "$POSTGRES_USER" -d "$target_db" < "$BACKUP_FILE"

echo "Restore completed in database: $target_db"
if [ "$RESTORE_TARGET" = "primary" ]; then
  echo "Start services again with: docker compose up -d api worker"
fi
