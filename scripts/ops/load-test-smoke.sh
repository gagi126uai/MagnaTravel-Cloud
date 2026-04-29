#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/../.."

BASE_URL="${BASE_URL:-http://localhost:3000}"
REQUESTS="${REQUESTS:-20}"
CONCURRENCY="${CONCURRENCY:-4}"
LOAD_TEST_PATHS="${LOAD_TEST_PATHS:-/api/health/ready}"
ACCESS_TOKEN="${ACCESS_TOKEN:-}"
export BASE_URL ACCESS_TOKEN

IFS=',' read -r -a paths <<< "$LOAD_TEST_PATHS"

tasks_file="$(mktemp)"
results_file="$(mktemp)"
trap 'rm -f "$tasks_file" "$results_file"' EXIT

for _ in $(seq 1 "$REQUESTS"); do
  for path in "${paths[@]}"; do
    trimmed_path="$(echo "$path" | xargs)"
    [ -n "$trimmed_path" ] && printf '%s\n' "$trimmed_path" >> "$tasks_file"
  done
done

echo "Running smoke load test against $BASE_URL with $REQUESTS rounds and concurrency $CONCURRENCY"

cat "$tasks_file" | xargs -P "$CONCURRENCY" -I {} bash -c '
  set -euo pipefail
  path="$1"
  url="${BASE_URL%/}$path"
  auth_header=()
  if [ -n "${ACCESS_TOKEN:-}" ]; then
    auth_header=(-H "Authorization: Bearer $ACCESS_TOKEN")
  fi
  curl -sS -o /dev/null -w "%{http_code} %{time_total} $path\n" "${auth_header[@]}" "$url"
' _ {} >> "$results_file"

cat "$results_file"

total="$(wc -l < "$results_file" | xargs)"
failures="$(awk '$1 < 200 || $1 >= 400 { count++ } END { print count + 0 }' "$results_file")"
p95="$(awk '{ print $2 }' "$results_file" | sort -n | awk -v total="$total" 'BEGIN { rank = int(total * 0.95); if (rank < 1) rank = 1 } NR == rank { print $1 } END { if (total == 0) print "0" }')"

echo "Total requests: $total"
echo "HTTP failures: $failures"
echo "Approx p95 seconds: $p95"

if [ "$failures" -gt 0 ]; then
  exit 1
fi
