#!/usr/bin/env bash
# First-run setup for deploy/compose.prod.yaml: generates an internal CA, service
# certificates, the Data Protection certificate, database/Redis passwords, the key
# pepper, the admin API's setup secret and deploy/.env. Safe to re-run: existing secrets are never overwritten
# (regenerating the pepper or Data Protection certificate would break existing data). A re-run keeps the sign-in
# values and FORS_SETUP_REOPEN from deploy/.env.
#
#   ./deploy/init-deployment.sh                      # prompts for what it needs
#   ./deploy/init-deployment.sh --gateway-host llm.example.se --admin-host llm-admin.example.se --yes
#   ./deploy/init-deployment.sh --domain example.se --yes
#                                                    # the same two names, from the domain (each option overrides its own)
#   ./deploy/init-deployment.sh --oidc-authority https://idp.example.se/realms/fors --domain example.se --yes
#                                                    # sign-in declared here instead of in the setup wizard
#   ./deploy/init-deployment.sh --renew-certs        # re-issue internal certificates only
#   ./deploy/init-deployment.sh --renew-ca           # new internal CA, then re-issue its certificates
#
# Sign-in to the admin console (docs/identity-and-roles.md, "Setup mode"): without --oidc-authority it is set in the
# setup wizard after the first up (the setup code is in the admin API's log, the wizard at https://<admin host>/setup).
#   --oidc-authority URL    declare the sign-in instead: the https URL of the OIDC provider. The wizard then never
#                           opens. Optional; an interactive first run asks once (leave it empty for the wizard). A re-run
#                           without it keeps the value in deploy/.env (empty the line there to hand sign-in to the wizard)
#   --oidc-client-id ID     the client as a deployment value (unset: the wizard's, else fors-admin); kept like the authority
#   --oidc-client-secret-file FILE
#                           confidential client: install the secret for the admin API (needs the authority; omit it for a
#                           public client using PKCE only, or to enter the secret in the wizard)
#
# Exposure options (see docs/runbook.md, "Reverse proxy and PKI"):
#   --proxy                 a reverse proxy (nginx, Nginx Proxy Manager, F5...) terminates public
#                           TLS on 443 and re-encrypts to this host; needs distinct hostnames
#   --upstream-host HOST    address the proxy uses to reach this Docker host (default: hostname)
#   --rolling               run two gateway and admin API slots (compose.rolling.yaml) for updates without
#                           downtime; needs a proxy in front (docs/zero-downtime-updates.md)
#   --no-chown              leave the files owned by you, for deploy/kubernetes/create-secrets.sh
#   --data-host HOST        hostname clients use for the optional Data API (default: gateway host); with
#                           --proxy a third distinct name, which enables the Data API in proxy/nginx.conf
#   --gateway-cert/--gateway-key/--admin-cert/--admin-key/--ca-file FILE
#                           use certificates from your PKI for the public listeners instead of
#                           the generated internal CA (ca-file = your root/intermediate chain)
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
certs="$here/certs"; secrets="$here/secrets"; env_file="$here/.env"
oidc_authority=""; oidc_client=""; gateway_host=""; admin_host=""; data_host=""; domain=""
gateway_port=8443; admin_port=9443; bind=""; yes=0; renew=0; renew_ca=0; rolling=0; chown=1
oidc_secret_file=""; proxy=0; upstream_host=""; gw_cert=""; gw_key=""; adm_cert=""; adm_key=""; ca_file=""

