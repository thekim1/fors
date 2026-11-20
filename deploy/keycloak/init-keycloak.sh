#!/usr/bin/env bash
# Setup for the optional Keycloak (deploy/keycloak/compose.yaml, docs/keycloak.md). Run it after deploy/init-deployment.sh:
# it uses the gateway's internal CA, certificate and secret directories (from deploy/.env) and writes deploy/keycloak/.env.
# Safe to re-run: existing secrets are never overwritten.
#
#   ./deploy/keycloak/init-keycloak.sh --host auth.example.se
#   ./deploy/keycloak/init-keycloak.sh --renew-certs          re-issue Keycloak's internal certificate only
#
#   --host HOST         public hostname of Keycloak (the realm URL is https://HOST/realms/fors)
#   --port PORT         host port of Keycloak's HTTPS listener (default 11443); a reverse proxy forwards HOST to it
#   --bind ADDRESS      address that port listens on (default 127.0.0.1, or the gateway's FORS_ADMIN_BIND)
#   --admin-url URL     public URL of the gateway's admin UI (default from deploy/.env), for the fors-admin client
#   --no-chown          leave the files owned by you
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deploy="$(dirname "$here")"
gateway_env="$deploy/.env"; env_file="$here/.env"
host=""; port=""; bind=""; admin_url=""; renew=0; chown=1

usage() { sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --host) host="$2"; shift 2;;
    --port) port="$2"; shift 2;;
    --bind) bind="$2"; shift 2;;
    --admin-url) admin_url="$2"; shift 2;;
    --renew-certs) renew=1; shift;;
    --no-chown) chown=0; shift;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
command -v openssl >/dev/null || die "openssl is required"
[ -f "$gateway_env" ] || die "$gateway_env not found: run deploy/init-deployment.sh first"
# shellcheck disable=SC1090
. "$gateway_env"
certs="${FORS_CERTS_DIR:?FORS_CERTS_DIR missing in $gateway_env}"; secrets="${FORS_SECRETS_DIR:?FORS_SECRETS_DIR missing in $gateway_env}"
if [ -f "$env_file" ]; then
  # shellcheck disable=SC1090
  . "$env_file"
  # A re-run keeps what an earlier run chose unless an option says otherwise.
  host="${host:-${FORS_KEYCLOAK_HOST:-}}"; admin_url="${admin_url:-${FORS_ADMIN_PUBLIC_URL:-}}"
  port="${port:-${FORS_KEYCLOAK_PORT:-}}"; bind="${bind:-${FORS_KEYCLOAK_BIND:-}}"
fi
port="${port:-11443}"
[ -n "$host" ] || die "--host is required (the public hostname of Keycloak)"
[ -n "$bind" ] || bind="${FORS_ADMIN_BIND:-127.0.0.1}"
if [ -z "$admin_url" ]; then
  admin_url="https://${FORS_ADMIN_PUBLIC_HOST:?FORS_ADMIN_PUBLIC_HOST missing in $gateway_env}"
  # Without a proxy the admin UI has its own port (init-deployment.sh writes the hostname only).
  [ "${FORS_ADMIN_BIND:-127.0.0.1}" = 0.0.0.0 ] || admin_url="$admin_url:${FORS_ADMIN_PORT:-9443}"
fi
case "$admin_url" in https://*) ;; *) die "--admin-url must be an https:// URL";; esac

rand() { openssl rand -hex 32; }
new_file() { [ -e "$1" ] && return 1; mkdir -p "$(dirname "$1")"; return 0; }

