#!/usr/bin/env bash
# Upgrades a Docker Compose installation (deploy/compose.prod.yaml, set up with init-deployment.sh) from 0.11 or earlier
# to Fors 0.12, where every name from before the rename to Fors (ume) changes (docs/upgrade-guide.md, "0.12.0"). Run it
# from the new release's directory, on the Docker host, with the old installation still running. It reads the
# installation (deploy/upgrade/lib.sh, #143): the projects from their Compose labels, the database's mount from the
# Postgres container, so a custom project name, a volume backed by a host directory or a direct bind mount all work.
#
#   ./deploy/upgrade/upgrade-to-0.12.sh                  # prints the plan; changes nothing
#   ./deploy/upgrade/upgrade-to-0.12.sh --apply          # does it (asks once before it stops anything)
#   ./deploy/upgrade/upgrade-to-0.12.sh --apply --yes    # no questions
#
#   --old-project NAME        the old installation's project (default: the only project running a gateway and a Postgres
#                             other than the new one, else ume-gateway)
#   --project NAME            the new project (default: COMPOSE_PROJECT_NAME from the shell or the env file, else
#                             fors-gateway); the same name as the old one keeps the database where it is
#   --old-keycloak-project NAME
#                             the old bundled Keycloak's project (default: ume-keycloak when it exists)
#   --env-file FILE           the installation's env file (default: deploy/.env)
#
# What it does, in this order (each step is skipped when it was done already, so a re-run is safe):
#   1. the env file (and deploy/keycloak/.env): renames the variables UME_* to FORS_* (the old files are kept as
#      .env.pre-0.12) and writes the names an existing installation keeps: the OIDC client ume-admin and the Data API
#      audience ume-data-api when the file sets none (the old defaults), and for the bundled Keycloak its realm ume,
#      client ume-admin and import client ume-realm-import;
#   2. stops the old project's services except Postgres, and the old Keycloak project;
#   3. renames the database roles ume_* to fors_* (deploy/upgrade/0.12-rename-roles.sql, as the Postgres superuser);
#   4. removes the old project's containers and networks (its volumes and host directories stay);
#   5. the database for the new project, by where it is: a named volume is copied to <project>_postgres-data (the old
#      one is kept: remove it once the new version runs); a volume backed by a host directory gets a new volume on the
#      same directory, nothing copied; a direct bind mount stays where it is (your Compose override keeps mounting it);
#      with the same project name nothing moves. A missing or empty database is refused before anything changes.
# Step 1 also copies the old release's deploy/certs, deploy/secrets, deploy/integration-manifests and
# deploy/integration-secrets here when the env file names them in another directory (the key pepper and the Data
# Protection certificate must carry over). Then re-run ./deploy/init-deployment.sh (it writes the connection strings with
# the new role names) and start the release as usual. Back up the database and those directories first (docs/runbook.md).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deploy="$(cd "$here/.." && pwd)"
# shellcheck source=deploy/upgrade/lib.sh
. "$here/lib.sh"
old_project=""; new_project=""; old_keycloak=""
env_file="$deploy/.env"; keycloak_env="$deploy/keycloak/.env"; yes=0
data=/var/lib/postgresql/data

usage() { sed -n '2,35p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
step() { echo; echo "== $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --old-project) old_project="$2"; shift 2;;
    --project) new_project="$2"; shift 2;;
    --old-keycloak-project) old_keycloak="$2"; shift 2;;
    --env-file) env_file="$2"; shift 2;;
    --apply) UP_APPLY=1; shift;;
    --yes|-y) yes=1; shift;;
    -h|--help) usage;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done

command -v docker >/dev/null || up_die "docker is required"
[ -f "$env_file" ] || up_die "$env_file not found: copy the old installation's env file here first (or pass --env-file)"
[ -n "$new_project" ] || new_project="${COMPOSE_PROJECT_NAME:-$(up_env_value "$env_file" COMPOSE_PROJECT_NAME)}"
[ -n "$new_project" ] || new_project=fors-gateway