usage() { sed -n '2,38p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
args=("$@")
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --oidc-authority) oidc_authority="$2"; shift 2;;
    --oidc-client-id) oidc_client="$2"; shift 2;;
    --domain) domain="$2"; shift 2;;
    --gateway-host) gateway_host="$2"; shift 2;;
    --admin-host) admin_host="$2"; shift 2;;
    --data-host) data_host="$2"; shift 2;;
    --gateway-port) gateway_port="$2"; shift 2;;
    --admin-port) admin_port="$2"; shift 2;;
    --bind) bind="$2"; shift 2;;
    --certs-dir) certs="$2"; shift 2;;
    --secrets-dir) secrets="$2"; shift 2;;
    --oidc-client-secret-file) oidc_secret_file="$2"; shift 2;;
    --proxy) proxy=1; shift;;
    --upstream-host) upstream_host="$2"; shift 2;;
    --rolling) rolling=1; shift;;
    --no-chown) chown=0; shift;;
    --gateway-cert) gw_cert="$2"; shift 2;;
    --gateway-key) gw_key="$2"; shift 2;;
    --admin-cert) adm_cert="$2"; shift 2;;
    --admin-key) adm_key="$2"; shift 2;;
    --ca-file) ca_file="$2"; shift 2;;
    --renew-certs) renew=1; shift;;
    --renew-ca) renew=1; renew_ca=1; shift;;
    --yes|-y) yes=1; shift;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
command -v openssl >/dev/null || die "openssl is required"
# A re-run as yourself: the last run gave certs/<service> and secrets/<service> to the services' users (mode 700), so
# as you this script would see none of their files and try to make new ones. It runs itself again with sudo then, and
# gives the files that are yours (the env file, its backup, the integration manifests) back to you at the end.
if [ "$(id -u)" != 0 ]; then
  for d in "$certs"/{postgres,redis,gateway,adminapi,dataapi,migrations} \
           "$secrets"/{postgres,redis,gateway,adminapi,dataapi,migrations} "$here/integration-secrets"; do
    if [ -d "$d" ] && { [ ! -r "$d" ] || [ ! -x "$d" ]; }; then
      command -v sudo >/dev/null || die "$d belongs to its service's user: run this script as root"
      echo "The files of the last run belong to the services' users: running again with sudo."
      exec sudo "${BASH_SOURCE[0]}" "${args[@]}"
    fi
  done
fi
env_value() { sed -n "s/^$1=//p" "$env_file" | tail -n 1; } # env_value NAME — its last value in deploy/.env
# A re-run without --oidc-authority or --oidc-client-id keeps the value in deploy/.env: a declared sign-in does not move to
# the setup wizard because an option was left out. The deployer's FORS_SETUP_REOPEN is kept too (set by hand, never here).
setup_reopen=""; first_run=1
if [ -f "$env_file" ]; then
  first_run=0
  [ -n "$oidc_authority" ] || oidc_authority="$(env_value FORS_OIDC_AUTHORITY)"
  [ -n "$oidc_client" ] || oidc_client="$(env_value FORS_OIDC_CLIENT_ID)"
  setup_reopen="$(env_value FORS_SETUP_REOPEN)"
fi
# --domain names both hosts (llm.<domain>, llm-admin.<domain>), as FORS_DOMAIN does in compose.portainer.yaml.
if [ -n "$domain" ]; then
  gateway_host="${gateway_host:-llm.$domain}"; admin_host="${admin_host:-llm-admin.$domain}"
fi

ask() { # ask VAR "prompt" [default]
  local var="$1" prompt="$2" default="${3:-}" reply
  [ -n "${!var}" ] && return
  if [ "$yes" = 1 ] || [ ! -t 0 ]; then
    [ -n "$default" ] || die "$prompt is required (pass the matching option)"
    printf -v "$var" '%s' "$default"; return
  fi
  read -r -p "$prompt${default:+ [$default]}: " reply
  reply="${reply:-$default}"; [ -n "$reply" ] || die "$prompt is required"
  printf -v "$var" '%s' "$reply"
}

