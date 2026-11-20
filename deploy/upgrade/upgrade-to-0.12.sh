#!/usr/bin/env bash
# Upgrades a Docker Compose installation (deploy/compose.prod.yaml, set up with init-deployment.sh) from 0.11 or earlier
# to Fors 0.12.0, where every name from before the rename to Fors (ume) changes (docs/upgrade-guide.md, "0.12.0").
# Run it from the new release's directory, on the Docker host, with the old installation still running:
#
#   ./deploy/upgrade/upgrade-to-0.12.sh                  # asks before it stops anything
#   ./deploy/upgrade/upgrade-to-0.12.sh --yes            # no questions
#   ./deploy/upgrade/upgrade-to-0.12.sh --old-project ume-gateway --env-file deploy/.env
#
# What it does, in this order (each step is skipped when it was done already, so a re-run is safe):
#   1. deploy/.env (and deploy/keycloak/.env): renames the variables UME_* to FORS_* (the old files are kept as
#      .env.pre-0.12) and writes the names an existing installation keeps: the OIDC client ume-admin and the Data API
#      audience ume-data-api when the file sets none (the old defaults), and for the bundled Keycloak its realm ume,
#      client ume-admin and import client ume-realm-import.
#   2. stops the old project's services except Postgres, and the old Keycloak project;
#   3. renames the database roles ume_* to fors_* (deploy/upgrade/0.12-rename-roles.sql, as the Postgres superuser);
#   4. removes the old project's containers and networks (its volumes stay);
#   5. copies the volume <old project>_postgres-data to fors-gateway_postgres-data. The old volume is kept: remove it
#      once the new version runs (docker volume rm ume-gateway_postgres-data).
# Then re-run ./deploy/init-deployment.sh (it writes the connection strings with the new role names) and start the
# release as usual. The certificates, secrets and the key pepper are files in deploy/certs and deploy/secrets and do not
# move. Back up the database and those two directories first (docs/runbook.md).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deploy="$(cd "$here/.." && pwd)"
old_project="ume-gateway"; new_project="fors-gateway"; old_keycloak="ume-keycloak"
env_file="$deploy/.env"; keycloak_env="$deploy/keycloak/.env"; yes=0

usage() { sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }
step() { echo; echo "== $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --old-project) old_project="$2"; shift 2;;
    --env-file) env_file="$2"; shift 2;;
    --yes|-y) yes=1; shift;;
    -h|--help) usage;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done

command -v docker >/dev/null || die "docker is required"
[ -f "$env_file" ] || die "$env_file not found: copy deploy/.env from the old installation here first"

# --- 1. Variables ------------------------------------------------------------------------------------------------------
# set_default FILE NAME VALUE: adds NAME=VALUE unless the file sets NAME (also an empty NAME=, which means unset on purpose).
set_default() { grep -qE "^$2=" "$1" || printf '%s=%s\n' "$2" "$3" >> "$1"; }
rename_vars() {
  local f="$1"
  if grep -qE '^[[:space:]]*#?[[:space:]]*UME_' "$f"; then
    [ -e "$f.pre-0.12" ] || cp -p "$f" "$f.pre-0.12"
    sed -i -E 's/(^|[^A-Za-z0-9_])UME_/\1FORS_/g' "$f"
    echo "renamed the variables in $f (the old file is $f.pre-0.12)"
  fi
}

step "1. Variables"
rename_vars "$env_file"
# The old defaults, kept as deployment values: the client registered in the identity provider and the audience its
# Data API tokens carry. Remove a line to take the new default (fors-admin, fors-data-api) once the provider has it.
if grep -qE '^FORS_OIDC_AUTHORITY=.+' "$env_file"; then
  set_default "$env_file" FORS_OIDC_CLIENT_ID ume-admin
