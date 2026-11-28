# Upgrade guide and release checklist

Each release says whether it supports a **rolling update** ([Updates without downtime](zero-downtime-updates.md)): the
previous version keeps running against the new schema while its instances are replaced. "No" means a planned stop: stop
the gateway, admin API and Data API, migrate, start the new version.

## 0.1.0

Initial demonstrator: .NET 10.0.12, Aspire 13.6, Vue 3, PostgreSQL 17 and Redis 8.6.
Production deployment now uses chiseled-extra images, immutable base digests, file-mounted
secrets, verified TLS, restricted DB roles/Redis ACL, static SPA serving and segmented Compose.
See [runbook](runbook.md) for the actual deployment procedure.

## 0.13.0: the organisation logo for integrations

- **Rolling update: yes** from 0.12.2. No migration, no configuration change. From 0.12.0 or 0.12.1, upgrade straight
  to 0.13.0 and follow the 0.12.2 steps below (its first update recreates `postgres` once). From 0.11, upgrade straight
  to 0.13.0 and follow the 0.12.2 steps for an upgrade from 0.11.
- **nginx in front of the admin console** (#125): coming back from the sign-in provider with an old session's cookies
  in the browser, the callback's `Set-Cookie` headers outgrew nginx's default 4 KB buffer and nginx answered
  `502 Bad Gateway` until the user cleared their cookies. `deploy/proxy/nginx.conf.example` now has the lines below. An
  existing `proxy/nginx.conf` (from `init-deployment.sh --proxy`) or a proxy of your own: add `proxy_buffer_size 32k;
  proxy_buffers 8 32k; proxy_busy_buffers_size 64k; large_client_header_buffers 4 32k;` at its `http` level and reload
  it. Nginx Proxy Manager: the admin UI host's *Advanced* tab ([runbook](runbook.md)). Kubernetes with ingress-nginx:
  the annotation `nginx.ingress.kubernetes.io/proxy-buffer-size: 32k`, as in `deploy/kubernetes/example/ingress.yaml`.
- **Also in 0.13.0, nothing to do:** the Integration API serves the admin console's logo at
  `GET /integration/v1/branding/logo` to any usable credential key, and `/v1/fors/context` announces the feature
  `organisation-logo` (#126); the favicon is an "F".

## 0.12.2: the database roles renamed by the stack

- **Rolling update: yes** from 0.12.0 and 0.12.1. No migration, no configuration change. From 0.11, upgrade straight to
  0.12.2 and follow the 0.12.0 steps below (rolling update: no) with the difference below; skip 0.12.0 and 0.12.1.
- **The Compose forms rename the database roles themselves.** `compose.prod.yaml` (and so `compose.rolling.yaml`) and
  the Portainer stack have a one-shot service `roles` that runs before `migrations` on every start. As the Postgres
  superuser, over Postgres's Unix socket and with no network, it ends the sessions of the roles `ume_*` and renames them
  to `fors_*` (`deploy/upgrade/0.12-rename-roles.sql`). On a new installation, and on one whose roles are renamed, it
  does nothing. `migrations` waits for it (`roles: service_completed_successfully`), and the gateway, admin API and Data
  API wait for `migrations`, so none of them starts before the rename.
- **Portainer from 0.11:** rename the variables (0.12.0, *Portainer*, step 1) and update the stack with 0.12.2's
  `compose.portainer.yaml` (step 2). The migration service signs in at that update, so step 3 is not needed. A stack
  that was updated with 0.12.0's or 0.12.1's file and stopped at its migration service goes on the same way: update it
  with 0.12.2's file. Step 3's statement stays as the fallback: if `roles` fails, its log says why, and the statement
  does the same by hand. In a stack that also holds Källa, the gateway's roles are renamed before `migrations` runs,
  and Källa's bootstrap waits for `migrations`; Källa's own database follows Källa's guide.
- **Compose from 0.11:** `deploy/upgrade/upgrade-to-0.12.sh` (it renames the variables and moves the database
  volume, whose name follows the project). Its step 3 still renames the roles; `roles` then does nothing. It now
  prints its plan unless you pass `--apply`, and it reads the installation instead of assuming the bundle's names
  (see 0.12.0, *Compose*).
- **Kubernetes from 0.11:** unchanged, the roles are renamed by hand (0.12.0, *Kubernetes*, step 2): the base uses a
  Postgres outside the cluster, whose superuser it does not have.
- **What changes in the files:** Postgres's socket directory `/var/run/postgresql` is the volume `postgres-socket` (a
  tmpfs, in memory and empty at every start, as before), which `roles` mounts too. `roles` uses the Postgres image the
  stack already pins and the superuser password file `postgres` reads; the Portainer bootstrap writes the statement to
  `fors-config` (`postgres/rename-roles.sql`). Because its mounts change, the first `up` (or stack update) with 0.12.2's
  files recreates the `postgres` container: a short restart of the database, which the gateway's request path does not
  use. `rolling-update.sh` leaves `postgres` running and runs `migrations` without its dependencies, so without
  `roles`: an update without downtime starts from 0.12.0 or later, whose roles are renamed.
- **The bundled Keycloak** (`deploy/keycloak`, #112): its realm import could not read its trust store after
  `init-keycloak.sh`, so the realm was never imported, while `up --wait` reported success. Re-run
  `./deploy/keycloak/init-keycloak.sh` (it keeps every secret and setting), then
  `docker compose -f deploy/keycloak/compose.yaml --env-file deploy/keycloak/.env up -d --build --wait`, which now waits
  for the import and fails when it fails. A new idle service `realm-imported` stays up for that.
- **Also in 0.12.2, nothing to do:** the setup callback's limit counts each caller on its own when
  `Admin:AuditSourceAddress` is `None`, and with `Truncated` it now counts per full address (#105); a rolling update no
  longer loses a POST now and then: a stopping instance refuses new connections for a second after its drain time
  (#106); the release bundle holds `docs/` (#113).

## 0.12.1: stored secrets readable after the rename

- **Rolling update: yes** from 0.12.0. No migration, no configuration change. From 0.11, upgrade straight to 0.12.1 and
  follow the 0.12.0 steps below (rolling update: no); skip 0.12.0.
- **What 0.12.0 broke:** it renamed the Data Protection application name and purposes, so after an in-place upgrade from
  0.11 the provider credentials and the sign-in client secret saved in the setup wizard no longer unprotected
  (thekim1/fors#116). 0.12.1 uses the names from before the rename again, and what 0.11 saved is readable.
- **If you already run 0.12.0:** after upgrading, enter again the provider credentials and the sign-in client secret
  you saved or changed under 0.12.0 (in the admin console, or with `FORS_SETUP_REOPEN` and the wizard). Admin sessions
  started under 0.12.0 end: sign in again.

## 0.12.0: the rename to Fors

The gateway is now called **Fors**, and the RAG platform that was called Ume.Rag is now **Källa**. Every name that
said "ume" changes: the projects and namespaces (`Fors.*`), the variables, the headers, the key prefix, the database
roles, the Redis keys, the metrics, the images and the repository (thekim1/fors, `https://thekim1.github.io/fors/`).
The licence changes from EUPL-1.2 to **Apache-2.0**. Earlier releases stay available under EUPL-1.2. Examples and the
default organisation name stay *Umeå kommun*.

- **Rolling update: no.** Stop the gateway, admin API, Data API and Keycloak, rename the database roles, then start the
  new version. The database roles, the volumes and the Redis keys change name, so the old and new versions cannot run side by
  side. Everyone signs in again: the admin sessions lived in Redis under the old prefix.
- **What keeps working until 0.13** (deprecated; switch now):
  - **Keys** `ume-sk-…` and Data API keys `ume-dk-…` stay valid. New keys are `fors-sk-…` and `fors-dk-…`.
  - **Request headers** `x-ume-*` (`x-ume-max-residency`, `x-ume-on-behalf-of`, `x-ume-session`, `x-ume-run-id`,
    `x-ume-step`, `x-ume-stream-cost`) are still read; when both are sent, the `x-fors-*` header wins. With
    `x-ume-stream-cost: 1` the stream ends with `: ume-cost-usd=…`, with `x-fors-stream-cost: 1` with `: fors-cost-usd=…`.
  - **Response headers** come under both names (`x-fors-provider` and `x-ume-provider`, ...).
  - **`/v1/ume/context`** answers like `/v1/fors/context` (it is not in the OpenAPI document).
  - **`GET /v1/models`** lists the gateway's facts under `fors` and, as a copy, under `ume`.
  - **Configuration documents** may name `credentialEnvironment` `UME_PROVIDER_*`. The variable must exist under that
    name in the admin API's environment.
- **Renamed without a fallback:**
  - **Variables:** `UME_*` becomes `FORS_*` (`UME_DOMAIN` → `FORS_DOMAIN`, `UME_OIDC_AUTHORITY` → `FORS_OIDC_AUTHORITY`, and so on;
    the rest of each name is unchanged).
  - **Database roles:** `ume_migrator`, `ume_gateway`, `ume_admin`, `ume_data`, `ume_keycloak` become `fors_*`. Migration
    `ForsRename` renames the function `ume_retention_step` to `fors_retention_step`.
  - **Redis:** keys and the invalidation channel move from `ume:` to `fors:`. The ACL in `deploy/redis/users.acl.example`
    follows; the Compose forms write it on every start. Budget counters are summed from Postgres again, and rate limits
    and circuit breakers start empty. Before the stop, check that the usage journal is empty (*Operations* in the admin
    console shows the usage writer's state). Batches still in it stay under the old key and are not written.
  - **Metrics:** `ume.gateway.*` becomes `fors.gateway.*`: update dashboards and alerts.
  - **Defaults:** the OIDC client id becomes `fors-admin` (was `ume-admin`), and the Data API audience
    `fors-data-api` (was `ume-data-api`). A deployment that relied on the old defaults sets them (the upgrade script
    does that): `FORS_OIDC_CLIENT_ID=ume-admin`, `FORS_DATA_AUDIENCE=ume-data-api`.
  - **Images:** `ghcr.io/thekim1/fors/{gateway,adminapi,migrations,dataapi}`. The release bundle is
    `fors-X.Y.Z.tar.gz`.
  - **Compose:** the project `ume-gateway` becomes `fors-gateway`, and with it the database volume
    (`fors-gateway_postgres-data`) and the network the bundled Keycloak joins (`fors-gateway_data`). The Keycloak project
    becomes `fors-keycloak`.
  - **Portainer:** the volumes `ume-certs` and `ume-secrets` become `fors-certs` and `fors-secrets`.
  - **Kubernetes:** the Secrets and ConfigMaps `ume-*` become `fors-*` (`fors-adminapi-secrets`, `fors-gateway-config`, ...).
- **Keycloak keeps its realm.** The realm, the admin console's client and the import client are now variables
  (`FORS_KEYCLOAK_REALM`, `FORS_KEYCLOAK_ADMIN_CLIENT_ID`, `FORS_KEYCLOAK_IMPORT_CLIENT_ID`; new installations get
  `fors`, `fors-admin`, `fors-realm-import`). An existing Keycloak keeps `ume`, `ume-admin` and `ume-realm-import`, so
  the issuer (`https://<host>/realms/ume`) and every other system's client stay as they are. Another system's realm
  file in `additions/` names the realm `"$(env:FORS_KEYCLOAK_REALM)"` from now on; one that still says `"ume"` keeps
  working in a kept realm. The import's copy of the client secret is now `keycloak-import/admin-client-secret`.

Back up first, as for every release: the database and the key pepper with the Data Protection certificate
([runbook](runbook.md)).

### Compose (`init-deployment.sh`)

`deploy/upgrade/upgrade-to-0.12.sh` does the upgrade. Run it from the new release's directory with the old installation
still running, and with its `deploy/.env` (and `deploy/keycloak/.env`) copied into the new `deploy/`. Without
`--apply` it only prints its plan: the old project it found, where the database is, and every step it would take.

```bash
./deploy/upgrade/upgrade-to-0.12.sh          # the plan; changes nothing
./deploy/upgrade/upgrade-to-0.12.sh --apply  # renames the variables, copies the old deploy/certs and deploy/secrets,
                                             # stops the services, renames the roles, moves the volume
./deploy/init-deployment.sh                # writes the connection strings with the new role names; keeps your settings
docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait
./deploy/keycloak/init-keycloak.sh         # only with the bundled Keycloak; keeps realm ume
docker compose -f deploy/keycloak/compose.yaml --env-file deploy/keycloak/.env up -d --build --wait
docker volume rm ume-gateway_postgres-data # once everything runs
```

**It reads the installation** (`deploy/upgrade/lib.sh`), so a host that differs from the bundle works too:

- **The old project** is the one running a gateway and a Postgres (`--old-project` names it when there are several);
  **the new one** is `COMPOSE_PROJECT_NAME` from the shell or the env file, else `fors-gateway` (`--project`). With the
  same name for both, the database stays where it is.
- **The database** is found at Postgres's mount: a named volume is copied to `<project>_postgres-data`; a volume on a
  host directory (a `device` option) gets a new volume on the same directory; a direct bind mount stays, and your
  Compose override goes on mounting it. Nothing on a host directory is copied, and a missing or empty database is
  refused before anything changes.
- **The certificates and secrets** (the key pepper, the Data Protection certificate) and the integration manifests and
  secrets are copied from the old release's `deploy/` into the new one, owners kept, when the env file names them
  there; `init-deployment.sh` would otherwise make new ones. A directory you chose (`--certs-dir`, `--secrets-dir`)
  stays, and the script prints the `init-deployment.sh` options that keep it.

`init-deployment.sh` asks for `sudo` here: the copied `certs/` and `secrets/` belong to the services' users, so it runs
itself again as root and gives `deploy/.env` back to you afterwards.

The script keeps the old files as `.env.pre-0.12`. It adds `FORS_OIDC_CLIENT_ID=ume-admin` (when the file sets an
authority but no client) and `FORS_DATA_AUDIENCE=ume-data-api`. For Keycloak it adds `FORS_KEYCLOAK_REALM=ume`,
`FORS_KEYCLOAK_ADMIN_CLIENT_ID=ume-admin` and `FORS_KEYCLOAK_IMPORT_CLIENT_ID=ume-realm-import`. A rolling
installation (`compose.rolling.yaml`) is upgraded the same way; start it with its usual command afterwards.

### Portainer

1. **Rename the variables** in the stack: every `UME_*` becomes `FORS_*`. If you relied on the old defaults, add
   `FORS_OIDC_CLIENT_ID=ume-admin` (when `FORS_OIDC_AUTHORITY` is set) and `FORS_DATA_AUDIENCE=ume-data-api`.
2. **Update the stack** with the release's `compose.portainer.yaml`. The bootstrap copies the volumes `ume-certs` and
   `ume-secrets` into `fors-certs` and `fors-secrets` once, so the key pepper and the Data Protection certificate carry over
   (its log says so). The migration service then fails to sign in, because the roles do not have their new names yet.
3. **Rename the roles**: open a console (`/bin/sh`) on the stack's `postgres` container and run

   ```sh
   PGPASSWORD="$(cat /run/postgres-secrets/bootstrap_password)" psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "
   DO \$\$ DECLARE r text; BEGIN FOREACH r IN ARRAY ARRAY['migrator','gateway','admin','data','keycloak'] LOOP
     IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ume_' || r) AND NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'fors_' || r)
     THEN EXECUTE format('ALTER ROLE %I RENAME TO %I', 'ume_' || r, 'fors_' || r); END IF; END LOOP; END \$\$;"
   ```

   (the same as `deploy/upgrade/0.12-rename-roles.sql`), then **redeploy the stack**.
4. Once it runs, remove the volumes `<stack>_ume-certs` and `<stack>_ume-secrets` if you like. They stay declared (and
   empty in a new stack) until 0.13.

### Kubernetes

1. Stop the services: `kubectl -n <namespace> scale deploy/gateway deploy/adminapi deploy/dataapi --replicas=0`.
2. Rename the roles in your Postgres as its superuser: `psql -f deploy/upgrade/0.12-rename-roles.sql`.
3. Re-run `./deploy/init-deployment.sh --no-chown` on the machine that holds `deploy/secrets` and `deploy/certs` (it
   writes the connection strings with the new role names), then `./deploy/kubernetes/create-secrets.sh --namespace
   <namespace>` (it creates the `fors-*` Secrets from the same files).
4. In your overlay, rename the references to `ume-*` ConfigMaps and Secrets and the `ume-llm-gateway/*` images to their
   `fors` names, and set `Oidc__ClientId=ume-admin` and `DataApi__Audience=ume-data-api` if you relied on the old
   defaults. Apply it (`./deploy/kubernetes/deploy.sh`).
5. Once it runs: `kubectl -n <namespace> delete secret,configmap -l app.kubernetes.io/part-of=ume-llm-gateway`.
   This label selects only the old objects; the new ones are labelled `part-of=fors`.

### Integrations (Källa and other platforms)

Nothing breaks during 0.12: old keys, headers, the context path and the `ume` facts keep working. Switch to
`x-fors-*`, `/v1/fors/context`, the `fors` facts and `: fors-cost-usd=` before 0.13. The preset `ume-rag.json` is now
`kalla.json` (title *Källa*), and its integration is named `Kalla` (an integration's name names a directory of secret
files, so it stays ASCII). A manifest finds its integration by name, so **rename an existing `Ume.Rag` integration to
`Kalla`** under *Integrations* before you provision from the new preset or from a manifest that says `Kalla`;
otherwise a second integration is created. The secret files of credential keys created from then on are under `Kalla/`
in the integration secrets. The default owner code of a manifest drops accents now: å, ä and ö count as a, a and o.

## 0.11.0: setup mode and integration manifests without an owner

The admin console's sign-in (authority, client id, client secret, claim names) can be set in a browser wizard, **setup
mode**, instead of the deployment's variables ([identity and roles](identity-and-roles.md#setup-mode)); the Portainer
stack names its hosts from `UME_DOMAIN`; `/v1/ume/context` names the sign-in provider. **An existing deployment keeps its
variables, stays declared and never sees the wizard:** it sets `UME_OIDC_AUTHORITY` (every deploy form required it until
now), and a deployment that sets the authority counts as set up at its first start. Nothing has to be added. The steps for
each deploy form follow the list of changes.

- **Rolling update: yes.** Migration `SignInSettings` adds the table `SignInSettings` (the stored sign-in values, all
  unset); migration `SetupMode` adds four nullable setup columns to it, creates its single row and adds the tables
  `SetupSessions` and `SetupTests`. `app-roles.sql` (and its copies: `compose.portainer.yaml`,
  `deploy/kubernetes/base/app-roles.sql`) only adds grants: the admin role reads and updates the row and the two tables,
  the gateway role reads only the `Authority` column, the Data API role nothing. The previous version reads none of them,
  so it keeps running against the new schema while its instances are replaced. Use the release's files.
- **The deploy files no longer require the authority.** `UME_OIDC_AUTHORITY` (Kubernetes: `Oidc__Authority`) is optional:
  set, the sign-in is declared and setup never opens; unset, the admin API starts with sign-in not configured and opens
  setup mode for an hour at its first start, with a setup code on its output. `init-deployment.sh` no longer requires
  `--oidc-authority` (an interactive first run asks once; empty leaves it to the wizard), and a re-run without it keeps
  the value in `deploy/.env`. `--oidc-client-secret-file` now needs the authority, and the Portainer bootstrap writes
  `UME_OIDC_CLIENT_SECRET` only together with `UME_OIDC_AUTHORITY`: a deployment client secret goes only to the
  deployment's own provider, so without one it would make every test sign-in of the wizard fail.
- **Client id and claim names are passed only when set.** The Compose files and the Portainer stack used to pass
  `UME_OIDC_CLIENT_ID`, `UME_OIDC_ROLE_CLAIM`, `_DEPARTMENT_CLAIM`, `_TEAM_CLAIM` and `_GROUP_CLAIM` with defaults, which
  made them deployment values and would lock the wizard's fields. Now an unset variable reaches the admin API empty, which
  counts as unset, and the code's default applies; the defaults are the old files' defaults (`ume-admin`, `roles`,
  `departmentCodes`, `teamCodes`, `groups`), so the effective values do not change. `UME_OIDC_EXTRA_SCOPE` defaulted to
  `email`, which the admin API requests anyway (`openid`, `profile` and `email` always): unset, no extra scope is passed
  and the scopes requested are the same. *Settings* now shows such a value's source as the default instead of the
  deployment. The Kubernetes base no longer sets `Oidc__ClientId=ume-admin` (the code's default is the same). The role
  groups (`UME_OIDC_GROUPS_*`) stay deployment values.
- **The setup secret.** Setup mode derives its code from a generated secret, the file `Setup__Secret` of the admin API
  (at least 32 characters). The Portainer bootstrap and `init-deployment.sh` generate it once and never overwrite it;
  `create-secrets.sh` puts it in `ume-adminapi-secrets` with the other files. It needs no backup (a new one only changes
  the codes printed from then on). A declared deployment does not need it, except to re-open setup.
- **New optional variables.** `UME_SETUP_REOPEN` (Kubernetes: `Setup__Reopen`), empty by default: a new value opens setup
  once more, for an hour, at the admin API's next start, and is consumed; `init-deployment.sh` writes the empty line and
  keeps a value on a re-run. `UME_DOMAIN` (Portainer) and `--domain` (`init-deployment.sh`) name the hosts
  (`llm.<domain>`, `llm-admin.<domain>`, the public URL `https://llm.<domain>`); `UME_GATEWAY_HOST`, `UME_ADMIN_HOST` and
  `UME_GATEWAY_PUBLIC_URL` still win, and `UME_GATEWAY_HOST` alone is enough without the domain, so an existing stack
  changes nothing.
- **The context names the sign-in provider:** the Compose files and the Portainer stack pass `UME_OIDC_AUTHORITY` to the
  gateway as `Gateway__OidcAuthority`, which `/v1/ume/context` hands out as `gateway.oidc_authority`; empty, it hands out
  the authority saved in the wizard, else `null`. Kubernetes: add `Gateway__OidcAuthority` to `ume-gateway-config` (the
  example lists it as a comment).
- **The Data API does not follow the wizard.** Its token issuer is still `UME_DATA_AUTHORITY`, else `UME_OIDC_AUTHORITY`
  (Kubernetes: `DataApi__Authority`). A deployment whose sign-in is set in the wizard sets `UME_DATA_AUTHORITY` for
  access tokens; without it the Data API accepts only Data API keys.
- **Manifests:** `integration.owner` may be left out. A manifest that names an owner behaves as before. Without one, a
  new integration is placed in a team and department made from its name (`My.Platform` → code `my-platform`), or in an
  existing team with that code; move it to your own team under *Integrations* afterwards. A platform's Portainer stack
  can then carry its manifest as a default instead of asking for a department code.
- **Changed behaviour:** `PUT /api/integrations/{id}` with another `ownerTeamId` no longer answers 409 while the
  integration has credential keys; it moves them (those not revoked) to the new team. A script that relied on the 409
  as a guard must check `credentialKeys` itself. During a rolling update an admin API of the previous version still
  answers 409.
- **Ume.Rag preset:** `ume-rag.json` no longer declares an owner. An integration already created from it is not changed.

### Portainer

**Before the update**

1. **Back up** the database and the two files a restore needs (`ume-secrets/.pepper`, `ume-certs/data-protection.pfx`;
   [runbook](runbook.md#portainer-and-other-hosts-without-shell-access)).
2. **Look at the stack's variables.** `UME_OIDC_AUTHORITY` holds your provider's address, as it must in a working stack;
   that is what keeps the stack declared. Keep every `UME_OIDC_*` you set. Nothing is to be added.

**The update**

3. **Edit the stack:** paste the new `compose.portainer.yaml` (the one attached to the release pins its images) over the
   old one, keep the stack name and the variables. Or redeploy from Git.
4. **Update the stack** with *Re-pull image and redeploy* on. The `bootstrap` container now also writes
   `adminapi/Setup__Secret`; then `migrations` runs `SignInSettings` and `SetupMode`, then the services start.

**After the update**

5. **The end of `bootstrap`'s log** says `bootstrap: sign-in to the admin console is declared by the stack's variables
   (UME_OIDC_AUTHORITY), so the setup wizard stays closed: …`, and nothing after it. A line
   `bootstrap: UME_OIDC_CLIENT_SECRET is left out: …` means the authority is empty (see *If a setup code is printed* below).
6. **`adminapi`'s log** has, once, at its first start of the new version, the record
   `The deployment declares the sign-in (Oidc:Authority): setup mode counts as completed` (in the JSON line's `Message`),
   and **no** line with `Setup code`.
7. **`https://<admin host>/bff/user`** (no sign-in needed) shows `"signIn":{"configured":true}` and
   `"setup":{"open":false}`. Sign in as before. *Settings* shows the authority's source as the deployment.

### Compose (`init-deployment.sh`)

**Before the update**

1. **Back up** the database (`deploy/Backup-Database.ps1`, or `pg_dump` in the `postgres` container), `deploy/secrets/.pepper`
   and `deploy/certs/data-protection.pfx`.
2. `deploy/.env` already sets `UME_OIDC_AUTHORITY`; leave it. Nothing is to be added.

**The update**

3. **Extract the new bundle** next to the old one and move `deploy/.env`, `deploy/certs` and `deploy/secrets` across
   ([runbook](runbook.md#releases-github)). There is no need to run `init-deployment.sh`. If you do run it again, give it
   the options of the first run (`--proxy`, the host names, `--rolling`, ...): it writes `deploy/.env` again from them
   (the old one is kept as `.env.bak`) and keeps only the sign-in lines and `UME_SETUP_REOPEN` from the old file.
4. **Start it:** `docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait`, or with two slots
   `./deploy/rolling-update.sh` ([Updates without downtime](zero-downtime-updates.md#update)).
5. **Optional, to be able to re-open setup later:** the setup secret. A re-run of `init-deployment.sh` (step 3) generates
   it; or by hand:

   ```bash
   openssl rand -hex 32 | tr -d '\n' | sudo tee deploy/secrets/adminapi/Setup__Secret > /dev/null
   sudo chmod 600 deploy/secrets/adminapi/Setup__Secret && sudo chown 1654:1654 deploy/secrets/adminapi/Setup__Secret
   ```

   and add the line `UME_SETUP_REOPEN=` to `deploy/.env` (unset works the same; the line is where a value goes later).

**After the update**

6. `docker compose -f deploy/compose.prod.yaml --env-file deploy/.env logs adminapi` has, once, the record
   `The deployment declares the sign-in (Oidc:Authority): setup mode counts as completed` (with two slots, in `adminapi`
   or `adminapi-b`), and **no** line with `Setup code`.
7. `/bff/user` shows `"signIn":{"configured":true}` and `"setup":{"open":false}`; sign in as before.

### Kubernetes

**Before the update**

1. **Back up** the database, `deploy/secrets/.pepper` and `deploy/certs/data-protection.pfx`.
2. Your overlay sets `Oidc__Authority`, which keeps it declared; leave it. Nothing is to be added.

**The update**

3. **Take the new base** (the release bundle's `deploy/kubernetes`, which pins the images, or the repository's) and keep
   your overlay. The base no longer sets `Oidc__ClientId=ume-admin` (same default) and sets an empty `Setup__Reopen`.
4. **Deploy:** `./deploy/kubernetes/deploy.sh <overlay>`. It ends with `==> Done` and, for a declared deployment, no line
   about setup mode.
5. **Optional, to be able to re-open setup later:** create `deploy/secrets/adminapi/Setup__Secret` (the `openssl` line of
   the Compose steps, without `sudo` and `chown`, or a re-run of `init-deployment.sh --no-chown` with the first run's
   options), run `./deploy/kubernetes/create-secrets.sh --namespace <ns>` again and
   `kubectl -n <ns> rollout restart deployment/adminapi`.

**After the update**

6. `kubectl -n <ns> logs -l app.kubernetes.io/component=adminapi --tail=-1` has, once (in the first new pod), the record
   `The deployment declares the sign-in (Oidc:Authority): setup mode counts as completed`, and no line with `Setup code`.
7. `/bff/user` shows `"signIn":{"configured":true}` and `"setup":{"open":false}`; sign in as before.

### If a setup code is printed after all

The admin API found no authority in its configuration: `UME_OIDC_AUTHORITY` (Kubernetes: `Oidc__Authority`) is missing or
empty. Until it is fixed, sign-in is not configured (`/bff/user` shows `"configured":false`). Either set the variable and
update again (setup then counts as completed at the next start and the code is void), or use the wizard with the printed
code ([runbook](runbook.md#first-start-without-an-authority-the-setup-wizard)). Both end in a working sign-in.

### Optional, afterwards: move sign-in into the wizard

Values saved in the wizard are stored in the database, the client secret encrypted with the Data Protection certificate.
Do each change in one go: between removing a value and saving it in the wizard, sign-in is incomplete.

- **Only the client secret** (the provider stays declared and locked in the wizard): remove `UME_OIDC_CLIENT_SECRET`
  (Compose: delete `deploy/secrets/adminapi/Oidc__ClientSecret`; Kubernetes: the file, then `create-secrets.sh`), set
  `UME_SETUP_REOPEN` (Kubernetes: `Setup__Reopen`) to a new value, for example today's date, and update. Open
  `https://<admin host>/setup` with the code from the admin API's log, type the secret, test the sign-in and finish.
- **Everything:** as above, and also remove `UME_OIDC_AUTHORITY` (Kubernetes: `Oidc__Authority`) and any `UME_OIDC_*`
  except the role groups. In this gateway the authority alone declares the sign-in: removing it is what hands the provider
  to the wizard, and the client secret file must go with it. The wizard needs the setup secret (the bootstrap writes it;
  Compose and Kubernetes: step 5 above). Two things follow the authority: the gateway's `Gateway__OidcAuthority` is then
  empty, so `/v1/ume/context` hands out the authority saved in the wizard (`null` until it is saved); and the Data API,
  which took `UME_OIDC_AUTHORITY` as its token issuer, accepts only Data API keys unless you set `UME_DATA_AUTHORITY` to
  the old address (Kubernetes sets `DataApi__Authority` on its own already).

### Restoring a database

- **Empty `UME_SETUP_REOPEN` first** (Kubernetes: `Setup__Reopen`). A backup taken before its last re-open, restored
  while the variable still holds that newer value, opens setup once more.
- A declared deployment needs nothing else: a backup from before this release, or from before setup counted as completed,
  is completed again at the first start.
- On a deployment whose sign-in was set in the wizard, a backup from before setup was completed opens setup again at the
  next start (a new code); finish it again, or declare the authority.
- **Restore with the same `data-protection.pfx`** (and pepper). A client secret saved in the wizard is encrypted with the
  key ring in the database, which that certificate protects. With another certificate the secret is unreadable: the admin
  API logs `The stored sign-in client secret cannot be unprotected and is treated as unreadable: …` and, on a sign-in,
  `Sign-in refused: sign-in is not configured (unreadable_client_secret)`; `/bff/user` shows `"configured":false`, and setup does not open by itself: set `UME_SETUP_REOPEN` to a new value and
  type the secret again in the wizard.

## 0.10.3: Ume.Rag preset keys limited to OnPrem and Eu

- **Rolling update: yes.** No migration, no configuration change.
- **Ume.Rag preset:** `rag-api` and `rag-admin` in `ume-rag.json` now allow only `OnPrem` and `Eu`, as `rag-worker`
  did. Keys already created from the preset are not changed (create-only): if you made them from 0.10.2's preset, set
  their residencies to `OnPrem` and `Eu` in the admin console.

## 0.10.2: organisation name and Integration API address in the context, provisioning integrations from a manifest

- **Rolling update: yes.** No migration. The migration service grants the gateway role `SELECT ("OrganisationName")` on
  `Branding`; a previous gateway ignores it. Use the release bundle's files (compose files, `init-deployment.sh`, the
  Portainer stack), which carry the new grant and mounts.
- **Optional:** set `UME_INTEGRATION_API_URL` (gateway `Gateway__IntegrationApiUrl`) to the Integration API's address as
  platforms reach it, for example `https://adminapi:8443/integration/v1` when they share a network with `adminapi`.
  Unset leaves `integration_api_url` null. The Compose files and the Portainer stack pass it; the Kubernetes example
  lists it as a comment.
- **Compose:** the migrations service now mounts `UME_INTEGRATION_MANIFESTS_DIR` (read-only) and
  `UME_INTEGRATION_SECRETS_DIR` (defaults `deploy/integration-manifests` and `deploy/integration-secrets`). Docker creates
  missing directories empty, which provisions nothing. To use them on an existing installation, run `init-deployment.sh
  --renew` again, or create the secrets directory yourself (`mkdir -m 700 deploy/integration-secrets; chown 1654:1654
  deploy/integration-secrets`).
- **Portainer:** the stack has a new volume, `<stack>_integration-secrets`, and an optional variable,
  `UME_INTEGRATION_MANIFEST`. Nothing is provisioned unless you set the variable.
- **Admin API:** presets are read from `Admin:IntegrationPresetsPath` (default `integration-presets` next to the admin
  API; the image ships `ume-rag.json`). Set it empty to offer none, or to a mounted directory to offer your own.

## 0.10.1: acting refusals answer 403

- **Rolling update: yes.** No migration, no configuration change.
- **Changed behaviour:** with `x-ume-on-behalf-of`, an expired assigned key answers `403 key_expired` (was `401`) and a
  revoked one `403 key_revoked` (was `403 delegation_not_allowed`). During the update both answers occur; a platform
  that matches on `error.code` with the header set handles both.

## 0.10.0: integrations for platforms such as a RAG system, department member groups

- **Compose and Kubernetes:** the Compose files (`compose.prod.yaml`, `compose.hardening.yaml`, the Portainer stack)
  now pass `UME_ADMIN_PUBLIC_URL` to the gateway as `Gateway__AdminUrl` and `UME_INTEGRATION_REQUESTS_PER_MINUTE` to
  the admin API. Add `UME_ADMIN_PUBLIC_URL=https://<admin host>[:port]` to an existing `deploy/.env` if platforms
  should link to the admin console; unset leaves it out. `Gateway:AdminUrl` may now be empty (it refused `""` before).
  The Kubernetes example lists both as comments.

- **Department member groups, rolling update: yes.** Migration `DepartmentMemberGroups` adds
  `Departments.MemberGroups` (`text[]`, default empty); a previous version ignores the column. Nothing changes until a
  gateway admin enters member groups on a department: from then on a user in one of those groups sees the department
  after their next sign-in ([Department scope](identity-and-roles.md#department-scope)). The identity provider must
  put the groups in the token. New fields: `memberGroups` on `Department` (admin API) and on the Data API's
  `/v1/catalog/departments`, `code` on `/v1/catalog/teams`, `departmentSources` on `/bff/user`, `member_groups` on the
  departments of `/v1/ume/context` for a credential key with `organisation.read`.

- **Residency bound and `organisation.read`, rolling update: yes**, no migration. **Changed behaviour:** a credential
  key's residencies now bound every key it acts as. A credential key without a residency list (the default) changes
  nothing; for one with a list, check that it holds every residency its service must reach. Until every gateway
  instance is updated an older one does not apply the bound: keep sending `x-ume-max-residency`. Give
  `organisation.read` (key form, *Integration access*) to the credential key of the service that syncs departments
  and teams.

- **Phase 3, rolling update: yes**, no migration. New setting `Admin__IntegrationRequestsPerMinute` (default 60). If
  the admin API is behind the reverse proxy, `/integration/v1` must reach it as `/api` does; the nginx example and the
  Ingress example route every path of the admin host, so nothing changes there.

- **Phase 2, rolling update: yes.** Migration `UsageRunMetadata` adds three nullable columns to `UsageRecords`
  (`ViaKeyId`, `RunId`, `Step`; no table rewrite); an older gateway writes its usage without them. `app-roles.sql` grants
  the Data API's role the key columns `IntegrationId` and `IntegrationRole`. During the update an older instance ignores
  `x-ume-on-behalf-of` and answers as the credential key without the echo: a platform must check the echo and fail the
  call when it is missing.

- **Rolling update: yes.** Migration `Integrations` adds the table `Integrations`, the key columns `IntegrationId`,
  `IntegrationRole` (default `None`) and `IntegrationScopes` (default empty) and `ModelDeployments.MaxOutputTokens`, all
  with defaults, so the previous version keeps working against the new schema. `app-roles.sql` (and its copies,
  including `compose.portainer.yaml`) grants the gateway `SELECT` and the admin API `SELECT, INSERT, UPDATE, DELETE` on
  `Integrations`; use the release bundle's files.
- `GET /v1/models` now reports a model's own residency (`ume.residency`) when it has one, as routing has done since
  0.9.1; a client that read the provider's residency there sees the model's.
- **Security update:** the images are built on .NET 10.0.15 (`aspnet:10.0.15-noble-chiseled-extra`); use the new images.
- **Optional:** set `Gateway__AdminUrl` (`UME_ADMIN_PUBLIC_URL` in Compose) to the admin console's public address to
  hand it to integrations on `/v1/ume/context`.
- **Connecting a platform** ([integrations](integrations.md)): create the integration under *Access › Integrations*,
  then its credential keys in the owner team; departments create assigned keys for their chatbots.

## 0.9.2: admin console branding, Keycloak groups shared with other systems

- **Rolling update: yes.** Migration `Branding` adds the table `Branding`; the migration service creates its single row
  (organisation name *Umeå kommun*, no logo) and grants the admin API's role `SELECT` and `UPDATE` on it with
  `app-roles.sql`, as on every start. An older admin API ignores it, and the gateway and Data API have no access to it.
  The Compose and Kubernetes files change only in the roles file (`app-roles.sql` and its copies, including
  `compose.portainer.yaml`); the Redis ACL is unchanged. Use the release bundle's files, which carry the new roles file.
- **Reverse proxy:** allow 52 MB request bodies (was 32 MB), or image edits between 32 and 50 MB are refused with 413 at
  the proxy. The nginx example, the Nginx Proxy Manager settings in the runbook and the Kubernetes Ingress example say
  52 MB now; an existing `proxy/nginx.conf` or Ingress is yours to change.
- **Data API behind the proxy (optional):** re-run `init-deployment.sh --proxy --data-host <third host name>` to get a
  `proxy/nginx.conf` with a Data API server block; on Kubernetes, `example/ingress-dataapi.yaml`. Without it, port 10443
  is bound to `0.0.0.0` in proxy mode: firewall it ([Network and ports](network-and-ports.md)).
- **Kubernetes NetworkPolicies (optional):** `components/networkpolicies`; adjust the lines marked `change` before
  enabling it ([Kubernetes](kubernetes.md#network-policies)).
- **After the upgrade**, set your organisation's name and logo under *Settings > Branding* if you are not Umeå kommun.
- **Keycloak project:** `up -d --wait` in `deploy/keycloak` re-runs the realm import, which adds a `groups` mapper to the
  `ume-admin` client (ID token only). Nothing else changes; a gateway still on the previous version ignores the claim.
  To share groups with another system, see [Keycloak](keycloak.md#realm-files), *One group for several systems*.

## 0.9.1: rerankers on Azure AI Foundry, a residency per model

- **Rolling update: yes.** Migration `DeploymentResidencyAndAddress` adds four nullable columns to `ModelDeployments`
  (`Residency`, `Publisher`, `RequestBaseUrl`, `RequestAuthMode`), all null for existing models, which keep their
  provider's residency and address. The previous version does not read or write them and keeps working against the new
  schema (tested in `DeploymentResidencyMigrationTests`). Compose, Kubernetes and Redis ACL files are unchanged.
- **Set a model's own residency only after every gateway and admin API instance runs this version.** An older gateway
  routes the model with its provider's residency, so a model marked *External* under an *EU* provider (a global
  deployment) could still be chosen within an `Eu` ceiling or for an EU-only key by an older instance. Likewise, add a
  Foundry reranker only after the upgrade: an older gateway does not route it.
- **Check your Foundry models after the upgrade.** Run *Discover models* on each Azure AI Foundry provider: it suggests
  a residency for global and data zone deployments and the publisher; open each model and confirm or change its
  *Residency*. Nothing changes until you do. A Foundry reranker that was set up as a second, OpenAI-compatible provider
  keeps working; to move it under the Foundry provider, add the model there (kind *Rerank*), check it, point routes and
  keys at it and delete the second provider.
- **Changed:** a rerank answer without token counts is charged by the request-size estimate instead of 0 tokens.
- **Admin API:** `PUT /api/models/{id}` replaces `residency` and `requestAuthMode` like `contextWindow` (null or omitted =
  the provider's): a script that updates models should send the model's `residencyOverride` and `requestAuthMode` back.
  `Model.residency` is now the residency in effect (the model's own, else the provider's).
- **Data API:** `usage.aggregate` reports the model's own residency when it has one; `catalog/models` has a new field
  `residency` (the model's own, null = the provider's).

## 0.9.0: residency ceiling, reranking, optional Keycloak

- **Rolling update: yes.** Migration `KeyResidencyCeiling` adds `VirtualKeys.RequireMaxResidency` (`false` by
  default); the previous version does not read it. Compose files and the Redis ACL are unchanged; `pg_hba.conf` and
  `init.sql` gain lines for the optional Keycloak database, which do nothing unless you set it up.

**Residency ceiling per request**

- **New request header** `x-ume-max-residency: OnPrem | Eu | External`: a request can lower the residencies it may reach,
  never raise them. New error `403 max_residency_not_met` (no target within the ceiling is configured); `503
  no_eligible_provider` with `Retry-After` when targets within it are disabled or drained. `GET /v1/models` honours it.
  `x-ume-residency` is now documented. Nothing changes for clients that do not send the header.
- **New key setting** *Require residency header* (`requireMaxResidency`). Turn it on only after every gateway instance
  runs this version: an older instance ignores the header and does not refuse requests without it. Do not rotate such a
  key until the admin API is upgraded everywhere: an older admin API does not copy the setting to the replacement.
- During the rollout an older instance ignores the header: a client that must be sure checks `x-ume-residency` on the
  answer.

**Optional reranking endpoint**

- No migration (model kinds and capabilities are stored as names and flags).
- **New, optional:** `POST /v1/rerank`, a provider capability *Rerank* (OpenAI-compatible providers only) and a model
  kind *Rerank*; see [reranking](providers-and-models.md#reranking-optional). Nothing changes until you add one.
- **Add Rerank models only after every gateway and admin API instance runs this version:** an older instance cannot
  read the new model kind from the catalogue.

**Optional Keycloak project**

- Nothing changes unless you set up `deploy/keycloak` ([Keycloak](keycloak.md)); the release bundle now includes it.
- `deploy/postgres/pg_hba.conf` gains a line for the optional role `ume_keycloak` (database `keycloak` only) and
  `init.sql` creates them when `secrets/postgres/keycloak_password` exists. Kubernetes and Portainer copies are updated.
- `Backup-Database.ps1` and `Restore-Database.ps1` take `-Database` (default `gatewaydb`, as before).

**Embeddings:** `/v1/embeddings` compares the longest input, not the whole batch, with the model's context window, so
batches that were refused with `context_length_exceeded` now pass. No configuration change.

## 0.8.9: gateway memory fix, fewer Redis round trips

- **Rolling update: yes.** No migration; Compose, Kubernetes and Redis ACL files are unchanged (the rate limit and budget
  reservation script runs with `+eval`/`+evalsha`, which the `gateway` user already has).
- **Fix:** the gateway no longer traces every Redis command (`Aspire__StackExchange__Redis__DisableTracing`, default
  `true`), which kept about 400 MB of objects alive at 16 000 requests/s and crashed it in a 1 GiB container. Set it to
  `false` to trace Redis commands again. Requests and provider calls are still traced.
- **Behaviour:** for keys without a routing rule, the rate limit and budget reservation are one Redis call, so an
  exhausted budget is answered with 402 before the PII and routing checks.
- **New setting:** `Gateway__UsageBatchWindowMs` (default 50, `0` = write at once, range 0-5000). Usage shows in the
  admin UI up to 50 ms later; Postgres load drops with several gateway instances.

## 0.8.8: signing out works again

- **Rolling update: yes.** No migration and no configuration change.
- **Fix:** signing out of the admin UI answered 500 because the Redis ACL user `gateway` may not run `UNLINK`, which the
  Redis client used to delete the session. Keys are now deleted with `DEL`, which the ACL already allows, so deployed
  `users.acl` files stay as they are. If you added `+unlink` to the `gateway` user as a workaround, you can remove it again.
  The same fix makes *Close circuit* in Operations work, and a successful provider call resets its failure count again.

## 0.8.7: SEK leftovers dropped, .NET 10.0.14 security update

- **Rolling update: no** (drops columns 0.8.6 reads). Stop the gateway, admin API and Data API, run the migration service
  (migration `DropSekLeftovers`), then start the new version.
- **Dropped:** the `CostSek` columns of `UsageRecords` and `UsageMonthlyTotals` and the `ExchangeRates` table. Nothing has
  read or written them since 0.8.1; all costs are in `CostUsd`. Export them first if you want to keep the old SEK figures.
- **Data retention:** the migration also redefines `ume_retention_step` without `CostSek`; owner and grants stay as they are.
- **Database roles:** `app-roles.sql` (Compose, Kubernetes and Portainer) no longer names `ExchangeRates`. The migration
  service applies it on start; if you run the role grants yourself, use the new file **after** the migration, because the
  old one grants a table that no longer exists.
- **Rolling back:** migrating down recreates the columns (0) and an empty `ExchangeRates` table, so migrating further back
  past `BudgetsInUsd` (0.6.0) converts budgets with the fallback rate of 10 SEK per USD instead of the stored rates.
- **Security update:** the images are built on .NET 10.0.14 (`aspnet:10.0.14-noble-chiseled-extra`), which fixes four
  CVEs; Aspire 13.6.4 and OpenTelemetry 1.20.1. Nothing to configure.
- **Log categories** of single classes have new names (code grouped per feature): a level set for one class needs the
  new name, for example `Logging__LogLevel__Ume.LlmGateway.Gateway.Requests.GatewayRequestHandler` (was
  `...Gateway.Pipeline...`). Per-project levels and security events are unchanged.

## 0.8.6: a provider is deleted together with its models

- **Rolling update: yes.** No migration and no configuration change.
- **New:** *Delete* on a provider with models deletes them with it (*Delete provider and N models*), in one transaction;
  refused while a route or routing rule uses one of them. API: `DELETE /api/providers/{id}?deleteModels=true`; without
  the flag the call is 409 as before. See [Providers and models](providers-and-models.md#removing-a-provider).

## 0.8.5: Claude on the Foundry provider authenticates

- **Rolling update: yes.** No migration and no configuration change.
- **Fixed:** Claude deployments of an Azure AI Foundry provider with an API key failed with 401 (502 to the client): the key
  went to the resource's Anthropic API in `api-key`, which it does not read. It now goes in `x-api-key`.
- **Remove workarounds:** a separate *Anthropic* provider kept only for Foundry's Claude deployments is no longer needed;
  see [Providers and models](providers-and-models.md).

## 0.8.4: model prices from models.dev, Claude on the Foundry provider, team admins, audio priced per unit

- **Rolling update: yes.** Four migrations, each adding nullable or defaulted columns or widening one, so the previous
  version keeps working against the new schema while its instances are replaced: `AudioDurationPrecision`, `TeamCodes`,
  `ModelListPrices`, `ModelBaseModel`.
- **Check before upgrading:** prices of existing Anthropic, Ollama Cloud, OpenAI and OpenRouter models follow models.dev
  from two minutes after the admin API starts (see below), the admin API needs outbound HTTPS to `models.dev`, and known
  provider types now serve every endpoint of their API whatever was ticked.

### Model prices and details from models.dev

- **Rolling update: yes.** Migration `ModelListPrices` adds `ModelDeployments.AutoUpdatePrice` and
  `ProviderAccounts.AutoUpdatePrices` (both default `true`), `ModelDeployments.Description` and `ModelPrices.Source`; the
  previous version ignores them.
- **Changed behaviour: prices of existing models can change.** Two minutes after the admin API starts, models of
  Anthropic, Ollama Cloud, OpenAI and OpenRouter providers whose upstream name is in the models.dev list get the list
  price added to their price history when it differs from theirs (usage before keeps its cost), and empty descriptions,
  capabilities and context windows are filled. A replaced price is in the price history and the audit log
  (`system:price-list`). For an agreed price, turn off *Price from models.dev* on the model (or the provider) and add it
  again. Such models no longer take a price by hand while they follow the list (`POST /api/models/{id}/prices` answers 409).
- **Azure AI Foundry: one provider for Claude too.** Migration `ModelBaseModel` adds the nullable
  `ModelDeployments.BaseModel` (the model behind a deployment). A Foundry provider now discovers and serves Claude
  deployments (through the resource's `…/anthropic/v1`), so a separate Anthropic provider per resource is no longer
  needed. Existing Anthropic providers at a Foundry address keep working unchanged. To move to one provider, add the Claude
  deployments under the Foundry provider with *Discover models*, point routes at them, then delete the Anthropic one.
  Claude models added by hand under their own deployment name need *Base model* set (`claude-…`); until then they go to
  `…/openai/v1`, which does not serve Claude. Rolling update: yes; an old gateway ignores the column.
- **Azure deployments** now get descriptions, capabilities and context windows from models.dev, and a price from it when
  they have none (discovery and the daily update). Their prices are otherwise left alone. Models added before have no
  base model: one whose deployment name differs from its model (`prod-chat`) is not found in models.dev until it has one.
  Open *Discover models* on the Foundry provider and use *Update … shown*, which sets the base model and fills empty
  descriptions of models already added.
- **Provider capabilities follow from the provider type.** OpenAI, Azure OpenAI and Foundry providers now serve every
  OpenAI endpoint (Responses, speech to text, realtime, images), Anthropic chat and Messages, Ollama chat and embeddings,
  Ollama Cloud chat, whatever was ticked before; which model gets a request still follows its kind. Only OpenAI-compatible
  providers keep their chosen endpoints. A provider that had an endpoint unticked to keep requests away from it now gets
  them for models of that kind: disable or remove those models instead. `capabilities` on `POST`/`PUT /api/providers` is
  now optional and ignored except for `OpenAICompatible`. No migration.
- **Outbound HTTPS** from the admin API to `models.dev` (about 6 MB once a day). Without internet access, set
  `Admin__PriceListUrl` to a copy of `https://models.dev/api.json` in your network, or empty to turn it off; a failed
  download only logs a warning and leaves prices as they are.
- **Admin API:** `Model` has `description`, `autoUpdatePrice`, `priceMode` and `priceListKey`; `Provider` has
  `autoUpdatePrices` and `priceList`; `Price` has `source`; `DiscoveredModel` has `description` and `baseModel`, and `Model`
  `baseModel`; catalogue models have `description`
  and `features`. `POST`/`PUT /api/models` and `/api/providers` take the new settings (omitted keeps them).
  `GET`/`POST /api/settings/model-prices` are new. The configuration export includes the settings, descriptions and
  model capabilities.

### Teams manage their own keys

- **Rolling update: yes.** Migration `TeamCodes` adds the nullable `Teams.Code` column and a unique index on it; the
  previous version ignores both. While old admin API instances still run, a team admin's calls to them get 403.
- **Nothing changes until you opt in.** No team has a code after the upgrade, so no one is a team admin. To start: give
  a team a **Team code** in *Organisation*, then give its members the `team-admin` role and that code in the `teamCodes`
  claim, or map groups (`UME_OIDC_TEAM_CLAIM`, `UME_OIDC_GROUPS_TEAM_ADMIN`; add both to `deploy/.env` and the compose
  file you deploy with, as in `deploy/compose.prod.yaml`). See [Identity provider and roles](identity-and-roles.md#team-scope).
- **Admin API:** `Team` has a new `code` field, and `POST`/`PUT /api/teams` take it; `/bff/user` returns `teamCodes`.
  Existing clients that do not send `code` leave it unchanged.

### Input audio priced per second, minute or hour

- **Rolling update: yes.** Migration `AudioDurationPrecision` widens `ModelPrices.AudioPerMinuteUsd` from
  `numeric(18,6)` to `numeric(18,10)`; the old version reads and writes the wider column unchanged. `ModelPrices` is
  small, so the rewrite is instant.
- **Changed behaviour:** a model with `audioPerMinuteUsd` above 0 and `audioInputPerMillionUsd` at 0 bills its audio
  input tokens by the duration only, no longer also at `inputPerMillionUsd`. Whisper-style models report no audio tokens
  and cost the same as before. A model whose provider charges both duration and audio tokens needs an audio input token
  price.
- The admin API is unchanged: prices are still sent per minute. To enter a price per hour, divide it by 60 (or use the
  unit in the admin UI price form).

## 0.8.3: tool calls on gpt-5.6 and GPT-6 through Chat Completions

- **Rolling update: yes.** No migration and no configuration change.
- **Changed behaviour:** a Chat Completions request with `tools` to gpt-5.6 or GPT-6 (reasoning profile), and not setting
  `reasoning_effort: "none"`, is now sent to the provider's `/v1/responses` and answered in Chat Completions form. Before,
  the provider refused it with a 400. The provider must serve `/v1/responses` (Azure OpenAI and Foundry v1 do; the
  provider's *Responses* tick is not needed for this). See [Providers and models](providers-and-models.md).
- **Remove workarounds:** a team or key policy that forces `reasoning_effort: "none"` for these models is no longer
  needed, and on gpt-6-astra and gpt-6.1-sol it makes every request fail (they reject `none`). Clients set to
  `api_mode: responses` only for these models can go back to Chat Completions.

## 0.8.2: provider time to first token, failed requests in Usage

- **Rolling update: yes.** The migration `ProviderFirstToken` adds a nullable `ProviderFirstTokenMs` column to
  `UsageRecords`; older instances keep writing records without it while they are replaced.
- **Changed figures:** provider p50/p95 on Overview, Health & operations and Provider stability are now the provider's
  time to first token on streamed answers, not the whole request's latency, so they are much lower and no longer grow with
  the length of the answers. Requests recorded before the upgrade have no such figure, so right after it the columns show
  *—* until streamed traffic arrives.
- **Admin API rename:** `p50LatencyMs`/`p95LatencyMs` on `GET /api/ops/health` and `GET /api/ops/providers/stats` are now
  `p50FirstTokenMs`/`p95FirstTokenMs`. Update scripts that read them.
- **New fields and filters** (additive): `providerFirstTokenMs` on `GET /api/usage/requests` and the Data API's
  `/v1/usage/records`; `GET /api/usage/requests` takes `errors=true`, `model` and `provider`; metric
  `ume.gateway.provider.time_to_first_token`.
- The old `CostSek` columns and the `ExchangeRates` table are still kept in this release; they are dropped in a later one.

## 0.8.1: costs in USD only, SEK removed

- **Rolling update: yes.** The migration `SekNoLongerWritten` only gives the `CostSek` columns of `UsageRecords` and
  `UsageMonthlyTotals` a default of 0, so 0.8.0 keeps writing them while its instances are replaced. Nothing reads them
  any more; they and the `ExchangeRates` table are dropped in the next release (which will be **Rolling update: no**
  from 0.8.0, so upgrade through 0.8.1).
- **Breaking: SEK is gone from every interface.** Update whatever reads these before upgrading:
  - Gateway response headers `x-ume-cost-sek` and `x-ume-budget-remaining-sek`: use `x-ume-cost-usd` and
    `x-ume-budget-remaining-usd`.
  - Budget alert webhook: `spentSek` and `limitSek` are gone; use `spentUsd` and `limitUsd`.
  - Data API: `costSek` is gone from `/v1/usage/records` and `/v1/usage/aggregate` (use `costUsd`), `limitSek` from
    `/v1/catalog/budgets` (use `limitUsd`), and `/v1/catalog/exchange-rates` is removed. Convert to SEK in your report
    where needed.
  - Metric `ume.gateway.cost` (SEK) is removed; use `ume.gateway.cost.usd`.
  - Admin API: `GET`/`PUT /api/settings/exchange-rate` are removed (404), and so is the Settings card.
- **Configuration:** `Gateway:FallbackSekPerUsd` (`Gateway__FallbackSekPerUsd`) is no longer read; remove it from your
  settings. The gateway's readiness no longer has an `exchange-rate` check, so it is no longer Degraded without a rate.

## 0.8.0: admin UI menu and phone layout, costs in USD

- **Rolling update: yes.** No migrations; 0.7.1 runs side by side while its instances are replaced.
- **Usage CSV export in USD.** The admin UI's export (`/api/usage/export.csv`) has the column `kostnad USD` instead of
  `kostnad SEK`. Update spreadsheets or imports that read the column by name, and convert to SEK yourself where needed.
  The Data API, gateway response headers and budget webhooks still carry SEK next to USD.
- **Admin API:** usage summaries and the catalogue report USD (`totalCostUsd`, `costUsd`, `inputUsdPerMillion`,
  `outputUsdPerMillion`); request rows no longer have `costSek`. The catalogue no longer needs an exchange rate.
- **Developers:** each npm project has an `.npmrc` that turns off install scripts and only takes package versions that are
  at least 7 days old when you add or update a package; `npm ci` installs the lockfile as before. See
  [npm packages](security-and-compliance.md#npm-packages).

## 0.7.1: Redis ACL fix for admin sign-in

- **Rolling update: yes.** No migrations; 0.7.0 runs side by side while its instances are replaced.
- **Redeploy the Redis ACL.** Admin sessions are written with a time to live (`SETEX`/`PSETEX`), which the ACL shipped
  with 0.6.4 and 0.7.0 did not allow, so admin sign-in failed with a 500 (`RedisServerException`) right after the
  identity provider returned. Redeploy the stack (Portainer regenerates `users.acl` on every start) or run
  `init-deployment.sh` and restart Redis (Compose); Kubernetes: give the Redis user `gateway` the commands in
  `deploy/redis/users.acl.example`.

## 0.7.0: content guard, token quotas, usage journal, retries, rate limits, request policies, prompt caching

- **Rolling update: yes,** with the caveats below. Five additive migrations: `CacheWritePrice`, `KeyConcurrencyLimit`,
  `RequestPolicies`, `TokenBudgets` and `ContentGuard`; 0.6.4 runs against the new schema while its instances are replaced.
- **Usage journal: redeploy the Redis ACL.** The gateway user needs `zrangebyscore` (read-only) to take over usage batches
  of a stopped instance: redeploy the stack or run `init-deployment.sh` (Compose) so `users.acl` is regenerated from
  `deploy/redis/users.acl.example`, then restart Redis; Portainer regenerates it on redeploy. Without it the journal still
  works (no records are lost while the database is down) but batches of a stopped instance wait until it is back, and the
  log warns "Usage journal unavailable (… NOPERM …)". The journal's keys are `ume:usage-journal*` (inside `~ume:*`).
- **Content guard:** migration `ContentGuard` adds `VirtualKeys.ContentGuardPolicy` (default `Off`, so nothing changes)
  and `UsageRecords.GuardFlags`. Nothing to configure unless you want it (docs/content-guard.md).
- **Token quotas:** migration `TokenBudgets` adds `Budgets.Unit` (default `Usd`, so existing budgets are unchanged),
  `LimitTokens` and `Residency`, and `AlertEvents.SpentTokens` / `LimitTokens`. During a rolling update an old gateway
  instance does not enforce token quotas (it ignores the columns); create them after the rollout. The Data API budgets
  feed adds `unit`, `limitTokens` and `residency`.

- **Migrations:** `CacheWritePrice`, `KeyConcurrencyLimit` and `RequestPolicies` only add nullable
  columns (`ModelPrices.CacheWriteInputPerMillionUsd`, `VirtualKeys.MaxConcurrentRequests`, `VirtualKeys.RequestPolicy` and
  `Teams.RequestPolicy`, jsonb); the previous version ignores them. One caveat: until every admin API instance runs the new
  version, an old instance cannot read usage records of the new endpoint `AnthropicCountTokens` (usage and PII pages may
  fail on it), so finish the rollout before clients use `/v1/messages/count_tokens`.
- `app-roles.sql` grants the Data API role `MaxConcurrentRequests` on `VirtualKeys`; the migration service applies it on
  its next start (the Data API keys feed gains `maxConcurrentRequests`).
- **Behaviour changes:** per-minute limits slide (a key that used its whole limit at the end of one minute waits into the
  next, and refused requests no longer count); the last candidate is retried once after a connection error or 502/503/504
  (`Gateway__Retry__MaxRetries=0` restores the old behaviour); OpenAI-format requests translated for Claude carry prompt-cache
  markers (`Gateway__PromptCaching=Off` restores the old behaviour; with Claude add a *Cache write* price, see below).
- **Redis:** the new keys `ume:cooldown:*` are inside the existing ACL (`~ume:*`); the cooldown script uses `set` and
  `pexpire` (already granted). Needs Redis 7 or later (`PEXPIRE … GT`); the shipped images use 8.6.
- **Behaviour change:** a 429/503 with `Retry-After` no longer counts toward the provider's circuit breaker; the deployment
  is put on cooldown instead (`CircuitBreaker__MaxRetryAfter=00:00:00` restores the old behaviour).
- Existing Claude prices bill cache writes at the input price, as before; add a price with *Cache write* to bill them
  at Anthropic's rate from its effective date.

## 0.6.4: security hardening

- **Rolling update: yes.** Two additive migrations (`ProviderFeaturePolicy`, `AuditActorContext`); 0.6.3 runs against the
  new schema while its instances are replaced.
- **Everyone is signed out once.** Admin sessions move to Redis (`ume:admin-session:*`, covered by the existing `~ume:*`
  ACL; commands `get`, `setex`, `psetex`, `del`). The ACL shipped with 0.6.4 and 0.7.0 lacked `setex`/`psetex`, so admin
  sign-in fails with a 500 (`RedisServerException`): redeploy with the current `deploy/redis/users.acl.example`. Old cookies are not accepted after the upgrade; users sign in
  again. During a rolling update a user can be signed out by an old instance once more.
- **New settings (defaults are fine):** `Admin:MaxSessionHours` (12) and `Gateway:AuthFailuresPerMinute` (60).
- **Provider address edits** need the credential again when the host, scheme or port changes. Scripts that move a provider
  with `PUT /api/providers/{id}` or a configuration import must send `credential` (or `credentialEnvironment`).
- **PII policy:** keys with *Block* or *Redact* may now refuse or change requests that passed before (personal data in the
  `user` field, a message `name`, an Anthropic text document, or written with full-width digits or invisible characters).
- **Console logs are JSON** outside development (all services). Log shippers and alerts that parse the plain-text format
  (`warn: Category[id]` lines) must read JSON, or set `Logging__Console__FormatterName=simple`.
- **Provider refusals are trimmed.** A provider's 4xx reaches the client with only `message`, `type`, `code` and `param`;
  clients that read other fields of a provider's error body no longer get them.
- **Repeated JSON properties** (`{"model":"a","model":"b"}`) get `400 invalid_request` instead of a `500`; on live audio
  such an event, or one that is not valid JSON, gets an `error` event instead of being passed to the provider.
- **API reference:** `/openapi/v1.json` and `/scalar` on the gateway and Data API stay public by default; turn them off with
  `Gateway:PublishApiReference=false` / `DataApi:PublishApiReference=false`. `/scalar` now has its own CSP.
- **Narrower database grants,** applied by the migration service on start (no manual step). The gateway role can no longer
  read `AuditLog` or `DataApiKeys` or add Data Protection keys; the admin role can no longer insert or delete the settings
  row or change `UsageRetainedFrom`. During a rolling update the old gateway version needs nothing it loses.
- **Internal CA:** new installations get an RFC 5280-conformant CA. On an existing Compose installation run
  `deploy/init-deployment.sh --renew-ca` once (it needs no other options) and restart the stack; Portainer replaces the CA
  on its own at the next redeploy. Integrations that trusted the old `ca.pem` must get the new one. Afterwards move
  `deploy/certs/ca.key` off the host.
- **Provider tools and storage are off for every key** (migration `ProviderFeaturePolicy` adds two columns, default
  `false`; rolling update still works). Clients that use web search, file search, code interpreter, MCP or other provider
  tools get `400 tool_not_allowed`, and `store: true`, `background` or `conversation` get `400 storage_not_allowed`, until
  an admin turns on *Allow provider tools* / *Allow provider storage* on the key. Responses API requests are now sent with
  `store: false`, so `previous_response_id` stops working for keys without provider storage. Check which applications
  rely on these before upgrading.
- **Audit entries record more** (migration `AuditActorContext` adds seven nullable columns to `AuditLog`; rolling update still
  works). New setting `Admin:AuditSourceAddress` (`Full`; `Truncated` or `None` if your privacy assessment requires).
  The admin API applies the gateway's trusted proxies to find the admin user's address. The Data API audit feed has the new
  fields, and its CSV has seven new columns at the end: consumers that read columns by position must allow for them.
- **New admin security events:** `admin.signin`, `admin.signout`, `admin.session.extended`, `admin.access.denied`,
  `admin.data.read`; exports are `admin.change` with action `export`. Route them in the collector like `admin.change`.

## 0.6.0: updates without downtime, budgets in USD

- **Rolling update: no** (renames budget columns 0.5.0 reads). Stop the gateway, admin API and Data API, migrate, start the
  new version. Later releases can use the rolling update described below.
- **Budgets are in USD.** The migration `BudgetsInUsd` renames `Budgets.LimitSek` to `LimitUsd` and `AlertEvents.SpentSek`
  / `LimitSek` to `SpentUsd` / `LimitUsd`, and converts the amounts: budget limits at the USD exchange rate in effect at
  migration time (so each budget allows what it allowed just before the upgrade; with no rate entered, the default
  fallback of 10 SEK per USD), stored alerts at the rate in effect when they were raised. Check the converted limits on
  *Budgets & alerts* and round them if you like. Spend is now summed from `CostUsd`.
- The gateway's Redis spend counters move from `ume:spend:*` (micro-SEK) to `ume:spend-usd:*` (micro-USD). The new
  counters are seeded from the usage records on first use; the old ones are never read again and expire with their
  period. No Redis ACL change is needed (`~ume:*`).
- **API changes.** Admin API budgets and alerts use `limitUsd` / `spentUsd` instead of `limitSek` / `spentSek`. The
  gateway adds `x-ume-cost-usd` and `x-ume-budget-remaining-usd`; `x-ume-cost-sek` and `x-ume-budget-remaining-sek` stay
  (converted at the current rate). The alert webhook adds `spentUsd` / `limitUsd` and keeps `spentSek` / `limitSek`. The
  Data API budgets feed adds `limitUsd` and keeps `limitSek` (converted at the current rate). A budget-exceeded message
  states the limit in USD.
- **Error messages are in English** in the gateway (`error.message`, realtime `error` events), the admin API and the
  Data API (`detail`, `title`, field errors). Error **codes** are unchanged; clients that match on message text must match
  the code instead.
- Every service now fails `/health/ready` from the moment it is told to stop and accepts the new setting
  `Shutdown__DrainSeconds` (default 0, no change). Load balancers that poll readiness see a stopping instance as down; a
  Compose `healthcheck` may briefly show a stopping container as unhealthy.
- New, opt-in: `compose.rolling.yaml` and `rolling-update.sh` for Docker Compose, and Kustomize manifests in
  `deploy/kubernetes`. A single-instance `compose.prod.yaml` installation needs no change.
- The first rolling update to this version still stops instances of 0.5.0, which do not drain; a few POSTs can fail at the
  moment each old instance stops.
- The admin API now asks `api.github.com` for the newest release (shown in *Settings > Version*), at most every 6 hours. Without
  outbound HTTPS it logs a warning and the card says it could not reach GitHub; set `Admin__CheckForReleases=false` to stop it.

## 0.5.0: request logging

- **Rolling update: yes** (from 0.4.1).
- **Successful requests are no longer written to the gateway log.** Refused, failed and cancelled requests still are, and
  every request is still a usage record. If dashboards or alerts count the "Request … status … in … ms" log lines, switch
  them to the usage data or the `ume.gateway.requests` metric, or turn on *Settings > Request logging*.
- The migration `RequestLogSwitch` adds `GatewaySettings.LogAllRequests` and `LogAllRequestsUntil`; it is additive, so the
  previous gateway and admin API keep working against the new schema. No grant changes.
- The usage writer now writes with `COPY`; it needs only the existing `INSERT` grant on `UsageRecords`.

## 0.4.1: MCP gateway removed

- **Rolling update: no** (drops columns 0.4.0 reads).
- The migration `RemoveMcpGateway` drops the MCP tables and columns of 0.4.0 (migration `McpGateway`) and deletes its
  history row. It is not expand/contract compatible with 0.4.0: a 0.4.0 gateway or admin API fails on the dropped columns,
  so stop them, migrate and start 0.4.1 together. On a database that never ran 0.4.0 it changes nothing.
- It also **deletes the rows of MCP traffic**: usage records, monthly totals and refused-key records with `Endpoint` `Mcp` or
  `McpToolCall`. 0.4.1 cannot read them, so they would break *Usage > Requests and costs* and *PII incidents*. Their cost no
  longer counts in usage reports or in budget counters rebuilt from Postgres. If you need the MCP calls for accounting,
  export them before upgrading: `SELECT * FROM "UsageRecords" WHERE "Endpoint" IN ('Mcp', 'McpToolCall')`.
- LLM requests that ran inside an MCP tool loop (`/v1/responses` turns) are ordinary records and are kept; only their
  `ParentRequestId` link is dropped.
- Redeploy the stack or run `init-deployment.sh` and restart Redis so the ACL no longer allows `ume:mcp:events`.

## 0.4.0: usage dashboard

- **Rolling update: yes** (from 0.3.x; all three 0.4.0 migrations are additive).
- The migration `RequestTimings` adds the nullable columns `UsageRecords.UpstreamMs` and `TimeToFirstTokenMs` (no table
  rewrite). It is additive: the previous gateway keeps writing records without them. The existing grants cover the new
  columns (the gateway inserts and the Data API reads the whole table).
- Records written before the upgrade have no provider time or time to first token, so the *Gateway overhead* and
  *Time to first token* cards fill in from the upgrade on.
- The Data API usage feed and `GET /api/usage/requests` have the new fields `upstreamMs` and `timeToFirstTokenMs`
  (`/api/usage/requests` also `costUsd`); clients that ignore unknown fields need no change.

## 0.4.0: data retention

- **Data older than 365 days is removed the first night after the upgrade** (usage records, refused-key records, ended
  budget alerts, audit entries), with monthly usage totals kept. If your records-retention decision says otherwise, set
  *Settings > Data retention* right after deploying, or deploy with `Retention__Enabled=false` on the admin API until it is set.
- The migration `DataRetention` adds retention columns to `GatewaySettings`, the table `UsageMonthlyTotals` and the function
  `ume_retention_step`; it is additive. Re-run the role grants (the migration service does it) so `ume_admin` may execute the
  function: until then runs fail with `SQL state 42501`, shown on the Settings page.

## 0.4.0: client address restrictions

- The migration `KeyIpRangesTrustedProxies` adds `VirtualKeys.AllowedIpRanges` (`text[]`, default empty) and the table
  `GatewaySettings`. It is additive: the previous gateway and admin API keep working against the new schema. `app-roles.sql`
  grants the admin role writes on `GatewaySettings` and the Data API role `SELECT` on the new column; the migration service
  applies it on start. Run the migration before starting the new Data API, whose key inventory reads the column.
- `UME_FORWARDED_HEADERS` / `ASPNETCORE_FORWARDEDHEADERS_ENABLED` no longer affect the gateway. Behind a reverse proxy, list
  the proxy under *Settings > Trusted proxies* after the upgrade, or refused keys are recorded with the proxy's address and
  keys given an address restriction are refused. Check the proxy sends `X-Forwarded-For` (see the runbook).
- New 403 error code `address_not_allowed` for clients of keys with an address restriction.

## 0.3.0: shared routing rules

- **Rolling update: no**.
- The migration `RoutingRuleScopeSets` converts `RoutingRules.ScopeId` into the set `ScopeIds` (`uuid[]`), drops
  `ScopeId`, adds `IsTemplate` and inserts four disabled template rules. It is not expand/contract compatible: gateways and
  the admin API of the previous version cannot read the new schema, so drain traffic, migrate and roll out both together.
- Admin API clients: send `scopeIds: [...]` instead of `scopeId` when creating, updating or reassigning a rule, and
  `{ scope, ruleIds }` to reorder. Responses carry `scopeIds` and `owners` instead of `scopeId`/`scopeName`.

## Upgrade procedure

1. Review release/source changes, package/image advisories, licenses and SBOM; approve exact
   application/base digests. Keep Aspire SDK/hosting packages on the 13.6 release family.
2. Stop the exact AppHost before local builds. Run all test suites, production deployment
   checks and accessibility/manual regression appropriate to the change.
3. Back up the database, pepper, Data Protection key ring/certificate and nonsecret config.
   Rehearse restoring the backup in isolation; record RPO/RTO.
4. Review migrations for expand/contract compatibility and mark the release *Rolling update: yes/no* above. For a rolling
   update use `deploy/rolling-update.sh` or `deploy/kubernetes/deploy.sh`; otherwise drain gateway traffic and run the
   one-shot migration/role-grant job with **only** migrator credentials. Do not give DDL to apps.
5. Deploy digest-pinned API images; verify health, schema/version, queue drain, OIDC/CSRF,
   role scoping and a synthetic inference request. Preserve credentials/key encryption material.
6. Observe error/fallback rates and spend reconciliation. Record release, digests, schema,
   test evidence and approval in change management.

Routing rules add the migrations `RoutingRules` (tables `RoutingRules`, `RoutingRuleTargets`) and
`RoutingRuleOnUsage` (two nullable columns on `UsageRecords`). Both are additive, so the previous gateway image keeps
working against the new schema. `deploy/postgres/app-roles.sql` now also grants the admin role write access to the
new tables; re-run the role-grant job with the migration.

Security events add the migrations `AuthFailures` (new table) and `FeedRecordedAt` (a `RecordedAt` column with a
database default on `UsageRecords`, `AuthFailures` and `AuditLog`; existing rows get the migration time). Both are
additive. `app-roles.sql` grants the gateway role `INSERT` on `AuthFailures`; re-run the role-grant job.

Speech to text and live audio add the migration `AudioTranscription`: `AudioSeconds` on `UsageRecords`, and
`AudioPerMinuteUsd`, `AudioInputPerMillionUsd` and `AudioOutputPerMillionUsd` on `ModelPrices`, all `NOT NULL DEFAULT 0`,
so it is additive and needs no new grants. After deploying, give the providers that serve speech models the *Speech to
text* capability, add the models with kind *Speech to text* (or rediscover them) and their per-minute or per-token
price, and create an alias such as `ume/transcribe`. If a proxy sits in front of the gateway, allow at least 32 MB
bodies and turn request buffering off (see the runbook).

Image generation (`/v1/images/generations`, `/v1/images/edits`) adds the migration `ImageGeneration`: `PerImageUsd` on
`ModelPrices`, `NOT NULL DEFAULT 0`, so it is additive and needs no new grants. After deploying, give the providers that serve
image models the *Image generation and editing* capability, add the models with kind *Image generation* (or rediscover them)
and their per-picture or per-token price, and create an alias such as `ume/image`. Image edits upload up to 50 MB: allow at
least 52 MB bodies at the proxy (`Gateway__MaxImageRequestBodyBytes`; raise both together).

Live audio (`/v1/realtime`, `/v1/realtime/translations`) needs three more steps:
- **Redis ACL.** The per-key session limit uses a sorted set: add `+zadd +zrem +zcard +zremrangebyscore` to the
  `gateway` user in `secrets/redis/users.acl` (the template `deploy/redis/users.acl.example` has them). Since 0.3.1 the
  stack's bootstrap and `init-deployment.sh` rewrite `users.acl` from the template on every start, so a redeploy (or
  running `init-deployment.sh` again) followed by a Redis restart is enough; before, existing installations kept their
  generated file and had to edit it by hand. Until then sessions work, but the
  limit is not enforced and the gateway logs a warning per session.
- **Proxy.** Pass the WebSocket upgrade: the `map $http_upgrade $ume_connection` block and the `Upgrade` /
  `Connection` headers in `deploy/proxy/nginx.conf.example`. Without them, clients get 400 (not a WebSocket request).
- **Providers.** Give the providers the *Live audio (realtime)* capability and add the models: speech-to-text models
  for live transcription, *Realtime (live)* models, *Live interpreting* models (`gpt-realtime-translate`), with their
  audio token or per-minute prices. Realtime models price audio tokens far above text tokens, so set the audio token
  prices or their cost is under-reported.

The control- and data-plane cleanup needs no migration, but check these before upgrading:
- **Startup validation.** The admin API refuses to start without `Oidc:Authority`, with `Admin:RequestsPerMinute`
  outside 1–10000, or with a `Gateway:BaseUrl`/`Gateway:OperationsUrl` that is not an absolute http(s) URL; the
  gateway validates `Gateway:FallbackSekPerUsd` (0.01–1000) and the migration service its `Seed:*` settings.
- **Exchange rate.** If no USD exchange rate has been entered, the gateway's readiness check `exchange-rate` now
  reports Degraded and each catalogue load logs a warning (costs still use `Gateway:FallbackSekPerUsd`, 10). Enter
  the rate under Settings.
- **Audit details.** New entries hold dedicated snapshots (camelCase, enum names, no derived fields); older entries
  keep their shape, so a consumer of `details` (Data API `security.read`, SIEM) must accept both.
- **CSV.** The admin usage export quotes cells only when needed (it quoted every text cell) and no longer prefixes
  numeric text such as `-1` with `'`; the Data API now also prefixes formulas after leading spaces or starting with a
  line feed.
- **Alert webhooks** are sent once, without retries, from their own queue.

### Adding the Data API to an existing installation

New installations get the read-only database role `ume_data` from `init.sql`. An existing database was initialised
before that, and creating a role needs the database superuser, so it is one manual step. The Data API is optional;
skip this until you want it.

1. Re-run `deploy/init-deployment.sh` (Portainer: redeploy the stack, which runs the bootstrap). Existing secrets are
   kept; it adds `secrets/postgres/data_password`, `secrets/dataapi/` and `certs/dataapi/`.
2. Create the role (idempotent; reads the password file inside the Postgres container and reloads `pg_hba.conf`):

   ```bash
   docker compose -f deploy/compose.prod.yaml --env-file deploy/.env exec -T postgres \
     sh -c 'PGPASSWORD=$(cat /run/postgres-secrets/bootstrap_password) psql -q -v ON_ERROR_STOP=1 -U postgres -d gatewaydb' \
     < deploy/postgres/add-data-role.sql
   ```

   The Postgres container must be running with the new `pg_hba.conf` (it lists `ume_data`); `docker compose up -d`
   with the updated files does that.
3. Re-run the migrations job so `app-roles.sql` grants the role its read access (it skips the grants while the role does
   not exist), then start the Data API: set `COMPOSE_PROFILES=data` in `deploy/.env` and run `docker compose ... up -d
   --wait`. If the Data API was started before the role existed, give its health check a few seconds to recover.

Schema-breaking changes require an explicit downtime/rollback plan. Image rollback is safe
only if the previous app supports the new schema. Do not automatically run destructive
down-migrations; restore an approved backup in isolation first. Redis counters reconstructed
after restart depend on committed usage and can lag in-flight requests.

## Certificate and secret rotation

Renew TLS certificates with appropriate SANs/CA, stage trust changes, restart the exact
services, verify strict validation. Rotate Redis ACL/client passwords together. Update
existing Postgres role passwords through an approved DBA workflow as well as secret files.
Keep old Data Protection decrypting certificates available during re-encryption/key-ring
transition; replacing the encryption certificate blindly loses provider credentials/cookies.
HMAC pepper rotation needs a designed key-hash migration or explicit replacement/revocation
of all virtual keys; the gateway does not support transparent dual-pepper rotation.

## Dependency and supply-chain commands

```powershell
dotnet list UmeLlmGateway.slnx package --vulnerable --include-transitive
Set-Location src\admin-ui
npm audit --audit-level=low
Set-Location ..\..\tests\e2e
npm audit --audit-level=low
```

Generate an SPDX SBOM with the approved Microsoft `sbom-tool` version against each publish
directory; review package/image scans and provenance before promoting images. SBOM inputs
must exclude secret/certificate mounts, test results and developer environments. Dependency
audit success is time-bound, not proof of absence of vulnerabilities; container OS/package
scanning and signed artifact attestations remain municipal release responsibilities.