if [ "$renew" = 0 ] || [ ! -f "$env_file" ]; then
  echo "Fors production setup"
  if [ "$first_run" = 1 ] && [ -z "$oidc_authority" ] && [ "$yes" = 0 ] && [ -t 0 ]; then
    read -r -p "OIDC authority URL (https://...; leave empty to set sign-in in the browser): " oidc_authority
  fi
  ask gateway_host "Public hostname for the gateway API" "$(hostname -f 2>/dev/null || hostname)"
  if [ "$proxy" = 0 ] && [ "$yes" = 0 ] && [ -t 0 ]; then
    read -r -p "Is a reverse proxy (nginx, Nginx Proxy Manager, F5...) in front terminating TLS? [y/N]: " reply
    case "$reply" in [yY]*) proxy=1;; esac
  fi
  if [ "$proxy" = 1 ]; then
    ask admin_host "Public hostname for the admin UI (must differ from the gateway's)"
    [ "$admin_host" != "$gateway_host" ] || die "proxy mode needs two distinct hostnames (the proxy routes by name)"
    [ -z "$data_host" ] || { [ "$data_host" != "$gateway_host" ] && [ "$data_host" != "$admin_host" ]; } \
      || die "proxy mode needs a --data-host distinct from the gateway and admin hosts (the proxy routes by name)"
    ask upstream_host "Address the proxy uses to reach this host" "$(hostname -f 2>/dev/null || hostname)"
  else
    ask admin_host "Public hostname for the admin UI" "$gateway_host"
  fi
fi
case "$oidc_authority" in ""|https://*) ;; *) die "the OIDC authority must be an https:// URL: $oidc_authority";; esac
[ -z "$oidc_secret_file" ] || [ -r "$oidc_secret_file" ] || die "cannot read --oidc-client-secret-file $oidc_secret_file"
# A deployment client secret goes only to the deployment's own provider: without the authority it would make every test
# sign-in of the setup wizard fail (deployment_secret_without_authority).
[ -z "$oidc_secret_file" ] || [ -n "$oidc_authority" ] || die "--oidc-client-secret-file needs --oidc-authority \
(without an authority, enter the client secret in the setup wizard instead)"
pki=0
if [ -n "$gw_cert$gw_key$adm_cert$adm_key" ]; then
  pki=1
  for f in "$gw_cert" "$gw_key" "$adm_cert" "$adm_key"; do
    [ -n "$f" ] && [ -r "$f" ] || die "PKI mode needs readable --gateway-cert, --gateway-key, --admin-cert and --admin-key"
  done
  [ -z "$ca_file" ] || [ -r "$ca_file" ] || die "cannot read --ca-file $ca_file"
fi
if [ -z "$bind" ]; then bind="127.0.0.1"; [ "$proxy" = 1 ] && bind="0.0.0.0"; fi
if [ "$proxy" = 1 ]; then public_gw=""; public_admin=""; else public_gw=":$gateway_port"; public_admin=":$admin_port"; fi

new_file() { # new_file PATH — true (and creates parent) if PATH does not exist yet
  [ -e "$1" ] && return 1
  mkdir -p "$(dirname "$1")"; return 0
}
rand() { openssl rand -hex 32; }

mkdir -p "$certs" "$secrets"

# --- Internal CA (trusted only by the services themselves) ----------------------
# keyUsage makes it RFC 5280-conformant; strict clients (Python 3.13, newer Go and Java) refuse a CA without it.
# --renew-ca replaces a CA made by an earlier version, which lacks it; the old one is kept as ca.pem.old.
if [ "$renew_ca" = 1 ] && [ -e "$certs/ca.pem" ]; then
  mv "$certs/ca.pem" "$certs/ca.pem.old"; rm -f "$certs/ca.key"
fi
if new_file "$certs/ca.pem"; then
  openssl req -x509 -newkey rsa:3072 -nodes -keyout "$certs/ca.key" -out "$certs/ca.pem" \
    -days 3650 -subj "/CN=fors-gateway-internal-ca" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" >/dev/null 2>&1
fi

