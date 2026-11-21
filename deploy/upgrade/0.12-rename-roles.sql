-- Fors 0.12.0: renames the database roles from before the rename to Fors (ume_*) to fors_* (docs/upgrade-guide.md).
-- Runs as the Postgres superuser: the Compose forms' one-shot service `roles` runs it before `migrations` on every start
-- (from 0.12.2); Kubernetes and a manual fallback run it with psql. Idempotent: a role already renamed, or never created
-- (Data API, Keycloak), is skipped, so on a new installation and on every later start it does nothing.
-- The sessions of a role are ended before it is renamed, so a service of the old version still connected under the old
-- name does not keep working against the new schema; it cannot sign in again, because the name is gone.
-- SCRAM passwords survive the rename, so the services' passwords stay the same. Grants, ownership and the databases
-- follow the role; the retention function is renamed by the migration ForsRename.
DO $$
DECLARE
  r text;
BEGIN
  FOREACH r IN ARRAY ARRAY['migrator', 'gateway', 'admin', 'data', 'keycloak'] LOOP
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ume_' || r)
       AND NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'fors_' || r) THEN
      PERFORM pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = 'ume_' || r;
      EXECUTE format('ALTER ROLE %I RENAME TO %I', 'ume_' || r, 'fors_' || r);
      RAISE NOTICE 'renamed role ume_% to fors_%', r, r;
    END IF;
  END LOOP;
END $$;
