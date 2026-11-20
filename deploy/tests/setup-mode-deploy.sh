#!/usr/bin/env bash
# Setup mode in the deploy forms (thekim1/fors#100), without booting the services: the Portainer bootstrap and
# init-deployment.sh each generate the admin API's setup secret once and never overwrite it, provide a deployment client
# secret only together with the deployment's authority, and keep a declared sign-in and the deployer's re-open value on
# a re-run, also for a deploy/.env written by the release before setup mode. CI runs it (verify.yml).
#
#   deploy/tests/setup-mode-deploy.sh        # needs Docker with Compose, jq and openssl; pulls the bootstrap's image
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"; project="fors-setup-deploy-$$"
# The variables a CI step or a shell may have set would change what is tested.
unset FORS_OIDC_AUTHORITY FORS_OIDC_CLIENT_ID FORS_OIDC_CLIENT_SECRET FORS_GATEWAY_HOST FORS_GATEWAY_PUBLIC_URL FORS_ADMIN_HOST \
  FORS_DOMAIN FORS_SETUP_REOPEN FORS_DATA_AUTHORITY COMPOSE_PROFILES FORS_CERTS_DIR FORS_SECRETS_DIR
export GATEWAY_IMAGE=gateway ADMINAPI_IMAGE=adminapi MIGRATIONS_IMAGE=migrations
stack=(docker compose -p "$project" -f "$repo/deploy/compose.portainer.yaml")
cleanup() { "${stack[@]}" down -v >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT
fail() { echo "error: $*" >&2; exit 1; }

# --- Portainer: the bootstrap, run as the stack runs it (its configs, volumes and environment) ----------------------
image="$(sed -n 's|^ *image: \(docker.io/alpine/openssl@sha256:[0-9a-f]*\)$|\1|p' "$repo/deploy/compose.portainer.yaml" | head -n 1)"
[ -n "$image" ] || fail "the bootstrap's image is not in compose.portainer.yaml"
bootstrap() { FORS_DOMAIN=example.se "${stack[@]}" run --rm --no-deps "$@" bootstrap 2>&1; }
secrets() { docker run --rm --entrypoint sh -v "${project}_fors-secrets:/s:ro" "$image" -c "$1"; }

out="$(bootstrap)" || { echo "$out"; fail "the bootstrap failed"; }
grep -q 'set in the setup wizard: https://llm-admin.example.se:9443/setup' <<< "$out" || { echo "$out"; fail "the bootstrap does not name the wizard"; }
secrets 'grep -qxE "[0-9a-f]{64}" /s/adminapi/Setup__Secret && [ "$(stat -c %u%a /s/adminapi/Setup__Secret)" = 1654600 ]' \
  || fail "adminapi/Setup__Secret is not 64 hex characters owned by 1654 with mode 600"
secrets '[ -z "$(find /s -name Setup__Secret ! -path "/s/adminapi/*")" ]' || fail "a setup secret outside adminapi/"
secrets '[ ! -e /s/adminapi/Oidc__ClientSecret ]' || fail "a client secret file without FORS_OIDC_CLIENT_SECRET"
first="$(secrets 'sha256sum /s/adminapi/Setup__Secret')"

# A client secret without the authority is left out (it would fail every test sign-in of the wizard) and named.
out="$(bootstrap -e FORS_OIDC_CLIENT_SECRET=s3cret)" || { echo "$out"; fail "the second bootstrap failed"; }
grep -q 'FORS_OIDC_CLIENT_SECRET is left out' <<< "$out" || { echo "$out"; fail "the left-out client secret is not named"; }
secrets '[ ! -e /s/adminapi/Oidc__ClientSecret ]' || fail "a client secret file without FORS_OIDC_AUTHORITY"
[ "$(secrets 'sha256sum /s/adminapi/Setup__Secret')" = "$first" ] || fail "the second bootstrap replaced the setup secret"

# Declared: the authority with the secret writes the file and says the wizard stays closed; without the variable the
# file goes again.
out="$(bootstrap -e FORS_OIDC_AUTHORITY=https://idp.test/realms/fors -e FORS_OIDC_CLIENT_SECRET=s3cret)" \
  || { echo "$out"; fail "the declared bootstrap failed"; }
grep -q 'declared by the stack' <<< "$out" || { echo "$out"; fail "the bootstrap does not say sign-in is declared"; }
[ "$(secrets 'cat /s/adminapi/Oidc__ClientSecret')" = s3cret ] || fail "the declared client secret was not written"
bootstrap -e FORS_OIDC_AUTHORITY=https://idp.test/realms/fors > /dev/null || fail "the bootstrap without the secret failed"
secrets '[ ! -e /s/adminapi/Oidc__ClientSecret ]' || fail "the client secret file outlived its variable"
[ "$(secrets 'sha256sum /s/adminapi/Setup__Secret')" = "$first" ] || fail "a later bootstrap replaced the setup secret"
echo "ok: Portainer bootstrap"

# --- init-deployment.sh, in a copy of deploy/ (it writes next to itself) -----------------------------------------------
d="$work/new"; mkdir -p "$d"; cp -R "$repo/deploy" "$d/"
s="$d/deploy/secrets/adminapi"; env_file="$d/deploy/.env"
init() { bash "$d/deploy/init-deployment.sh" --no-chown --yes --domain example.se "$@" < /dev/null > "$d/init.log" 2>&1; }
admin_env() { docker compose -f "$d/deploy/compose.prod.yaml" --env-file "$env_file" --profile data config --format json | jq -e "$1" > /dev/null; }

# A new deployment without sign-in options: nothing declares sign-in, and the closing notes point at the wizard.
init || { cat "$d/init.log"; fail "init-deployment.sh without --oidc-authority failed"; }
grep -qxE '[0-9a-f]{64}' "$s/Setup__Secret" || fail "secrets/adminapi/Setup__Secret is not 64 hex characters"
[ ! -e "$s/Oidc__ClientSecret" ] || fail "a client secret file without --oidc-client-secret-file"
for line in FORS_OIDC_AUTHORITY= FORS_OIDC_CLIENT_ID= FORS_SETUP_REOPEN=; do
  grep -qx "$line" "$env_file" || fail "deploy/.env lacks the line $line"
done
grep -q 'Set up sign-in in the setup wizard' "$d/init.log" || fail "the closing notes do not name the wizard"
grep -q "logs adminapi | grep 'Setup code'" "$d/init.log" || fail "the closing notes do not say where the code is"
admin_env '.services.adminapi.environment | [to_entries[] | select((.key | startswith("Oidc__")) and .value != "")] | length == 0' \
  || fail "the admin API gets a non-empty Oidc__* value without sign-in options"
admin_env '.services.dataapi.environment.DataApi__Authority == ""' || fail "the Data API got an authority from nowhere"
first="$(sha256sum "$s/Setup__Secret")"

# The deployer's re-open value survives a re-run and reaches the admin API; the setup secret is not replaced.
sed -i 's/^FORS_SETUP_REOPEN=.*/FORS_SETUP_REOPEN=2026-11-16/' "$env_file"
init || { cat "$d/init.log"; fail "the re-run failed"; }
grep -qx 'FORS_SETUP_REOPEN=2026-11-16' "$env_file" || fail "the re-run dropped FORS_SETUP_REOPEN"
[ "$(sha256sum "$s/Setup__Secret")" = "$first" ] || fail "the re-run replaced the setup secret"
admin_env '.services.adminapi.environment.Setup__Reopen == "2026-11-16"' || fail "Setup__Reopen does not reach the admin API"

# A deployment client secret needs the deployment's authority.
printf 'secret\n' > "$d/client.secret"
if init --oidc-client-secret-file "$d/client.secret"; then fail "--oidc-client-secret-file without an authority was accepted"; fi
grep -q -- '--oidc-client-secret-file needs --oidc-authority' "$d/init.log" || fail "the refusal does not say why"
[ ! -e "$s/Oidc__ClientSecret" ] || fail "the refused run wrote the client secret"

# An existing deployment from before setup mode: its deploy/.env sets the authority and client id and has no
# FORS_SETUP_REOPEN, its secrets no setup secret. A re-run without options keeps it declared and adds what is new.
d="$work/old"; mkdir -p "$d"; cp -R "$repo/deploy" "$d/"
s="$d/deploy/secrets/adminapi"; env_file="$d/deploy/.env"
init --oidc-authority https://idp.example.se/realms/fors --oidc-client-id fors-admin --oidc-client-secret-file "$work/new/client.secret" \
  || { cat "$d/init.log"; fail "init-deployment.sh with --oidc-authority failed"; }
sed -i '/^FORS_SETUP_REOPEN=/d' "$env_file"; rm "$s/Setup__Secret"
init || { cat "$d/init.log"; fail "the re-run of an existing deployment failed"; }
grep -qx 'FORS_OIDC_AUTHORITY=https://idp.example.se/realms/fors' "$env_file" || fail "the re-run dropped the authority"
grep -qx 'FORS_OIDC_CLIENT_ID=fors-admin' "$env_file" || fail "the re-run dropped the client id"
grep -qx 'FORS_SETUP_REOPEN=' "$env_file" || fail "the re-run did not add FORS_SETUP_REOPEN"
grep -qxE '[0-9a-f]{64}' "$s/Setup__Secret" || fail "the re-run did not generate the setup secret"
[ "$(cat "$s/Oidc__ClientSecret")" = secret ] || fail "the re-run touched the client secret"
grep -q 'Sign-in is declared here' "$d/init.log" || fail "the closing notes do not say sign-in is declared"
admin_env '.services.adminapi.environment.Oidc__Authority == "https://idp.example.se/realms/fors"
  and .services.dataapi.environment.DataApi__Authority == "https://idp.example.se/realms/fors"
  and .services.gateway.environment.Gateway__OidcAuthority == "https://idp.example.se/realms/fors"' \
  || fail "the declared authority does not reach the admin API, the Data API and the gateway"
bash "$d/deploy/init-deployment.sh" --renew-certs --no-chown < /dev/null > "$d/renew.log" 2>&1 \
  || { cat "$d/renew.log"; fail "--renew-certs failed"; }
grep -qx 'FORS_OIDC_AUTHORITY=https://idp.example.se/realms/fors' "$env_file" || fail "--renew-certs changed the authority"
echo "ok: init-deployment.sh"