# --- Service certificates ---------------------------------------------------------
issue() { # issue SERVICE SAN...
  local svc="$1"; shift; local san="DNS:$svc,DNS:localhost" h tmp
  [ -r "$certs/ca.key" ] || die "$certs/ca.key is needed to issue certificates: copy it back from where you keep it offline"
  for h in "$@"; do [ -n "$h" ] && san="$san,DNS:$h"; done
  mkdir -p "$certs/$svc"; tmp="$(mktemp -d)"
  openssl req -newkey rsa:3072 -nodes -keyout "$certs/$svc/$svc.key" -out "$tmp/leaf.csr" -subj "/CN=$svc" >/dev/null 2>&1
  printf 'subjectAltName=%s\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature,keyEncipherment\nsubjectKeyIdentifier=hash\nauthorityKeyIdentifier=keyid\n' \
    "$san" > "$tmp/leaf.ext"
  openssl x509 -req -in "$tmp/leaf.csr" -CA "$certs/ca.pem" -CAkey "$certs/ca.key" -CAcreateserial \
    -CAserial "$tmp/ca.srl" -out "$certs/$svc/$svc.crt" -days 825 -extfile "$tmp/leaf.ext" >/dev/null 2>&1
  rm -rf "$tmp"; : > "$certs/$svc/.internal"
}
if [ "$renew" = 1 ] && [ -f "$env_file" ]; then
  # shellcheck disable=SC1090
  . "$env_file"
  gateway_host="${FORS_GATEWAY_PUBLIC_URL#https://}"; gateway_host="${gateway_host%%[:/]*}"
  admin_host="${FORS_ADMIN_PUBLIC_HOST:-$gateway_host}"
  data_host="${FORS_DATA_HOST:-}"
fi
[ -n "$data_host" ] || data_host="$gateway_host"
for svc in postgres redis; do
  if [ "$renew" = 1 ] || new_file "$certs/$svc/$svc.crt"; then issue "$svc"; fi
done
use_pki() { # use_pki SERVICE CERT KEY — install operator-supplied PKI files
  mkdir -p "$certs/$1"; cp "$2" "$certs/$1/$1.crt"; cp "$3" "$certs/$1/$1.key"; rm -f "$certs/$1/.internal"
}
if [ "$pki" = 1 ]; then
  use_pki gateway "$gw_cert" "$gw_key"; use_pki adminapi "$adm_cert" "$adm_key"
else
  # On renewal only re-issue what this script issued (PKI-supplied certificates have no marker).
  if { [ "$renew" = 1 ] && [ -e "$certs/gateway/.internal" ]; } || new_file "$certs/gateway/gateway.crt"; then issue gateway "$gateway_host"; fi
  if { [ "$renew" = 1 ] && [ -e "$certs/adminapi/.internal" ]; } || new_file "$certs/adminapi/adminapi.crt"; then issue adminapi "$admin_host"; fi
fi
# The Data API always gets an internal certificate; put it behind your proxy or API gateway for a PKI one.
if [ "$renew" = 1 ] || new_file "$certs/dataapi/dataapi.crt"; then issue dataapi "$data_host"; fi

# --- Data Protection certificate (stable: never regenerate once data exists) --------
dp="$certs/data-protection.pfx"
if new_file "$dp"; then
  tmp="$(mktemp -d)"
  openssl req -x509 -newkey rsa:3072 -nodes -keyout "$tmp/dp.key" -out "$tmp/dp.crt" -days 3650 \
    -subj "/CN=fors-gateway-data-protection" >/dev/null 2>&1
  openssl pkcs12 -export -inkey "$tmp/dp.key" -in "$tmp/dp.crt" -out "$dp" -passout pass: >/dev/null 2>&1
  rm -rf "$tmp"
fi
for svc in gateway adminapi migrations; do
  mkdir -p "$certs/$svc"
  cp "$dp" "$certs/$svc/data-protection.pfx"
  cp "$certs/ca.pem" "$certs/$svc/ca.pem"
