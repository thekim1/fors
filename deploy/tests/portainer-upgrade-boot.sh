#!/usr/bin/env bash
# Upgrades a Portainer stack from 0.11.0 in place to this one, the way an operator does it: the stack runs the 0.11.0
# release's compose.portainer.yaml and images, then the variables UME_* become FORS_* and the stack is updated with this
# compose.portainer.yaml. Checks that the one-shot `roles` renames the database roles ume_* to fors_* without a manual
# step, that the migrations pass and the services are healthy; that a session still open under an old name is ended
# before its role is renamed; and that a further update changes nothing.
#
#   deploy/tests/portainer-upgrade-boot.sh   # uses the local images fors/*:latest, or GATEWAY_IMAGE, ADMINAPI_IMAGE and
#                                            # MIGRATIONS_IMAGE; needs Docker with Compose and curl, and downloads the
#                                            # 0.11.0 stack from the GitHub release (its images are public)
#   COMPOSE_OVERRIDE=subnets.yaml deploy/tests/portainer-upgrade-boot.sh   # an extra Compose file, e.g. fixed subnets
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"; project=fors-upgrade-boot
old_url=https://github.com/thekim1/fors/releases/download/v0.11.0/compose.portainer.yaml
old_sha=99731c8a32a30a998bf659f6b68e0bb1d12f146bfda02f9f528ebf0930ed0ee8
gateway_image="${GATEWAY_IMAGE:-fors/gateway:latest}"
adminapi_image="${ADMINAPI_IMAGE:-fors/adminapi:latest}"
migrations_image="${MIGRATIONS_IMAGE:-fors/migrations:latest}"
# The old stack reads the same image variables (with its own release as the default), and either stack reads the rest.
unset GATEWAY_IMAGE ADMINAPI_IMAGE MIGRATIONS_IMAGE DATAAPI_IMAGE COMPOSE_PROFILES
for v in $(compgen -e | grep -E '^(UME|FORS)_' || true); do unset "$v"; done
extra=(); [ -z "${COMPOSE_OVERRIDE:-}" ] || extra=(-f "$COMPOSE_OVERRIDE")
old=(docker compose -p "$project" -f "$work/old.yaml" "${extra[@]}")
new=(env GATEWAY_IMAGE="$gateway_image" ADMINAPI_IMAGE="$adminapi_image" MIGRATIONS_IMAGE="$migrations_image"
  FORS_DOMAIN=upgrade.test FORS_OIDC_AUTHORITY=https://idp.upgrade.test/realms/fors
  FORS_GATEWAY_BIND=127.0.0.1 FORS_ADMIN_BIND=127.0.0.1 FORS_GATEWAY_PORT=28443 FORS_ADMIN_PORT=29443
  docker compose -p "$project" -f "$repo/deploy/compose.portainer.yaml" "${extra[@]}")
