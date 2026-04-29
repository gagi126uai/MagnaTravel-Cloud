#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

if [ -f .env ]; then
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
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

echo "Running one-shot database migrations..."
docker compose run --rm migrate

echo "Starting application services..."
docker compose up -d api worker reservas-service web whatsapp-bot postgres-backup

echo "Checking production readiness..."
bash scripts/ops/check-prod.sh
