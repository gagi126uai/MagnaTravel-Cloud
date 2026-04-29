#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

if [ -f .env ]; then
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
fi

PROJECT_NAME="${COMPOSE_PROJECT_NAME:-magnatravel-cloud}"
BACKUP_ROOT="${VOLUME_BACKUP_ROOT:-backups/volumes}"
timestamp="$(date +%Y%m%d-%H%M%S)"
target_dir="$BACKUP_ROOT/$timestamp"

volumes=(
  "${PROJECT_NAME}_minio_data"
  "${PROJECT_NAME}_whatsapp_auth"
  "${PROJECT_NAME}_rabbitmq_data"
)

if [ "${INCLUDE_PGDATA:-false}" = "true" ]; then
  echo "Including pgdata volume. Prefer pg_dump for PostgreSQL; live filesystem volume backups are only safe with the DB stopped or snapshotted."
  volumes+=("${PROJECT_NAME}_pgdata")
fi

mkdir -p "$target_dir"

for volume in "${volumes[@]}"; do
  if ! docker volume inspect "$volume" >/dev/null 2>&1; then
    echo "Skipping missing volume: $volume"
    continue
  fi

  archive="$volume.tar.gz"
  echo "Backing up volume $volume -> $target_dir/$archive"
  docker run --rm \
    -v "$volume:/data:ro" \
    -v "$(pwd)/$target_dir:/backup" \
    alpine:3.20 \
    sh -c "cd /data && tar -czf /backup/$archive ."
done

echo "Volume backup completed: $target_dir"