done
for svc in postgres redis dataapi; do cp "$certs/ca.pem" "$certs/$svc/ca.pem"; done
# Services trust the internal CA plus, when given, your PKI chain (their only trust store).
if [ -n "$ca_file" ]; then
  for svc in gateway adminapi dataapi; do cat "$ca_file" >> "$certs/$svc/ca.pem"; done
fi

# --- Secrets ----------------------------------------------------------------------
mkdir -p "$secrets"/{postgres,redis,gateway,adminapi,dataapi,migrations}
for role in bootstrap migrator gateway admin data; do
  new_file "$secrets/postgres/${role}_password" && rand > "$secrets/postgres/${role}_password"
done
new_file "$secrets/redis/redis_password" && rand > "$secrets/redis/redis_password"
# Derived from the password and the template, so rewritten on every run: an upgrade that allows the gateway a new Redis
# command reaches existing deployments when this script is run again.
hash="$(tr -d '\n' < "$secrets/redis/redis_password" | openssl dgst -sha256 | awk '{print $NF}')"
sed "s/REPLACE_WITH_SHA256_PASSWORD_HASH/$hash/" "$here/redis/users.acl.example" > "$secrets/redis/users.acl"
pepper_src="$secrets/.pepper"
new_file "$pepper_src" && openssl rand -hex 48 > "$pepper_src"
for svc in gateway adminapi migrations; do
  role="$svc"; case "$svc" in adminapi) role=admin;; migrations) role=migrator;; esac
  printf '%s' "$(cat "$pepper_src")" > "$secrets/$svc/Security__KeyPepper"
  printf 'Host=postgres;Database=gatewaydb;Username=fors_%s;Password=%s;SSL Mode=VerifyFull;Root Certificate=/run/certs/ca.pem;GSS Encryption Mode=Disable' \
    "$role" "$(cat "$secrets/postgres/${role}_password")" > "$secrets/$svc/ConnectionStrings__gatewaydb"
  if [ "$svc" != migrations ]; then
    printf 'redis:6379,user=gateway,password=%s,ssl=true,sslHost=redis,abortConnect=false' \
      "$(cat "$secrets/redis/redis_password")" > "$secrets/$svc/ConnectionStrings__redis"
  fi
done

# Data API (optional, profile "data"): read-only role, no pepper, no Redis.
printf 'Host=postgres;Database=gatewaydb;Username=fors_data;Password=%s;SSL Mode=VerifyFull;Root Certificate=/run/certs/ca.pem;GSS Encryption Mode=Disable' \
  "$(cat "$secrets/postgres/data_password")" > "$secrets/dataapi/ConnectionStrings__gatewaydb"

if [ -n "$oidc_secret_file" ]; then
  tr -d '\r\n' < "$oidc_secret_file" > "$secrets/adminapi/Oidc__ClientSecret"
fi
# The admin API's setup secret: the setup code is derived from it (docs/identity-and-roles.md, "Setup mode"). Kept, but
# not worth backing up: a new one only changes the setup codes printed from then on.
new_file "$secrets/adminapi/Setup__Secret" && rand | tr -d '\n' > "$secrets/adminapi/Setup__Secret"

# --- Ownership: container UIDs must be able to read their own files -------------------
own() { # own UID DIR... (needs root; falls back to a printed command)
  local id="$1"; shift
  [ "$chown" = 1 ] || return 0
  if [ "$(id -u)" = 0 ]; then chown -R "$id:$id" "$@"; return; fi
  if command -v sudo >/dev/null && sudo chown -R "$id:$id" "$@"; then return; fi
  chown_todo+=("chown -R $id:$id $*")
}
chown_todo=()
own 70 "$certs/postgres" "$secrets/postgres"
own 999 "$certs/redis" "$secrets/redis"
own 1654 "$certs/gateway" "$certs/adminapi" "$certs/dataapi" "$certs/migrations" \
  "$secrets/gateway" "$secrets/adminapi" "$secrets/dataapi" "$secrets/migrations"
