#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

if [ -f .env ]; then
  # shellcheck disable=SC1091
  . scripts/ops/lib/env.sh
  load_env_file .env
else
  echo ".env is required for production deploy." >&2
  exit 1
fi

is_placeholder() {
  local value="${1:-}"
  [ -z "$value" ] && return 0
  [[ "$value" == *CHANGE_THIS* ]] && return 0
  [[ "$value" == *change_this* ]] && return 0
  [[ "$value" == *travelpass* ]] && return 0
  [[ "$value" == minioadmin ]] && return 0
  [[ "$value" == guest ]] && return 0
  return 1
}

require_secret() {
  local name="$1"
  local value="${!name:-}"
  if is_placeholder "$value"; then
    echo "$name is missing or still uses a placeholder/default value." >&2
    exit 1
  fi
}

required_secrets=(
  POSTGRES_PASSWORD
  JWT_KEY
  SECURITY_ENCRYPTION_KEY
  WHATSAPP_WEBHOOK_SECRET
  METRICS_TOKEN
  RESERVATIONS_INTERNAL_TOKEN
  RABBITMQ_PASSWORD
  MINIO_ROOT_USER
  MINIO_ROOT_PASSWORD
)

for secret_name in "${required_secrets[@]}"; do
  require_secret "$secret_name"
done

echo "Building application images..."
docker compose build api worker reservas-service web whatsapp-bot

echo "Starting infrastructure..."
docker compose up -d db rabbitmq minio

echo "Running one-shot database migrations (waiting for completion)..."
# Importante: NO usar `docker compose run --rm migrate` aqui — eso crea un
# container con nombre random que no satisface el `depends_on: migrate
# (service_completed_successfully)` que tienen api/worker/reservas-service en
# docker-compose.yml. Hay que usar el container `travel_migrate` real.
docker compose up -d --force-recreate --no-deps migrate
echo "Waiting for migrate container to finish..."
migrate_exit=$(docker wait travel_migrate)
if [ "$migrate_exit" != "0" ]; then
  echo "Migration failed (exit code $migrate_exit). Last logs:" >&2
  docker logs --tail=80 travel_migrate >&2
  exit 1
fi
echo "Migrations applied successfully."

echo "Starting application services..."
docker compose up -d api worker reservas-service web whatsapp-bot postgres-backup

echo "Checking production readiness..."
bash scripts/ops/check-prod.sh

if [ "${RUN_DOCKER_CLEANUP_AFTER_DEPLOY:-false}" = "true" ]; then
  echo "Running post-deploy Docker cleanup..."
  bash scripts/ops/docker-cleanup.sh --execute
fi