# The old project: given, else the only one other than the new with a gateway and a Postgres, else ume-gateway.
if [ -z "$old_project" ]; then
  found="$(comm -12 <(up_projects gateway) <(up_projects postgres) | grep -vxF "$new_project" || true)"
  case "$(grep -c . <<< "$found" || true)" in
    0) old_project=ume-gateway;;
    1) old_project="$found";;
    *) up_die "more than one installation runs a gateway ($(paste -sd' ' <<< "$found")): name the old one with --old-project";;
  esac
fi
if [ -z "$old_keycloak" ]; then
  old_keycloak=ume-keycloak
fi

postgres="$(up_container "$old_project" postgres)"
running=""; [ -z "$postgres" ] || running="$(docker inspect -f '{{.State.Running}}' "$postgres")"
new_volume="${new_project}_postgres-data"
if [ "$running" != true ]; then
  # Done already: the new project has its Postgres or its volume, or the env file has no UME_* left and nothing old runs.
  if [ "$old_project" = "$new_project" ] || [ -n "$(up_container "$new_project" postgres)" ] \
     || docker volume inspect "$new_volume" >/dev/null 2>&1 || ! grep -qE '^UME_' "$env_file"; then
    echo "Postgres of project $old_project is not running and the new project's database is in place: steps 2 to 5 were done already."
    echo "Next: ./deploy/init-deployment.sh, then start the release (docs/upgrade-guide.md)."
    exit 0
  fi
  up_die "Postgres of project $old_project is not running: start the old installation (or pass --old-project)"
fi

image="$(docker inspect -f '{{.Config.Image}}' "$postgres")"
mount="$(up_mount "$postgres" "$data")"
[ -n "$mount" ] || up_die "the Postgres container of $old_project has nothing mounted at $data: its data would be lost"
IFS='|' read -r kind old_volume path <<< "$mount"
up_has_data "$image" "$mount" || up_die "the database of $old_project ($kind ${old_volume:-$path}) is empty: refusing to upgrade it"

echo "Old installation: project $old_project, Postgres $(docker inspect -f '{{.Name}}' "$postgres" | sed 's,^/,,'), database: $kind ${old_volume:-$path}${old_volume:+ ($path)}"
echo "New installation: project $new_project"
[ "$UP_APPLY" = 1 ] || echo "This is the plan; nothing is changed without --apply."

# --- 1. Variables ------------------------------------------------------------------------------------------------------
# set_default FILE NAME VALUE: adds NAME=VALUE unless the file sets NAME (also an empty NAME=, which means unset on purpose).
set_default() {
  grep -qE "^$2=" "$1" && return 0
  if [ "$UP_APPLY" = 1 ]; then printf '%s=%s\n' "$2" "$3" >> "$1"; else echo "  would add $2=$3 to $1"; fi
}
rename_vars() {
  local f="$1"
  grep -qE '^[[:space:]]*#?[[:space:]]*UME_' "$f" || return 0
  if [ "$UP_APPLY" != 1 ]; then echo "  would rename the variables UME_* to FORS_* in $f (keeping $f.pre-0.12)"; return 0; fi
  [ -e "$f.pre-0.12" ] || cp -p "$f" "$f.pre-0.12"
  sed -i -E 's/(^|[^A-Za-z0-9_])UME_/\1FORS_/g' "$f"
  echo "renamed the variables in $f (the old file is $f.pre-0.12)"
}
# set_value FILE NAME: NAME after the rename (FORS_NAME, or UME_NAME while the plan has not renamed it).
set_value() { local v; v="$(up_env_value "$1" "FORS_$2")"; [ -n "$v" ] || v="$(up_env_value "$1" "UME_$2")"; echo "$v"; }