chmod 755 "$certs" "$secrets" 2>/dev/null || true
# Integration manifests and the secrets of the credential keys they create (docs/integrations.md); empty provisions nothing.
manifests="$here/integration-manifests"; integration_secrets="$here/integration-secrets"
mkdir -p "$manifests" "$integration_secrets"
# Manifests hold no secrets, and the migration service (uid 1654) must list them despite this script's umask.
chmod 755 "$manifests"
chmod 700 "$integration_secrets"
own 1654 "$integration_secrets"

# --- deploy/.env (non-secret settings only) ------------------------------------------
if [ "$renew" = 0 ] || [ ! -f "$env_file" ]; then
  if [ -e "$env_file" ]; then
    mv "$env_file" "$env_file.bak"; echo "Existing .env saved as $(basename "$env_file").bak"
  fi
  ops_url=""
  # PKI certificates rarely cover the internal name "gateway", so the admin API reaches
  # the gateway's operations endpoint by its public name instead.
  [ "$pki" = 1 ] && ops_url="FORS_GATEWAY_OPERATIONS_URL=https://$gateway_host$public_gw"
  admin_url="https://$admin_host$public_admin"
  cat > "$env_file" <<EOF
# Non-secret settings for deploy/compose.prod.yaml. Secrets live in FORS_SECRETS_DIR.
FORS_CERTS_DIR=$certs
FORS_SECRETS_DIR=$secrets
# Sign-in (docs/identity-and-roles.md, "Setup mode"): empty is unset, so the setup wizard's value applies, else the
# code's default (client fors-admin). A set authority declares the sign-in and keeps the wizard closed; every FORS_OIDC_*
# set here is the deployment's value and locks its field in the wizard. A re-run of init-deployment.sh keeps both lines.
FORS_OIDC_AUTHORITY=$oidc_authority
FORS_OIDC_CLIENT_ID=$oidc_client
# Empty. Any new value without spaces (for example today's date), then \`docker compose ... up -d\`, opens the setup
# wizard once more, for an hour, with a new setup code in the admin API's log; empty it before restoring a database.
FORS_SETUP_REOPEN=$setup_reopen
FORS_GATEWAY_PUBLIC_URL=https://$gateway_host$public_gw
FORS_ADMIN_PUBLIC_HOST=$admin_host
# The admin console's address, handed to integrations (/v1/fors/context) for links to a key or budget.
FORS_ADMIN_PUBLIC_URL=$admin_url
$ops_url
# Listeners default to loopback (0.0.0.0 with --proxy: firewall them to the proxy only).
FORS_GATEWAY_BIND=$bind
FORS_GATEWAY_PORT=$gateway_port
FORS_ADMIN_BIND=$bind
FORS_ADMIN_PORT=$admin_port
# Second slots for updates without downtime (compose.rolling.yaml, docs/zero-downtime-updates.md).
FORS_GATEWAY_B_PORT=$((gateway_port + 1))
FORS_ADMIN_B_PORT=$((admin_port + 1))
# Optional read-only Data API (docs/data-access.md): uncomment to start it with the stack.
# COMPOSE_PROFILES=data
FORS_DATA_HOST=$data_host
FORS_DATA_BIND=$bind
FORS_DATA_PORT=10443
# FORS_DATA_AUTHORITY=   (defaults to FORS_OIDC_AUTHORITY, never to the setup wizard's; the issuer of the integrations'
#                       client-credentials tokens. Without either, the Data API accepts only Data API keys)
# FORS_DATA_AUDIENCE=fors-data-api
# Claims from an existing IdP (Keycloak, AD FS, Entra ID); see docs/runbook.md, "Identity provider". Unset, the setup
# wizard's value applies, else the default shown here (no extra scope; openid, profile and email are always requested).
# FORS_OIDC_ROLE_CLAIM=roles
# FORS_OIDC_DEPARTMENT_CLAIM=departmentCodes
# FORS_OIDC_TEAM_CLAIM=teamCodes   (or groups: team codes are then the group names)
# FORS_OIDC_GROUP_CLAIM=groups
# FORS_OIDC_EXTRA_SCOPE=groups
# Groups (names as in the token, separated by ';') that grant each role without IdP-side roles:
# FORS_OIDC_GROUPS_GATEWAY_ADMIN=GG-Llm-Admins
# FORS_OIDC_GROUPS_DEPARTMENT_ADMIN=
# FORS_OIDC_GROUPS_VIEWER=
# FORS_OIDC_GROUPS_TEAM_ADMIN=
# Integration API (/integration/v1): requests per minute per credential key (default 60).
# FORS_INTEGRATION_REQUESTS_PER_MINUTE=60
# Integration manifests to provision on every deploy, and where the new keys' secrets are written (docs/integrations.md).
FORS_INTEGRATION_MANIFESTS_DIR=$manifests
FORS_INTEGRATION_SECRETS_DIR=$integration_secrets
# The Integration API's address as integrations reach it, handed out on /v1/fors/context (empty leaves it out), e.g.
# FORS_INTEGRATION_API_URL=https://adminapi:8443/integration/v1
# Pin images by digest once pushed to your registry, for example:
# GATEWAY_IMAGE=registry.example.se/fors/gateway@sha256:...
# ADMINAPI_IMAGE=registry.example.se/fors/adminapi@sha256:...
# MIGRATIONS_IMAGE=registry.example.se/fors/migrations@sha256:...
# DATAAPI_IMAGE=registry.example.se/fors/dataapi@sha256:...
EOF
fi