# --- Certificate from the gateway's internal CA (names: keycloak for the import job, HOST for the proxy) ----------
crt="$certs/keycloak/keycloak.crt"
if [ "$renew" = 1 ] || [ ! -e "$crt" ]; then
  [ -r "$certs/ca.key" ] || die "$certs/ca.key is needed to issue Keycloak's certificate: copy it back from where you keep it offline"
  mkdir -p "$certs/keycloak"; tmp="$(mktemp -d)"
  openssl req -newkey rsa:3072 -nodes -keyout "$certs/keycloak/keycloak.key" -out "$tmp/leaf.csr" -subj "/CN=keycloak" >/dev/null 2>&1
  printf 'subjectAltName=DNS:keycloak,DNS:localhost,DNS:%s\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature,keyEncipherment\nsubjectKeyIdentifier=hash\nauthorityKeyIdentifier=keyid\n' \
    "$host" > "$tmp/leaf.ext"
  openssl x509 -req -in "$tmp/leaf.csr" -CA "$certs/ca.pem" -CAkey "$certs/ca.key" -CAcreateserial -CAserial "$tmp/ca.srl" \
    -out "$crt" -days 825 -extfile "$tmp/leaf.ext" >/dev/null 2>&1
  rm -rf "$tmp"
fi
cp "$certs/ca.pem" "$certs/keycloak/ca.pem"
# The import job is Java: it trusts the internal CA through a PKCS#12 trust store (no secret in it).
# OpenSSL 3.2+ writes it directly; older ones (Ubuntu 24.04 has 3.0) leave it to keytool in the pinned Keycloak image.
mkdir -p "$certs/keycloak-import"
store="$certs/keycloak-import/truststore.p12"
rm -f "$store"
if ! openssl pkcs12 -export -nokeys -in "$certs/ca.pem" -jdktrust anyExtendedKeyUsage -out "$store" -passout pass:changeit >/dev/null 2>&1; then
  rm -f "$store"
  image="$(sed -n 's/^FROM \(.*\)$/\1/p' "$here/Dockerfile")"
  command -v docker >/dev/null || die "need OpenSSL 3.2 or later, or Docker, to write the trust store"
  docker run --rm --user "$(id -u):$(id -g)" -v "$certs:/certs" --entrypoint keytool "$image" -importcert -noprompt -alias fors-internal-ca \
    -file /certs/ca.pem -keystore /certs/keycloak-import/truststore.p12 -storetype PKCS12 -storepass changeit >/dev/null \
    || die "could not write the trust store with keytool"
fi
chmod 644 "$store"

# --- Secrets -------------------------------------------------------------------------------------------------------
mkdir -p "$secrets/keycloak" "$secrets/keycloak-import/config" "$secrets/postgres"
new_file "$secrets/keycloak/db_password" && rand > "$secrets/keycloak/db_password"
new_file "$secrets/keycloak/admin_password" && rand > "$secrets/keycloak/admin_password"
new_file "$secrets/keycloak/import_client_secret" && rand > "$secrets/keycloak/import_client_secret"
# Postgres creates the role fors_keycloak with this password (postgres/init.sql or add-keycloak-database.sql).
cp "$secrets/keycloak/db_password" "$secrets/postgres/keycloak_password"
tr -d '\r\n' < "$secrets/keycloak/import_client_secret" > "$secrets/keycloak-import/config/keycloak.client-secret"
# The admin UI's confidential client: one secret, used by the admin API and set on the client by the realm file.
if [ ! -s "$secrets/adminapi/Oidc__ClientSecret" ]; then
  mkdir -p "$secrets/adminapi"; rand | tr -d '\n' > "$secrets/adminapi/Oidc__ClientSecret"
fi
tr -d '\r\n' < "$secrets/adminapi/Oidc__ClientSecret" > "$secrets/keycloak-import/admin-client-secret"

# --- Ownership: Keycloak runs as 1000, the import job as 65534, Postgres as 70 ----------------------------------------
chown_todo=()
own() { # own UID:GID PATH...
  [ "$chown" = 1 ] || return 0
  if [ "$(id -u)" = 0 ]; then chown -R "$1" "${@:2}"; return; fi
  if command -v sudo >/dev/null && sudo chown -R "$1" "${@:2}"; then return; fi
  chown_todo+=("chown -R $1 ${*:2}")
}
own 1000:0 "$certs/keycloak" "$secrets/keycloak"
own 65534:65534 "$secrets/keycloak-import"
own 70:70 "$secrets/postgres/keycloak_password"
own 1654:1654 "$secrets/adminapi/Oidc__ClientSecret"

