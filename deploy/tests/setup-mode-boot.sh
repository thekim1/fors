#!/usr/bin/env bash
# Boots the Portainer stack (compose.portainer.yaml) the way a new deployment for the setup wizard starts it, with only
# UME_DOMAIN and the images, and checks setup mode end to end up to the wizard's session (thekim1/llmgateway#100): the
# admin API prints a setup code on its standard output, /bff/user says sign-in is not configured and setup is open, a
# wrong code is refused, the printed code gives a setup session, a restart within the hour prints the same code; then
# the stack is declared with UME_OIDC_AUTHORITY, and setup counts as completed and closes (404). The test sign-in at a
# real provider is not part of it.
#
#   deploy/tests/setup-mode-boot.sh     # uses the local images ume-llm-gateway/*:latest, or GATEWAY_IMAGE,
#                                       # ADMINAPI_IMAGE and MIGRATIONS_IMAGE; needs Docker with Compose, curl and jq
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"; project=ume-setup-boot; host=llm-admin.setup.test; port=19443
unset UME_OIDC_AUTHORITY UME_OIDC_CLIENT_ID UME_OIDC_CLIENT_SECRET UME_GATEWAY_HOST UME_GATEWAY_PUBLIC_URL UME_ADMIN_HOST \
  UME_SETUP_REOPEN UME_DATA_AUTHORITY COMPOSE_PROFILES
export GATEWAY_IMAGE="${GATEWAY_IMAGE:-ume-llm-gateway/gateway:latest}"
export ADMINAPI_IMAGE="${ADMINAPI_IMAGE:-ume-llm-gateway/adminapi:latest}"
export MIGRATIONS_IMAGE="${MIGRATIONS_IMAGE:-ume-llm-gateway/migrations:latest}"
export UME_DOMAIN=setup.test UME_GATEWAY_BIND=127.0.0.1 UME_ADMIN_BIND=127.0.0.1 UME_GATEWAY_PORT=18443 UME_ADMIN_PORT="$port"
stack=(docker compose -p "$project" -f "$repo/deploy/compose.portainer.yaml")
cleanup() { "${stack[@]}" down -v >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT
fail() { echo "error: $*" >&2; "${stack[@]}" logs --no-color --tail 40 adminapi >&2 || true; exit 1; }

up() { "${stack[@]}" up -d --wait --wait-timeout 300 >/dev/null 2>&1 || { "${stack[@]}" ps -a >&2; fail "the stack did not start"; }; }
codes() { "${stack[@]}" logs --no-color adminapi | sed -n 's/.*adminapi: setup mode is open until .*Setup code: \([0-9A-Z-]*\).*/\1/p'; }
# The admin API with the internal CA, a cookie jar and the BFF's antiforgery headers on every request.
api() { # api METHOD PATH [JSON]: prints the body, then the status on its own line
  local xsrf; xsrf="$(awk '$6 == "XSRF-TOKEN" {print $7}' "$work/jar" 2>/dev/null | tail -n 1)"
  curl -sS --cacert "$work/ca.pem" --resolve "$host:$port:127.0.0.1" -b "$work/jar" -c "$work/jar" -X "$1" \
    -H 'X-Requested-With: XMLHttpRequest' -H "X-XSRF-TOKEN: $xsrf" ${3:+-H 'Content-Type: application/json' --data "$3"} \
    -w '\n%{http_code}' "https://$host:$port$2"
}
status() { tail -n 1 <<< "$1"; }
body() { sed '$d' <<< "$1"; }

up
"${stack[@]}" run --rm --no-deps --entrypoint cat bootstrap /certs/ca.pem > "$work/ca.pem" 2>/dev/null || fail "no internal CA"
for _ in $(seq 30); do [ -n "$(codes)" ] && break; sleep 2; done
code="$(codes | tail -n 1)"
[ -n "$code" ] || fail "no setup code on the admin API's output"
echo "ok: setup code printed"

r="$(api GET /bff/user)"; [ "$(status "$r")" = 200 ] || fail "/bff/user answered $(status "$r")"
body "$r" | jq -e '.signIn.configured == false and .setup.open == true' > /dev/null || fail "/bff/user: $(body "$r")"
r="$(api POST /bff/setup/session '{"code":"00000-00000-00000-00000-000000"}')"
[ "$(status "$r")" = 400 ] && body "$r" | jq -e '.code == "setup_code_invalid"' > /dev/null || fail "a wrong code: $r"
r="$(api POST /bff/setup/session "{\"code\":\"$code\"}")"
[ "$(status "$r")" = 200 ] || fail "the printed code was refused: $r"
r="$(api GET /bff/setup/state)"
[ "$(status "$r")" = 200 ] || fail "/bff/setup/state answered $(status "$r")"
body "$r" | jq -e --arg uri "https://$host:$port/signin-oidc" '.kind == "first_run" and .redirectUris.signIn == $uri
  and .signIn.authority.value == null' > /dev/null || fail "/bff/setup/state: $(body "$r")"
echo "ok: the code gives a setup session; the wizard's state names the redirect URI"

"${stack[@]}" restart adminapi > /dev/null 2>&1
up
for _ in $(seq 30); do [ "$(codes | wc -l)" -ge 2 ] && break; sleep 2; done
[ "$(codes | tail -n 1)" = "$code" ] || fail "a restart within the hour printed another code"
echo "ok: a restart within the hour prints the same code"

export UME_OIDC_AUTHORITY=https://idp.setup.test/realms/ume
up
declared='The deployment declares the sign-in (Oidc:Authority): setup mode counts as completed'
# grep reads all of the logs (no -q): with pipefail, grep -q leaving early fails the pipeline through SIGPIPE.
declared_logged() { "${stack[@]}" logs --no-color adminapi | grep -F "$declared" > /dev/null; }
for _ in $(seq 15); do declared_logged && break; sleep 2; done
declared_logged || fail "the declared admin API did not count setup as completed"
r="$(api POST /bff/setup/session "{\"code\":\"$code\"}")"; [ "$(status "$r")" = 404 ] || fail "setup is still open when declared: $r"
r="$(api GET /bff/user)"
body "$r" | jq -e '.signIn.configured == true and .setup.open == false' > /dev/null || fail "/bff/user when declared: $(body "$r")"
echo "ok: declaring the authority completes setup and closes it"
