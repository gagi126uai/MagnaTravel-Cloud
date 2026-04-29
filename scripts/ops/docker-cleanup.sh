#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

# shellcheck disable=SC1091
. scripts/ops/lib/env.sh
load_env_file .env

EXECUTE=false

while [ $# -gt 0 ]; do
  case "$1" in
    --execute)
      EXECUTE=true
      shift
      ;;
    -h|--help)
      cat <<'USAGE'
Usage:
  bash scripts/ops/docker-cleanup.sh          # dry-run, prints planned cleanup
  bash scripts/ops/docker-cleanup.sh --execute

Environment:
  DOCKER_CLEANUP_KEEP_HOURS=168       # keep unused objects newer than 7 days
  DOCKER_CLEANUP_BUILD_CACHE=true     # prune old build cache
  DOCKER_CLEANUP_IMAGES=true          # prune unused images
  DOCKER_CLEANUP_CONTAINERS=true      # prune stopped containers
  DOCKER_CLEANUP_NETWORKS=true        # prune unused networks
  DOCKER_CLEANUP_VOLUMES=false        # keep false unless you know exactly why
USAGE
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

KEEP_HOURS="${DOCKER_CLEANUP_KEEP_HOURS:-168}"
PRUNE_BUILD_CACHE="${DOCKER_CLEANUP_BUILD_CACHE:-true}"
PRUNE_IMAGES="${DOCKER_CLEANUP_IMAGES:-true}"
PRUNE_CONTAINERS="${DOCKER_CLEANUP_CONTAINERS:-true}"
PRUNE_NETWORKS="${DOCKER_CLEANUP_NETWORKS:-true}"
PRUNE_VOLUMES="${DOCKER_CLEANUP_VOLUMES:-false}"

if ! [[ "$KEEP_HOURS" =~ ^[0-9]+$ ]]; then
  echo "DOCKER_CLEANUP_KEEP_HOURS must be a positive integer." >&2
  exit 1
fi

run_or_print() {
  if [ "$EXECUTE" = "true" ]; then
    "$@"
  else
    printf 'DRY RUN:'
    printf ' %q' "$@"
    printf '\n'
  fi
}

echo "Current Docker disk usage:"
docker system df
echo

echo "Cleanup policy: remove unused Docker objects older than ${KEEP_HOURS}h."
echo "Volumes are protected unless DOCKER_CLEANUP_VOLUMES=true."
echo

if [ "$PRUNE_CONTAINERS" = "true" ]; then
  run_or_print docker container prune --force --filter "until=${KEEP_HOURS}h"
fi

if [ "$PRUNE_NETWORKS" = "true" ]; then
  run_or_print docker network prune --force
fi

if [ "$PRUNE_IMAGES" = "true" ]; then
  run_or_print docker image prune --all --force --filter "until=${KEEP_HOURS}h"
fi

if [ "$PRUNE_BUILD_CACHE" = "true" ]; then
  run_or_print docker builder prune --all --force --filter "until=${KEEP_HOURS}h"
fi

if [ "$PRUNE_VOLUMES" = "true" ]; then
  echo "WARNING: pruning unused volumes. This can delete data if a volume is detached."
  run_or_print docker volume prune --force --filter "label!=com.docker.compose.project=magnatravel-cloud"
fi

echo
echo "Docker disk usage after planned/executed cleanup:"
docker system df