# --- deploy/keycloak/.env ------------------------------------------------------------------------------------------
cat > "$env_file" <<EOF
# Settings for deploy/keycloak/compose.yaml (docs/keycloak.md). Secrets live in FORS_SECRETS_DIR.
FORS_CERTS_DIR=$certs
FORS_SECRETS_DIR=$secrets
FORS_KEYCLOAK_HOST=$host
FORS_KEYCLOAK_BIND=$bind
FORS_KEYCLOAK_PORT=$port
FORS_ADMIN_PUBLIC_URL=$admin_url
# The gateway project's internal network, where Postgres is (compose project fors-gateway).
# FORS_GATEWAY_DATA_NETWORK=fors-gateway_data
# The realm, the admin console's client and the import client (an installation from before Fors keeps ume, ume-admin and
# ume-realm-import; docs/upgrade-guide.md). Kept on a re-run.
${FORS_KEYCLOAK_REALM:+FORS_KEYCLOAK_REALM=$FORS_KEYCLOAK_REALM}${FORS_KEYCLOAK_REALM:-# FORS_KEYCLOAK_REALM=fors}
${FORS_KEYCLOAK_ADMIN_CLIENT_ID:+FORS_KEYCLOAK_ADMIN_CLIENT_ID=$FORS_KEYCLOAK_ADMIN_CLIENT_ID}${FORS_KEYCLOAK_ADMIN_CLIENT_ID:-# FORS_KEYCLOAK_ADMIN_CLIENT_ID=fors-admin}
${FORS_KEYCLOAK_IMPORT_CLIENT_ID:+FORS_KEYCLOAK_IMPORT_CLIENT_ID=$FORS_KEYCLOAK_IMPORT_CLIENT_ID}${FORS_KEYCLOAK_IMPORT_CLIENT_ID:-# FORS_KEYCLOAK_IMPORT_CLIENT_ID=fors-realm-import}
# Other systems' realm files (applied after realm/*.json) and the values of their \$(env:...) placeholders:
# FORS_KEYCLOAK_ADDITIONS_DIR=/srv/keycloak-additions
# FORS_KEYCLOAK_IMPORT_ENV=/srv/keycloak-additions/import.env
# FORS_KEYCLOAK_MEMORY=1536m
# FORS_KEYCLOAK_MAX_QUEUED_REQUESTS=1000
EOF

cat <<EOF

Done. Generated $env_file, Keycloak's certificate in $certs/keycloak and its secrets in $secrets/keycloak.
EOF
if [ ${#chown_todo[@]} -gt 0 ]; then
  echo "Run as root first (containers run as other users and must read their files):"
  for c in "${chown_todo[@]}"; do echo "  $c"; done
fi
cat <<EOF
Next steps (docs/keycloak.md):
  1. Existing installation: add the database once (new installations get it when Postgres is first created):
       docker compose -f deploy/compose.prod.yaml --env-file deploy/.env exec -T postgres \\
         psql -U postgres -v ON_ERROR_STOP=1 -f /dev/stdin < deploy/postgres/add-keycloak-database.sql
     (mount secrets/postgres/keycloak_password first: it is read from /run/postgres-secrets).
  2. docker compose -f deploy/keycloak/compose.yaml --env-file deploy/keycloak/.env up -d --build --wait
  3. Point the gateway at it: FORS_OIDC_AUTHORITY=https://$host/realms/${FORS_KEYCLOAK_REALM:-fors} in deploy/.env, then restart the admin API.
  4. Forward https://$host to $bind:$port in your reverse proxy (X-Forwarded-* headers, docs/keycloak.md).
  5. Sign in to https://$host/admin as ${FORS_KEYCLOAK_ADMIN_USER:-bootstrap-admin} (password in $secrets/keycloak/admin_password),
     create personal admin accounts and delete the bootstrap admin.
EOF
