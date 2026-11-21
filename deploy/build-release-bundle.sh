#!/bin/sh
# Builds the deployment bundle attached to a GitHub release: the files a Docker host needs to run
# compose.prod.yaml (or compose.portainer.yaml, or compose.rolling.yaml on top), the Kubernetes manifests and the optional
# Keycloak project (deploy/keycloak, built on the host), with the image defaults pinned to the released images, and
# docs/, the guides those files and the release notes point to.
#
#   GATEWAY_IMAGE=... ADMINAPI_IMAGE=... MIGRATIONS_IMAGE=... DATAAPI_IMAGE=... \
#     deploy/build-release-bundle.sh VERSION OUTPUT_DIR
#
# Each *_IMAGE is a full reference, ideally pinned by digest (registry/repo:version@sha256:...).
# Writes OUTPUT_DIR/fors-VERSION.tar.gz (one top-level directory containing deploy/ and docs/, so
# the README commands work unchanged) and OUTPUT_DIR/compose.portainer.yaml (paste into Portainer).
set -eu
version="${1:?usage: build-release-bundle.sh VERSION OUTPUT_DIR}"
out="${2:?usage: build-release-bundle.sh VERSION OUTPUT_DIR}"
: "${GATEWAY_IMAGE:?}" "${ADMINAPI_IMAGE:?}" "${MIGRATIONS_IMAGE:?}" "${DATAAPI_IMAGE:?}"
here="$(cd "$(dirname "$0")" && pwd)"
name="fors-$version"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
stage="$(mktemp -d)"
trap 'rm -r "$stage"' EXIT
mkdir -p "$stage/$name/deploy"

cd "$here"
cp -R compose.prod.yaml compose.portainer.yaml compose.rolling.yaml init-deployment.sh rolling-update.sh \
  Backup-Database.ps1 Restore-Database.ps1 postgres redis proxy otel kubernetes keycloak upgrade "$stage/$name/deploy/"
# The licence and the copyright notice travel with every copy (Apache-2.0, section 4).
cp ../LICENSE ../NOTICE "$stage/$name/"
# The guides the scripts, the Compose files and the release notes name (docs/runbook.md, docs/keycloak.md, ...), so an
# operator with only the bundle has them at the released version (#113).
cp -R ../docs "$stage/$name/"
cd "$stage/$name"
# Every guide a bundled file names is in the bundle.
missing="$(grep -rhoE 'docs/[A-Za-z0-9_-]+\.md' deploy | sort -u | while read -r doc; do [ -f "$doc" ] || echo "$doc"; done)"
[ -z "$missing" ] || { echo "error: the bundle names guides it does not contain:" $missing >&2; exit 1; }
cd deploy
# Line endings matter: these files are mounted into Linux containers or run by sh.
find . -type f -exec sed -i 's/\r$//' {} +
chmod +x init-deployment.sh rolling-update.sh kubernetes/*.sh keycloak/init-keycloak.sh upgrade/*.sh

# ${GATEWAY_IMAGE:-local default} and ${GATEWAY_IMAGE:?required} both become ${GATEWAY_IMAGE:-<released image>},
# so the variables still override the release.
for var in GATEWAY_IMAGE ADMINAPI_IMAGE MIGRATIONS_IMAGE DATAAPI_IMAGE; do
  ref="$(eval echo "\$$var")"
  sed -i -E 's#\$\{'"$var"':[-?][^}]*\}#${'"$var"':-'"$ref"'}#' compose.prod.yaml compose.portainer.yaml
done
for file in compose.prod.yaml compose.portainer.yaml; do
  grep -q 'image: \${[A-Z]*_IMAGE:-fors/' "$file" && { echo "error: unpinned image left in $file" >&2; exit 1; }
done
# Kubernetes: the base keeps its local image names; an images transformer maps them to the released images by digest.
{
  echo "images:"
  for var in GATEWAY_IMAGE ADMINAPI_IMAGE MIGRATIONS_IMAGE DATAAPI_IMAGE; do
    ref="$(eval echo "\$$var")"; repo="${ref%@*}"; repo="${repo%:*}"; digest="${ref##*@}"
    svc="$(echo "${var%_IMAGE}" | tr 'A-Z' 'a-z')"
    case "$ref" in *@sha256:*) echo "  - {name: fors/$svc, newName: $repo, digest: $digest}";;
                   *) echo "  - {name: fors/$svc, newName: $repo, newTag: \"${ref##*:}\"}";; esac
  done
} >> kubernetes/base/kustomization.yaml

cd "$stage"
tar -czf "$out/$name.tar.gz" "$name"
cp "$name/deploy/compose.portainer.yaml" "$out/compose.portainer.yaml"
echo "Wrote $out/$name.tar.gz and $out/compose.portainer.yaml"
