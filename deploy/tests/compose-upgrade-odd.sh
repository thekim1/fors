#!/usr/bin/env bash
# Upgrades an odd Compose installation from 0.11.0 with deploy/upgrade/upgrade-to-0.12.sh (#143): the 0.11.0 release
# bundle runs under its own project name, on its own ports, with the database on a direct bind mount of a host
# directory, and this release is unpacked beside it. Checks that the plan changes nothing; that --apply finds the project
# and the mount by itself, renames the roles, copies the old bundle's certificates and secrets (the key pepper) but no
# data; that this release then starts under another project name on the same directory with a row written before the
# upgrade; and that a second --apply is a no-op.
#
#   deploy/tests/compose-upgrade-odd.sh       # uses the local images (fors/*:latest) or GATEWAY_IMAGE, ADMINAPI_IMAGE,
#                                             # MIGRATIONS_IMAGE and DATAAPI_IMAGE; downloads the 0.11.0 bundle
#   deploy/tests/compose-upgrade-odd.sh --layout default   # the bundle's own layout instead: projects ume-gateway and
#                                             # fors-gateway, the database in a named volume that is copied
#   deploy/tests/compose-upgrade-odd.sh --keep    # leaves the stack and the work directory for a look
#
# Needs Docker with Compose, curl, openssl and root or sudo (init-deployment.sh sets the files' owners). The work
# directory's path must be the same for the Docker daemon (a bind mount).
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
keep=0; work=""; layout=odd
while [ $# -gt 0 ]; do
  case "$1" in
    --keep) keep=1; shift;;
    --work-dir) work="$2"; shift 2;;
    --layout) layout="$2"; shift 2;;
    *) echo "unknown option: $1" >&2; exit 2;;
  esac
done
[ -n "$work" ] || work="$(mktemp -d)"
mkdir -p "$work"; work="$(cd "$work" && pwd)"
bundle_url=https://github.com/thekim1/fors/releases/download/v0.11.0/ume-llm-gateway-0.11.0.tar.gz
case "$layout" in
  odd) old_project=odd-ume; new_project=odd-fors;;
  default) old_project=ume-gateway; new_project=fors-gateway;;
  *) echo "unknown layout: $layout" >&2; exit 2;;
esac
data="$work/pgdata"
as_root() { if [ "$(id -u)" = 0 ]; then "$@"; else sudo "$@"; fi; }
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }

old=(docker compose -p "$old_project" -f "$work/old/deploy/compose.prod.yaml" -f "$work/odd.yaml" --env-file "$work/old/deploy/.env")
new=(env GATEWAY_IMAGE="${GATEWAY_IMAGE:-fors/gateway:latest}" ADMINAPI_IMAGE="${ADMINAPI_IMAGE:-fors/adminapi:latest}"
  MIGRATIONS_IMAGE="${MIGRATIONS_IMAGE:-fors/migrations:latest}" DATAAPI_IMAGE="${DATAAPI_IMAGE:-fors/dataapi:latest}"
  docker compose -p "$new_project" -f "$work/new/deploy/compose.prod.yaml" -f "$work/odd.yaml" --env-file "$work/new/deploy/.env")
unset GATEWAY_IMAGE ADMINAPI_IMAGE MIGRATIONS_IMAGE DATAAPI_IMAGE COMPOSE_PROJECT_NAME COMPOSE_FILE COMPOSE_PROFILES
# Never on a host that has these projects: the cleanup removes their volumes.
for p in "$old_project" "$new_project"; do
  if [ -n "$(docker ps -aq --filter "label=com.docker.compose.project=$p")$(docker volume ls -q --filter "label=com.docker.compose.project=$p")" ]; then
    fail "project $p exists on this host; run the test where it does not"
  fi
done
cleanup() {
  [ "$keep" = 1 ] && { echo "kept: $work, projects $old_project and $new_project"; return; }
  "${new[@]}" down -v >/dev/null 2>&1 || true
  "${old[@]}" down -v >/dev/null 2>&1 || true
  cd / && as_root rm -r -f "$work"
}
trap cleanup EXIT