proxy_note=""
if [ "$proxy" = 1 ]; then
  sed -e "s/@GATEWAY_HOST@/$gateway_host/g" -e "s/@ADMIN_HOST@/$admin_host/g" \
      -e "s/@UPSTREAM@/$upstream_host/g" -e "s/@GATEWAY_PORT@/$gateway_port/g" \
      -e "s/@ADMIN_PORT@/$admin_port/g" -e "s/@GATEWAY_B_PORT@/$((gateway_port + 1))/g" \
      -e "s/@ADMIN_B_PORT@/$((admin_port + 1))/g" -e "s/@DATA_HOST@/$data_host/g" -e "s/@DATA_PORT@/10443/g" \
      "$here/proxy/nginx.conf.example" > "$here/proxy/nginx.conf"
  [ "$rolling" = 1 ] && sed -i 's/#rolling //' "$here/proxy/nginx.conf"
  data_proxied=0
  if [ "$data_host" != "$gateway_host" ] && [ "$data_host" != "$admin_host" ]; then
    sed -i 's/^#data \{0,1\}//' "$here/proxy/nginx.conf"; data_proxied=1
  fi
  proxy_note="
Reverse proxy: $here/proxy/nginx.conf is ready for nginx; for Nginx Proxy Manager or F5 use
the same settings (see docs/runbook.md, 'Reverse proxy and PKI'). Both hosts forward to
HTTPS upstreams: $upstream_host:$gateway_port (gateway) and $upstream_host:$admin_port (admin UI),
preserving the Host header. Restrict those ports to the proxy with the host firewall."
  [ "$rolling" = 1 ] && proxy_note="$proxy_note
With --rolling, also list the second slots: $upstream_host:$((gateway_port + 1)) (gateway) and
$upstream_host:$((admin_port + 1)) (admin UI); proxy/nginx.conf already does."
  if [ "$data_proxied" = 1 ]; then proxy_note="$proxy_note
The optional Data API (COMPOSE_PROFILES=data) is proxied as $data_host to $upstream_host:10443."
  else proxy_note="$proxy_note
Port 10443 (the optional Data API) is bound to $bind as well. To proxy it, re-run with --data-host <third host name>,
or firewall it to the integrations that call it directly."
  fi