step "1. Variables"
rename_vars "$env_file"
# The old defaults, kept as deployment values: the client registered in the identity provider and the audience its
# Data API tokens carry. Remove a line to take the new default (fors-admin, fors-data-api) once the provider has it.
if [ -n "$(set_value "$env_file" OIDC_AUTHORITY)" ]; then
  set_default "$env_file" FORS_OIDC_CLIENT_ID ume-admin
fi
set_default "$env_file" FORS_DATA_AUDIENCE ume-data-api
if [ -f "$keycloak_env" ]; then
  rename_vars "$keycloak_env"
  set_default "$keycloak_env" FORS_KEYCLOAK_REALM ume
  set_default "$keycloak_env" FORS_KEYCLOAK_ADMIN_CLIENT_ID ume-admin
  set_default "$keycloak_env" FORS_KEYCLOAK_IMPORT_CLIENT_ID ume-realm-import
  up_do sed -i -E "s/^(#[[:space:]]*)?FORS_GATEWAY_DATA_NETWORK=${old_project}_data\$/# FORS_GATEWAY_DATA_NETWORK=${new_project}_data/" "$keycloak_env"
  # The import's copy of the admin client secret has a name without "ume" now (init-keycloak.sh writes it again).
  secrets="$(set_value "$env_file" SECRETS_DIR)"
  if [ -n "$secrets" ] && [ -f "$secrets/keycloak-import/ume-admin-client-secret" ] && [ ! -e "$secrets/keycloak-import/admin-client-secret" ]; then
    up_do cp -p "$secrets/keycloak-import/ume-admin-client-secret" "$secrets/keycloak-import/admin-client-secret"
  fi
fi

# The installation's files, when the env file names another release's deploy/ (the new release unpacked beside the old):
# the certificates, the secrets (the key pepper, the Data Protection certificate, the database passwords) and the
# integration manifests and secrets are copied here, as root with their owners, because init-deployment.sh writes its
# own directories. A directory elsewhere (--certs-dir, --secrets-dir) stays, and init-deployment.sh is told to keep it.
init_args=()
for entry in CERTS_DIR:certs:--certs-dir SECRETS_DIR:secrets:--secrets-dir INTEGRATION_MANIFESTS_DIR:integration-manifests: \
             INTEGRATION_SECRETS_DIR:integration-secrets:; do
  IFS=: read -r name dir flag <<< "$entry"
  from="$(set_value "$env_file" "$name")"; to="$deploy/$dir"
  if [ -z "$from" ] || [ "$from" = "$to" ] || [ ! -d "$from" ]; then continue; fi
  if [ "$(basename "$from")" != "$dir" ] || [ "$(basename "$(dirname "$from")")" != deploy ]; then
    if [ -n "$flag" ]; then init_args+=("$flag" "$from"); fi
    echo "  $from stays where it is"
    continue
  fi
  if [ -d "$to" ] && [ -n "$(ls -A "$to" 2>/dev/null || echo x)" ]; then
    echo "  $to is not empty: left as it is"
    continue
  fi
  up_do docker run --rm --user 0 --network none --mount "type=bind,src=$from,dst=/from,readonly" \
    --mount "type=bind,src=$deploy,dst=/to" --entrypoint sh "$image" -c "mkdir -p '/to/$dir' && cp -a /from/. '/to/$dir/'"
  echo "  copied $from to $to"
done

if [ "$UP_APPLY" = 1 ] && [ "$yes" != 1 ]; then
  echo
  read -r -p "Stop the gateway, admin API, Data API and Keycloak of project $old_project now? [y/N] " answer
  case "$answer" in y|Y|yes) ;; *) up_die "stopped after step 1; the services were not touched";; esac
fi

# --- 2. Stop the services ----------------------------------------------------------------------------------------------
step "2. Stopping the services of $old_project and $old_keycloak (Postgres keeps running)"
others="$( (docker ps -q --filter "label=com.docker.compose.project=$old_project"; docker ps -q --filter "label=com.docker.compose.project=$old_keycloak") \
  | grep -v "^${postgres:0:12}" || true)"
