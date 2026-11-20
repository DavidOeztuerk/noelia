#!/usr/bin/env bash
# Writes a throwaway demo/.env for a fresh copy of the repository (#55).
#
# The file is gitignored, so a fresh copy (`git worktree add … vX.Y.Z`, a clone, CI) has none, and
# `docker compose up` stops on the first variable it needs. In the main copy nobody notices,
# because the file is already there.
#
# Every value is generated here and is good for this copy only: nothing in it belongs to
# a real installation. Never use it for anything that holds data you want to keep. The compose
# project name `noelia-demo` is fixed, so every copy shares the volumes; a stack started with
# throwaway keys has to be removed afterwards, or the next run with other keys meets a key ring
# encrypted with a master key it does not have:
#
#   docker compose --profile all down -v
#
# Usage: eng/fresh-copy-env.sh [--out <path>]      (default: demo/.env)
# It refuses to overwrite: an existing file may hold the keys a running stack was built with.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="$here/../.env"
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out="${2:?--out needs a path}"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 64 ;;
  esac
done

if [ -e "$out" ]; then
  echo "refusing to overwrite $out (it may hold the keys of a running stack)" >&2
  exit 1
fi

b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }

kid="throwaway-$(date -u +%Y%m%d)"
priv="$(openssl ecparam -name prime256v1 -genkey -noout -outform DER \
  | openssl pkcs8 -topk8 -nocrypt -inform DER -outform DER | base64 | tr -d '\n')"
pub="$(echo "$priv" | base64 -d | openssl pkey -inform DER -pubout -outform DER | base64 | tr -d '\n')"
operator="$(openssl rand -base64 32)"
master="$(openssl rand -base64 32)"
# The hash of a machine client's secret, never the secret: nothing in the demo calls the
# issuer yet, so the plaintext is neither needed nor kept (demo/README.md, "The issuer's machine client").
secret="$(openssl rand 32 | b64url)"
hash="sha256:$(printf %s "$secret" | openssl dgst -sha256 -binary | b64url)"

umask 077
{
  echo "# Throwaway values from eng/fresh-copy-env.sh. Good for this copy only."
  echo "# Remove the stack afterwards: docker compose --profile all down -v"
  echo "NOELIA_JWT_KID=$kid"
  echo "NOELIA_JWT_PRIVATE_KEY=$priv"
  echo "NOELIA_JWT_PUBLIC_KEY=$pub"
  echo "NOELIA_DASHBOARD_OPERATOR_SECRET=$operator"
  echo "NOELIA_MASTER_KEY=$master"
  echo "NOELIA_M2M_CLIENT_TODO_SERVICE_SECRET_HASH=$hash"
} > "$out"
chmod 600 "$out"

# Names and place only, never a value.
echo "wrote $out (6 throwaway variables). Remove the stack afterwards: docker compose --profile all down -v"