fi
up_cmd="docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait"
update_note=""
if [ "$rolling" = 1 ]; then
  up_cmd="docker compose -f deploy/compose.prod.yaml -f deploy/compose.rolling.yaml --env-file deploy/.env up -d --wait"
  update_note="
  7. Update later without downtime with ./deploy/rolling-update.sh (docs/zero-downtime-updates.md)."
  [ "$proxy" = 1 ] || update_note="$update_note
     The second slots listen on ports $((gateway_port + 1)) and $((admin_port + 1)): put a proxy in front that uses both."
fi
# Sign-in: declared by the authority, or set in the setup wizard after the first up.
logs_cmd="${up_cmd% up -d --wait} logs adminapi"
if [ -n "$oidc_authority" ]; then
  signin_step="  3. Sign-in is declared here (FORS_OIDC_AUTHORITY in deploy/.env), so the setup wizard stays closed:
     sign in at https://$admin_host$public_admin/."
else
  signin_step="  3. Set up sign-in in the setup wizard. Read the setup code (valid for an hour; a restart of the admin API
     prints it again, and while setup was never finished a restart after the hour opens a new hour with a new code):
       $logs_cmd | grep 'Setup code'
     and open https://$admin_host$public_admin/setup. The wizard shows the redirect URIs of step 1, takes the provider,
     client and secret, and tests a sign-in before saving; whoever tests needs the role gateway-admin."
  [ ! -s "$secrets/adminapi/Oidc__ClientSecret" ] || signin_step="$signin_step
     $secrets/adminapi/Oidc__ClientSecret is a deployment client secret without an authority: every test
     sign-in fails with deployment_secret_without_authority. Set FORS_OIDC_AUTHORITY in deploy/.env (or re-run with
     --oidc-authority), or delete the file and enter the secret in the wizard."
  signin_step="$signin_step
     The optional Data API takes access tokens only from FORS_DATA_AUTHORITY then (never the wizard's provider)."
fi
# Run with sudo (above, or by hand): the files that are not a service's go back to whoever ran sudo.
if [ "$(id -u)" = 0 ] && [ -n "${SUDO_UID:-}" ] && [ "$SUDO_UID" != 0 ]; then
  for f in "$env_file" "$env_file.bak" "$manifests"; do
    [ ! -e "$f" ] || chown -R "$SUDO_UID:${SUDO_GID:-$SUDO_UID}" "$f"
  done
fi
own_step="  0. File ownership is set."
[ "$chown" = 1 ] || own_step="  0. Files stay owned by you (--no-chown). For Kubernetes, run deploy/kubernetes/create-secrets.sh next."
if [ ${#chown_todo[@]} -gt 0 ]; then
  own_step="  0. Run as root first (containers run as other users and must read their files):"
  for c in "${chown_todo[@]}"; do own_step="$own_step
       $c"; done
fi

cat <<EOF

Done. Generated:
  $env_file
  $certs   (internal CA, service certificates, Data Protection certificate)
  $secrets (database/Redis passwords, key pepper, connection strings, setup secret)

Next steps:
$own_step
  1. Register these redirect URIs for client '${oidc_client:-fors-admin}' in your IdP (code + PKCE):
       https://$admin_host$public_admin/signin-oidc
       https://$admin_host$public_admin/signout-callback-oidc
  2. $up_cmd
$signin_step
  4. Back up $secrets/.pepper and $certs/data-protection.pfx separately from database
     dumps. Without them a restored database is unusable (and a client secret saved in the setup wizard unreadable).
  5. Internal certificates last 825 days; re-issue with: $0 --renew-certs
     To use your PKI instead, re-run with --gateway-cert/--gateway-key/--admin-cert/--admin-key.
  6. Move $certs/ca.key off this host (with the backups in step 4). Anyone who can read it can
     issue certificates the services trust; it is only needed again for --renew-certs.$update_note
$proxy_note
EOF
