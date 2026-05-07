#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

# shellcheck disable=SC1091
. scripts/ops/lib/env.sh
load_env_file .env

is_placeholder() {
  local value="${1:-}"
  [ -z "$value" ] && return 0
  [[ "$value" == *CHANGE_THIS* ]] && return 0
  [[ "$value" == *change_this* ]] && return 0
  return 1
}

require_health() {
  local container="$1"
  local status
  status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container" 2>/dev/null || true)"

  if [ "$status" != "healthy" ] && [ "$status" != "running" ]; then
    echo "$container is not healthy/running. Current status: ${status:-missing}" >&2
    exit 1
  fi

  echo "$container: $status"
}

echo "Compose services:"
docker compose ps

require_health travel_db
require_health travel_api
require_health travel_worker
require_health travel_whatsapp_bot
require_health travel_minio
require_health travel_postgres_backup

echo "Checking API readiness..."
docker compose exec -T api curl -fsS http://127.0.0.1:8080/health/ready >/dev/null

echo "Checking worker readiness..."
docker compose exec -T worker curl -fsS http://127.0.0.1:8080/health/ready >/dev/null

if ! is_placeholder "${METRICS_TOKEN:-}"; then
  echo "Checking protected internal metrics..."
  docker compose exec -T api curl -fsS -H "X-Metrics-Token: $METRICS_TOKEN" http://127.0.0.1:8080/internal/metrics >/dev/null
fi

echo "Production check completed."
