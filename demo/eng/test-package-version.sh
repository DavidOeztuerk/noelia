#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <Noelia-version> <local-feed-directory>" >&2
  exit 64
fi

candidate_version="$1"
candidate_feed_input="$2"

if [[ ! "$candidate_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]]; then
  echo "Invalid package version: $candidate_version" >&2
  exit 64
fi

if [[ ! -d "$candidate_feed_input" ]]; then
  echo "Local feed does not exist: $candidate_feed_input" >&2
  exit 66
fi

demo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
candidate_feed="$(cd "$candidate_feed_input" && pwd)"
candidate_packages="$(mktemp -d /tmp/noelia-demo-candidate-packages.XXXXXX)"
candidate_config="$(mktemp /tmp/noelia-demo-nuget.XXXXXX)"
candidate_root_input="$(mktemp -d /tmp/noelia-demo-candidate-workspace.XXXXXX)"
candidate_root="$(cd "$candidate_root_input" && pwd -P)"
candidate_transcript="$(mktemp /tmp/noelia-demo-candidate-transcript.XXXXXX)"
candidate_canary="NOELIA-GATE-CANARY-5.0-$$-$(date +%s)"

cleanup() {
  rm -rf -- "$candidate_packages"
  rm -rf -- "$candidate_root"
  rm -f -- "$candidate_config"
  rm -f -- "$candidate_transcript"
}
trap cleanup EXIT

export NOELIA_GATE_CANARY="$candidate_canary"

expected_packages=(
  Noelia.Abstractions
  Noelia.Application
  Noelia.Contracts
  Noelia.Core
  Noelia.Dashboard
  Noelia.Data.EntityFrameworkCore
  Noelia.Http
  Noelia.InMemory
  Noelia.Infrastructure
  Noelia.Messaging.MassTransit
  Noelia.Passwords.Argon2
  Noelia.Passwords.BCrypt
  Noelia.Redis
)

probe_projects=0
while IFS= read -r project; do
  probe_projects=$((probe_projects + 1))
  direct_count="$(rg --no-filename -o 'PackageReference Include="Noelia\.[^"]+"' "$project" | wc -l | tr -d ' ')"
  if [[ "$direct_count" != 1 ]]; then
    echo "Probe must directly reference exactly one Noelia package: $project" >&2
    exit 1
  fi
done < <(find "$demo_root/probes" -name '*.csproj' -type f -print | sort)

if [[ "$probe_projects" != "${#expected_packages[@]}" ]]; then
  echo "Expected ${#expected_packages[@]} isolated probes, found $probe_projects." >&2
  exit 1
fi

direct_packages="$(rg --no-filename -o 'PackageReference Include="Noelia\.[^"]+"' \
  "$demo_root/probes" -g '*.csproj' \
  | sed -E 's/.*Include="([^"]+)"/\1/' \
  | sort)"

for package in "${expected_packages[@]}"; do
  count="$(printf '%s\n' "$direct_packages" | rg -x "$package" | wc -l | tr -d ' ')"
  if [[ "$count" != 1 ]]; then
    echo "Package matrix must contain $package exactly once; found $count." >&2
    exit 1
  fi
done

if rg -n 'Projects/Noelia|ProjectReference[^>]+/Users/' \
  "$demo_root" -g '*.csproj'; then
  echo "A Demo project references the framework repository directly." >&2
  exit 1
fi

rsync -a \
  --exclude '.git/' \
  --exclude '.env' \
  --exclude '.env.*' \
  --exclude 'bin/' \
  --exclude 'obj/' \
  --exclude 'node_modules/' \
  --exclude 'artifacts/' \
  --exclude '.packages/' \
  --exclude '.local-feed/' \
  "$demo_root/" "$candidate_root/"

case "$candidate_feed" in
  *'&'*|*'<'*|*'>'*|*'"'*|*"'"*)
    echo "Local feed path contains a character that is unsafe in NuGet XML: $candidate_feed" >&2
    exit 65
    ;;
esac

cat > "$candidate_config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$candidate_feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="candidate">
      <package pattern="Noelia.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

run() {
  "$@" 2>&1 | tee -a "$candidate_transcript"
}

run dotnet restore "$candidate_root/Noelia.TodoDemo.sln" \
  -p:NoeliaVersion="$candidate_version" \
  --configfile "$candidate_config" \
  --packages "$candidate_packages" \
  --force \
  --no-cache

found_noelia_package=false
while IFS= read -r assets_file; do
  while IFS= read -r package; do
    found_noelia_package=true
    resolved_version="${package##*/}"
    if [[ "$resolved_version" != "$candidate_version" ]]; then
      echo "Unexpected Noelia package in $assets_file: $package" >&2
      exit 1
    fi
  done < <(jq -r '.libraries | keys[] | select(startswith("Noelia."))' "$assets_file")
done < <(find "$candidate_root" -path '*/obj/project.assets.json' -type f -print)

if [[ "$found_noelia_package" != true ]]; then
  echo "No Noelia package was resolved; the candidate was not tested." >&2
  exit 1
fi

run dotnet build "$candidate_root/Noelia.TodoDemo.sln" \
  -p:NoeliaVersion="$candidate_version" \
  --no-restore

run dotnet test "$candidate_root/Noelia.TodoDemo.sln" \
  -p:NoeliaVersion="$candidate_version" \
  --no-build \
  --no-restore \
  --logger 'console;verbosity=minimal'

run dotnet list "$candidate_root/Noelia.TodoDemo.sln" package \
  --vulnerable \
  --include-transitive \
  --no-restore \
  --configfile "$candidate_config"

run npm --prefix "$candidate_root/src/frontend" ci
run npm --prefix "$candidate_root/src/frontend" test

if rg -F "$candidate_canary" "$candidate_transcript"; then
  echo "The release-gate canary reached build, test, audit or browser output." >&2
  exit 1
fi

echo "Noelia $candidate_version passed the Demo package gate."
