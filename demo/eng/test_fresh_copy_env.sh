#!/usr/bin/env bash
# Tests for fresh-copy-env.sh. No Docker, no network: it only runs the generator into a
# temporary file and reads the result. Written before the script (#55): without the
# script every check here fails.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script="$here/fresh-copy-env.sh"
pass=0; fail=0
ok()  { pass=$((pass + 1)); echo "ok   - $1"; }
bad() { fail=$((fail + 1)); echo "FAIL - $1"; }

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

if [ ! -x "$script" ]; then
  bad "fresh-copy-env.sh is missing or not executable"
  echo "$pass passed, $fail failed"; exit 1
fi

out="$tmp/generated.env"
"$script" --out "$out" >/dev/null 2>&1 && ok "generator exits 0" || bad "generator exits 0"
[ -f "$out" ] && ok "file is written" || bad "file is written"

# Every variable the compose file needs, non-empty.
for name in NOELIA_JWT_KID NOELIA_JWT_PRIVATE_KEY NOELIA_JWT_PUBLIC_KEY \
            NOELIA_DASHBOARD_OPERATOR_SECRET NOELIA_MASTER_KEY \
            NOELIA_M2M_CLIENT_TODO_SERVICE_SECRET_HASH; do
  value="$(grep -E "^${name}=" "$out" 2>/dev/null | head -1 | cut -d= -f2-)"
  [ -n "$value" ] && ok "$name is set" || bad "$name is set"
done

# The public half must belong to the private half, or every verifier rejects every token.
priv="$(grep -E '^NOELIA_JWT_PRIVATE_KEY=' "$out" | cut -d= -f2-)"
pub="$(grep -E '^NOELIA_JWT_PUBLIC_KEY=' "$out" | cut -d= -f2-)"
derived="$(echo "$priv" | base64 -d 2>/dev/null | openssl pkey -inform DER -pubout -outform DER 2>/dev/null | base64 | tr -d '\n')"
[ -n "$derived" ] && [ "$derived" = "$pub" ] && ok "public key belongs to the private key" || bad "public key belongs to the private key"

# The master key is 32 bytes, base64 (.env.example).
mk="$(grep -E '^NOELIA_MASTER_KEY=' "$out" | cut -d= -f2-)"
[ "$(printf %s "$mk" | base64 -d 2>/dev/null | wc -c | tr -d ' ')" = "32" ] && ok "master key is 32 bytes" || bad "master key is 32 bytes"

# The machine-client hash has the documented shape: sha256: + 43 base64url characters.
h="$(grep -E '^NOELIA_M2M_CLIENT_TODO_SERVICE_SECRET_HASH=' "$out" | cut -d= -f2-)"
printf %s "$h" | grep -Eq '^sha256:[A-Za-z0-9_-]{43}$' && ok "client secret hash has the documented shape" || bad "client secret hash has the documented shape"

# Owner-only, because it holds a private key.
mode="$(stat -f %Lp "$out" 2>/dev/null || stat -c %a "$out" 2>/dev/null)"
[ "$mode" = "600" ] && ok "file is mode 600" || bad "file is mode 600 (was $mode)"

# It never overwrites: an existing file may hold the keys a running stack's volumes were made with.
before="$(cksum < "$out")"
"$script" --out "$out" >/dev/null 2>&1 && bad "refuses to overwrite an existing file" || ok "refuses to overwrite an existing file"
[ "$before" = "$(cksum < "$out")" ] && ok "existing file is untouched" || bad "existing file is untouched"

# Two runs give two different secrets (nothing fixed in the script).
out2="$tmp/second.env"
"$script" --out "$out2" >/dev/null 2>&1
[ "$(grep MASTER "$out")" != "$(grep MASTER "$out2")" ] && ok "values are random per run" || bad "values are random per run"

# Values never reach the terminal.
printed="$("$script" --out "$tmp/third.env" 2>&1)"
printf %s "$printed" | grep -Fq "$(grep -E '^NOELIA_MASTER_KEY=' "$tmp/third.env" | cut -d= -f2-)" \
  && bad "generator prints no secret value" || ok "generator prints no secret value"

echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
