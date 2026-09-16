#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <Noelia-version> <local-feed-directory>" >&2
  exit 64
fi

candidate_version="$1"
candidate_feed="$(cd "$2" && pwd)"
demo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
micro_project="noelia-gate-micro-$$"
monolith_project="noelia-gate-monolith-$$"
local_feed_parent="$demo_root/.local-feed"
mkdir -p "$local_feed_parent"
local_feed="$(mktemp -d "$local_feed_parent/gate.XXXXXX")"
micro_started=false
monolith_started=false

cleanup() {
  if [[ "$monolith_started" == true ]]; then
    docker compose -p "$monolith_project" -f "$demo_root/docker-compose.monolith.yml" \
      down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi
  if [[ "$micro_started" == true ]]; then
    docker compose -p "$micro_project" -f "$demo_root/docker-compose.yml" \
      down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi
  rm -rf -- "$local_feed"
}
trap cleanup EXIT

if [[ ! -f "$demo_root/.env" ]]; then
  echo "Demo .env is missing. Create its local JWT key pair without printing it." >&2
  exit 66
fi

env_mode="$(stat -f '%Lp' "$demo_root/.env" 2>/dev/null \
  || stat -c '%a' "$demo_root/.env")"
if (( (8#$env_mode & 8#077) != 0 )); then
  echo "Demo .env must not be readable or writable by group or others (current mode: $env_mode)." >&2
  exit 77
fi

docker compose --env-file "$demo_root/.env" -f "$demo_root/docker-compose.yml" config --quiet
docker compose --env-file "$demo_root/.env" -f "$demo_root/docker-compose.monolith.yml" config --quiet

package_count=0
while IFS= read -r package; do
  cp "$package" "$local_feed/"
  package_count=$((package_count + 1))
done < <(find "$candidate_feed" -maxdepth 1 -type f \
  -name "Noelia.*.${candidate_version}.nupkg" ! -name '*.snupkg' -print | sort)

if [[ "$package_count" != 13 ]]; then
  echo "Expected 13 candidate packages for Docker, found $package_count." >&2
  exit 1
fi

relative_feed="${local_feed#"$demo_root/"}"
export NOELIA_SOURCE="/src/$relative_feed"

micro_started=true
docker compose --env-file "$demo_root/.env" -p "$micro_project" \
  -f "$demo_root/docker-compose.yml" up --build --detach --wait --wait-timeout 240

for port in 8090 8081 8082; do
  for endpoint in health/live health/ready; do
    curl --fail --silent --show-error -o /dev/null "http://127.0.0.1:$port/$endpoint"
  done
done

for port in 8090 8081 8082; do
  micro_dashboard="$(curl --silent -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/noelia")"
  if [[ "$micro_dashboard" != 404 ]]; then
    echo "Production microservice dashboard on port $port must be hidden; received $micro_dashboard." >&2
    exit 1
  fi
done

docker compose -p "$micro_project" -f "$demo_root/docker-compose.yml" \
  down --volumes --remove-orphans
micro_started=false

monolith_started=true
docker compose --env-file "$demo_root/.env" -p "$monolith_project" \
  -f "$demo_root/docker-compose.monolith.yml" up --build --detach --wait --wait-timeout 240

for endpoint in health/live health/ready; do
  curl --fail --silent --show-error -o /dev/null "http://127.0.0.1:8091/$endpoint"
done

monolith_dashboard="$(curl --silent -o /dev/null -w '%{http_code}' http://127.0.0.1:8091/noelia)"
if [[ "$monolith_dashboard" != 404 ]]; then
  echo "Production monolith dashboard must be hidden; received $monolith_dashboard." >&2
  exit 1
fi

echo "Noelia $candidate_version passed both Docker health gates; Production dashboards stayed hidden."