cleanup() {
  "${new[@]}" down -v >/dev/null 2>&1 || true
  # Declared only in the 0.11 stack, so the new stack's down leaves it.
  docker volume rm -f "${project}_ume-config" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT
fail() { echo "error: $*" >&2; "${new[@]}" ps -a >&2 || true; "${new[@]}" logs --no-color --tail 20 roles migrations >&2 || true; exit 1; }
# psql as the superuser inside the postgres container; prints the rows unaligned.
sql() {
  docker exec "$("${new[@]}" ps -q postgres)" sh -c \
    'PGPASSWORD="$(cat /run/postgres-secrets/bootstrap_password)" exec psql -U postgres -d gatewaydb -v ON_ERROR_STOP=1 -tAc "$1"' sh "$1"
}
roles() { sql "SELECT string_agg(rolname, ' ' ORDER BY rolname) FROM pg_roles WHERE rolname ~ '^(ume|fors)_'"; }
# The output of the one-shot roles since its last start (docker logs keeps the earlier runs of a container).
roles_output() {
  local id; id="$("${new[@]}" ps -aq roles)"
  docker logs --since "$(docker inspect -f '{{.State.StartedAt}}' "$id")" "$id" 2>&1
}
update() { "${new[@]}" up -d --wait --wait-timeout 300 > "$work/up.log" 2>&1 || { cat "$work/up.log" >&2; fail "the update failed"; }; }

# --- 0.11.0 --------------------------------------------------------------------------------------------------------
curl -fsSL "$old_url" -o "$work/old.yaml"
echo "$old_sha  $work/old.yaml" | sha256sum -c --quiet - || fail "the 0.11.0 stack is not the released file"
UME_DOMAIN=upgrade.test UME_OIDC_AUTHORITY=https://idp.upgrade.test/realms/fors UME_GATEWAY_BIND=127.0.0.1 \
  UME_ADMIN_BIND=127.0.0.1 UME_GATEWAY_PORT=28443 UME_ADMIN_PORT=29443 \
  "${old[@]}" up -d --wait --wait-timeout 300 > "$work/up.log" 2>&1 || { cat "$work/up.log" >&2; fail "0.11.0 did not start"; }
[ "$(roles)" = "ume_admin ume_data ume_gateway ume_migrator" ] || fail "0.11.0 has the roles: $(roles)"
echo "ok: 0.11.0 runs with the roles ume_*"

# --- The update: new variables, new file, nothing else ----------------------------------------------------------------
update
[ "$(roles)" = "fors_admin fors_data fors_gateway fors_migrator" ] || fail "after the update the roles are: $(roles)"
for r in migrator gateway admin data; do
  grep -q "renamed role ume_$r to fors_$r" <<< "$(roles_output)" || fail "roles did not report ume_$r"
done
[ "$(sql "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%_ForsRename'")" = 1 ] \
  || fail "the migration ForsRename was not applied"
for s in gateway adminapi; do
  [ "$(docker inspect -f '{{.State.Health.Status}}' "$("${new[@]}" ps -q "$s")")" = healthy ] || fail "$s is not healthy"
done
[ -z "$(sql "SELECT usename FROM pg_stat_activity WHERE usename LIKE 'ume\_%'")" ] || fail "a session under an old name"
echo "ok: the update renamed the roles, the migrations passed, gateway and adminapi are healthy"

# --- A session still open under an old name is ended, and its role renamed -------------------------------------------
sql 'ALTER ROLE fors_data RENAME TO ume_data' > /dev/null
docker exec -d "$("${new[@]}" ps -q postgres)" sh -c \
  'PGPASSWORD="$(cat /run/postgres-secrets/data_password)" psql -U ume_data -d gatewaydb -c "SELECT pg_sleep(300)"'
for _ in $(seq 20); do [ "$(sql "SELECT count(*) FROM pg_stat_activity WHERE usename = 'ume_data'")" = 1 ] && break; sleep 0.5; done
[ "$(sql "SELECT count(*) FROM pg_stat_activity WHERE usename = 'ume_data'")" = 1 ] || fail "no session as ume_data"
update
grep -q 'renamed role ume_data to fors_data' <<< "$(roles_output)" || fail "roles did not rename ume_data"
[ "$(sql "SELECT count(*) FROM pg_stat_activity WHERE usename IN ('ume_data', 'fors_data')")" = 0 ] \
  || fail "the session as ume_data was not ended"
echo "ok: a session under an old name is ended before its role is renamed"

# --- A further update changes nothing --------------------------------------------------------------------------------
update
[ -z "$(roles_output)" ] || fail "roles did something on a renamed database: $(roles_output)"
! grep -q Recreate "$work/up.log" || fail "a further update recreated containers: $(grep Recreate "$work/up.log")"
[ "$(roles)" = "fors_admin fors_data fors_gateway fors_migrator" ] || fail "after a further update the roles are: $(roles)"
echo "ok: a further update is a no-op"