psql_in() { # psql_in CONTAINER SQL: one value, as the Postgres superuser
  docker exec "$1" sh -c 'PGPASSWORD="$(cat /run/postgres-secrets/bootstrap_password)" exec psql -U postgres -d postgres -tAq -c "$0"' "$2"
}
postgres_of() { docker ps -q --filter "label=com.docker.compose.project=$1" --filter label=com.docker.compose.service=postgres; }

# --- The 0.11.0 installation, the odd way -----------------------------------------------------------------------------
mkdir -p "$work/old"
curl -fsSL -o "$work/bundle.tar.gz" "$bundle_url"
bundle_sha="$(curl -fsSL "${bundle_url%/*}/SHA256SUMS" | awk '$2 ~ /ume-llm-gateway-0.11.0.tar.gz$/ {print $1}')"
[ -n "$bundle_sha" ] || fail "no checksum for the 0.11.0 bundle"
echo "$bundle_sha  $work/bundle.tar.gz" | sha256sum -c --quiet || fail "the 0.11.0 bundle's checksum"
tar -xzf "$work/bundle.tar.gz" -C "$work/old" --strip-components 1
( cd "$work/old" && ./deploy/init-deployment.sh --domain upgrade.test --yes >/dev/null )
printf 'UME_GATEWAY_PORT=28843\nUME_ADMIN_PORT=29843\n' >> "$work/old/deploy/.env"
if [ "$layout" = odd ]; then
  mkdir -p "$data" && as_root chown 70:70 "$data" && as_root chmod 700 "$data" # Postgres runs as 70:70
  printf 'services:\n  postgres:\n    volumes:\n      - %s:/var/lib/postgresql/data\n' "$data" > "$work/odd.yaml"
else
  printf 'services: {}\n' > "$work/odd.yaml"
fi
"${old[@]}" up -d --wait --wait-timeout 300 >/dev/null
pg="$(postgres_of "$old_project")"
psql_in "$pg" "CREATE TABLE upgrade_marker (v text); INSERT INTO upgrade_marker VALUES ('kept')" >/dev/null
[ "$(psql_in "$pg" "SELECT count(*) FROM pg_roles WHERE rolname LIKE 'ume\\_%'")" -gt 0 ] || fail "0.11.0 has no ume_* roles"
pepper="$(as_root sha256sum "$work/old/deploy/secrets/.pepper" | cut -d' ' -f1)"
ok "0.11.0 runs as project $old_project ($layout layout)"

# --- This release's bundle, with the old env file -----------------------------------------------------------------------
mkdir -p "$work/new"
cp -R "$repo/deploy" "$work/new/"
cp -p "$work/old/deploy/.env" "$work/new/deploy/.env"
upgrade=("$work/new/deploy/upgrade/upgrade-to-0.12.sh")
[ "$layout" = default ] || upgrade+=(--project "$new_project")

plan="$("${upgrade[@]}" 2>&1)" || fail "the plan failed: $plan"
grep -q "Old installation: project $old_project" <<< "$plan" || fail "the plan did not find $old_project: $plan"
if [ "$layout" = odd ]; then
  grep -q "database: bind $data" <<< "$plan" || fail "the plan did not find the bind mount: $plan"
  grep -q 'nothing is copied' <<< "$plan" || fail "the plan copies the database: $plan"
else
  grep -q "database: volume ${old_project}_postgres-data" <<< "$plan" || fail "the plan did not find the volume: $plan"
  ! docker volume inspect "${new_project}_postgres-data" >/dev/null 2>&1 || fail "the plan created ${new_project}_postgres-data"
fi
grep -q '^UME_' "$work/new/deploy/.env" || fail "the plan changed the env file"
[ -n "$(docker ps -q --filter "label=com.docker.compose.project=$old_project" --filter label=com.docker.compose.service=gateway)" ] \
  || fail "the plan stopped the gateway"
ok "the plan names the project and the database and changes nothing"

"${upgrade[@]}" --apply --yes > "$work/apply.log" 2>&1 || { cat "$work/apply.log" >&2; fail "--apply failed"; }
[ -z "$(docker ps -aq --filter "label=com.docker.compose.project=$old_project")" ] || fail "the old project's containers are still there"
if [ "$layout" = odd ]; then
  ! docker volume inspect "${new_project}_postgres-data" >/dev/null 2>&1 || fail "--apply created a volume for a bind mount"
else
  docker volume inspect "${new_project}_postgres-data" >/dev/null 2>&1 || fail "--apply did not create ${new_project}_postgres-data"
fi
! grep -q '^UME_' "$work/new/deploy/.env" || fail "the env file still has UME_* variables"
[ "$(as_root sha256sum "$work/new/deploy/secrets/.pepper" | cut -d' ' -f1)" = "$pepper" ] || fail "the key pepper did not carry over"
ok "--apply renamed the variables, copied the old bundle's certificates and secrets, and removed $old_project"

# The rest as the upgrade guide says: init-deployment.sh, then up with the override that keeps the bind.
# As the user, as the guide says: the copied certs/<service> and secrets/<service> belong to the services' users, so the
# script runs itself again with sudo, and the env file is the user's again afterwards.
( cd "$work/new" && ./deploy/init-deployment.sh --yes > "$work/init.log" 2>&1 ) || { tail -20 "$work/init.log" >&2; fail "init-deployment.sh failed"; }
if [ "$(id -u)" != 0 ]; then
  grep -q 'running again with sudo' "$work/init.log" || fail "init-deployment.sh did not run itself with sudo"
  [ -O "$work/new/deploy/.env" ] || fail "the env file is not the user's after init-deployment.sh"
fi
"${new[@]}" up -d --wait --wait-timeout 300 >/dev/null || { "${new[@]}" logs --tail 50 >&2; fail "the new release did not start"; }
pg="$(postgres_of "$new_project")"
mounted="$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Type}} {{if eq .Type "bind"}}{{.Source}}{{else}}{{.Name}}{{end}}{{end}}{{end}}' "$pg")"
if [ "$layout" = odd ]; then want="bind $data"; else want="volume ${new_project}_postgres-data"; fi
[ "$mounted" = "$want" ] || fail "the new Postgres has $mounted, not $want"
[ "$(psql_in "$pg" "SELECT v FROM upgrade_marker")" = kept ] || fail "the row written before the upgrade is gone"
[ "$(psql_in "$pg" "SELECT count(*) FROM pg_roles WHERE rolname LIKE 'ume\\_%'")" = 0 ] || fail "ume_* roles are left"
[ "$(as_root sha256sum "$work/new/deploy/secrets/.pepper" | cut -d' ' -f1)" = "$pepper" ] || fail "init-deployment.sh replaced the key pepper"
ok "this release runs as $new_project on $want with the old row, the same key pepper and the roles renamed"

# A plain re-run in place, as the user: the same files, the same pepper, the env file still the user's.
( cd "$work/new" && ./deploy/init-deployment.sh --yes > "$work/init2.log" 2>&1 ) || { tail -20 "$work/init2.log" >&2; fail "a re-run of init-deployment.sh failed"; }
[ "$(as_root sha256sum "$work/new/deploy/secrets/.pepper" | cut -d' ' -f1)" = "$pepper" ] || fail "a re-run of init-deployment.sh replaced the key pepper"
[ "$(id -u)" = 0 ] || [ -O "$work/new/deploy/.env" ] || fail "the env file is not the user's after a re-run"
"${new[@]}" up -d --wait --wait-timeout 300 >/dev/null || fail "the release did not start after a re-run of init-deployment.sh"
ok "init-deployment.sh runs again as the user (through sudo) and keeps every secret"

again="$("${upgrade[@]}" --apply --yes 2>&1)" || fail "a second --apply failed: $again"
grep -q 'done already' <<< "$again" || fail "a second --apply did something: $again"
ok "a second --apply is a no-op"
echo "The Compose upgrade from 0.11.0 ($layout layout) passed."
