#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

echo "Docker disk usage:"
docker system df -v

echo
echo "Project containers:"
docker compose ps

echo
echo "Largest Docker log files:"
container_ids="$(docker ps -aq)"
if [ -n "$container_ids" ]; then
  docker inspect --format '{{.Name}} {{.LogPath}}' $container_ids 2>/dev/null \
    | while read -r name log_path; do
        [ -n "$log_path" ] && [ -f "$log_path" ] && du -h "$log_path" | awk -v container="$name" '{ print $1, container, $2 }'
      done \
    | sort -hr \
    | head -n 20
else
  echo "No Docker containers found."
fi

echo
echo "Backup directory size:"
du -sh backups 2>/dev/null || true

echo
echo "Docker root size:"
docker info --format '{{.DockerRootDir}}' 2>/dev/null \
  | xargs -r du -sh 2>/dev/null || true