if [ -n "$others" ]; then
  # shellcheck disable=SC2086 # container ids
  up_do docker stop $others
else
  echo "  nothing else is running"
fi

# --- 3. Database roles -------------------------------------------------------------------------------------------------
step "3. Renaming the database roles"
rename_roles() {
  docker exec -i "$postgres" sh -c 'PGPASSWORD="$(cat /run/postgres-secrets/bootstrap_password)" exec psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q' \
    < "$here/0.12-rename-roles.sql"
}
if [ "$UP_APPLY" = 1 ]; then rename_roles; else echo "  would run deploy/upgrade/0.12-rename-roles.sql in $postgres as the Postgres superuser"; fi

# --- 4. Old project ----------------------------------------------------------------------------------------------------
step "4. Removing the containers of $old_project and $old_keycloak (volumes and host directories stay)"
if [ -n "$(docker ps -aq --filter "label=com.docker.compose.project=$old_keycloak")" ]; then
  up_do docker compose -p "$old_keycloak" down
fi
up_do docker compose -p "$old_project" down

# --- 5. Database -------------------------------------------------------------------------------------------------------
step "5. The database of $new_project"
remove_old=""; override=""
labels=(--label "com.docker.compose.project=$new_project" --label com.docker.compose.volume=postgres-data)
if [ "$old_project" = "$new_project" ]; then
  echo "  the same project: the database stays where it is ($kind ${old_volume:-$path})"
  [ "$kind" != bind ] || override="$path"
else
  case "$kind" in
    bind)
      echo "  a direct bind mount of $path: nothing is copied; your Compose override must mount it at $data in $new_project"
      override="$path";;
    bind-backed)
      if docker volume inspect "$new_volume" >/dev/null 2>&1; then
        device="$(docker volume inspect -f '{{index .Options "device"}}' "$new_volume")"
        [ "$device" = "$path" ] || up_die "$new_volume exists on ${device:-a Docker directory}, not on $path: remove it or pass another --project"
        echo "  $new_volume is already on $path"
      else
        up_do docker volume create "${labels[@]}" --driver local --opt type=none --opt o=bind --opt "device=$path" "$new_volume"
        echo "  $new_volume on the same directory $path: nothing is copied"
      fi;;
    volume)
      if docker volume inspect "$new_volume" >/dev/null 2>&1 && up_has_data "$image" "volume|$new_volume|"; then
        echo "  $new_volume exists and is not empty: left as it is"
      else
        docker volume inspect "$new_volume" >/dev/null 2>&1 || up_do docker volume create "${labels[@]}" "$new_volume"
        up_do docker run --rm --user 0 --network none --mount "type=volume,src=$old_volume,dst=/from,readonly" \
          --mount "type=volume,src=$new_volume,dst=/to" --entrypoint sh "$image" -c 'cp -a /from/. /to/'
        echo "  copied $old_volume to $new_volume; the old volume is kept until you remove it"
        remove_old="$old_volume"
      fi;;
  esac
fi

[ "$UP_APPLY" = 1 ] || { echo; echo "Nothing was changed. Run it again with --apply to do this."; exit 0; }
cat <<EOF

Done. Next (docs/upgrade-guide.md, "0.12.0"):
  1. ./deploy/init-deployment.sh${init_args[*]:+ ${init_args[*]}}
                                          writes the connection strings with the new role names; keeps your settings
  2. start the release as usual (docker compose -p $new_project -f deploy/compose.prod.yaml --env-file $env_file up -d --wait)
     and, with the bundled Keycloak, ./deploy/keycloak/init-keycloak.sh and its compose up
EOF
[ -z "$override" ] || echo "     with your Compose override that mounts $override at $data"
[ -z "$remove_old" ] || echo "  3. once everything runs: docker volume rm $remove_old"