fi
set_default "$env_file" FORS_DATA_AUDIENCE ume-data-api
if [ -f "$keycloak_env" ]; then
  rename_vars "$keycloak_env"
  set_default "$keycloak_env" FORS_KEYCLOAK_REALM ume
  set_default "$keycloak_env" FORS_KEYCLOAK_ADMIN_CLIENT_ID ume-admin
  set_default "$keycloak_env" FORS_KEYCLOAK_IMPORT_CLIENT_ID ume-realm-import
  sed -i -E 's/^(#[[:space:]]*)?FORS_GATEWAY_DATA_NETWORK=ume-gateway_data$/# FORS_GATEWAY_DATA_NETWORK=fors-gateway_data/' "$keycloak_env"
  # The import's copy of the admin client secret has a name without "ume" now (init-keycloak.sh writes it again).
  # shellcheck disable=SC1090
  secrets="$(. "$env_file"; echo "${FORS_SECRETS_DIR:-}")"
  if [ -n "$secrets" ] && [ -f "$secrets/keycloak-import/ume-admin-client-secret" ] && [ ! -e "$secrets/keycloak-import/admin-client-secret" ]; then
    cp -p "$secrets/keycloak-import/ume-admin-client-secret" "$secrets/keycloak-import/admin-client-secret"
  fi
fi

containers() { docker ps -q --filter "label=com.docker.compose.project=$1" "${@:2}"; }
postgres="$(containers "$old_project" --filter label=com.docker.compose.service=postgres)"
old_volume="${old_project}_postgres-data"; new_volume="${new_project}_postgres-data"
if [ -z "$postgres" ]; then
  docker volume inspect "$new_volume" >/dev/null 2>&1 \
    || die "Postgres of project $old_project is not running: start the old installation (or pass --old-project)"
  echo; echo "Project $old_project is not running and $new_volume exists: steps 2 to 5 were done already."
  echo "Next: ./deploy/init-deployment.sh, then start the release (docs/upgrade-guide.md)."
  exit 0
fi

if [ "$yes" != 1 ]; then
  echo
  read -r -p "Stop the gateway, admin API, Data API and Keycloak of project $old_project now? [y/N] " answer
  case "$answer" in y|Y|yes) ;; *) die "stopped; nothing else was changed";; esac
fi

# --- 2. Stop the services ----------------------------------------------------------------------------------------------
step "2. Stopping the services of $old_project and $old_keycloak (Postgres keeps running)"
others="$( (containers "$old_project"; containers "$old_keycloak") | grep -v "^${postgres}$" || true)"
# shellcheck disable=SC2086
[ -z "$others" ] || docker stop $others >/dev/null

# --- 3. Database roles -------------------------------------------------------------------------------------------------
step "3. Renaming the database roles"
docker exec -i "$postgres" sh -c 'PGPASSWORD="$(cat /run/postgres-secrets/bootstrap_password)" exec psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q' \
  < "$here/0.12-rename-roles.sql"

# --- 4. Old project ----------------------------------------------------------------------------------------------------
step "4. Removing the containers of $old_project and $old_keycloak (volumes stay)"
image="$(docker inspect -f '{{.Config.Image}}' "$postgres")"
docker compose -p "$old_keycloak" down >/dev/null 2>&1 || true
docker compose -p "$old_project" down

# --- 5. Database volume ------------------------------------------------------------------------------------------------
step "5. Copying $old_volume to $new_volume"
if docker volume inspect "$new_volume" >/dev/null 2>&1 \
   && [ -n "$(docker run --rm -v "$new_volume:/to:ro" --entrypoint ls "$image" -A /to)" ]; then
  echo "$new_volume exists and is not empty: left as it is"
else
  docker volume create "$new_volume" >/dev/null
  docker run --rm --user 0 -v "$old_volume:/from:ro" -v "$new_volume:/to" --entrypoint sh "$image" -c 'cp -a /from/. /to/'
  echo "copied; the old volume $old_volume is kept until you remove it"
fi

cat <<EOF

Done. Next (docs/upgrade-guide.md, "0.12.0"):
  1. ./deploy/init-deployment.sh          writes the connection strings with the new role names; keeps your settings
  2. start the release as usual (docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait)
     and, with the bundled Keycloak, ./deploy/keycloak/init-keycloak.sh and its compose up
  3. once everything runs: docker volume rm $old_volume
EOF
