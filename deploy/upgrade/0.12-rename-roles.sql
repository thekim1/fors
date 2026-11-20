-- Fors 0.12.0: renames the database roles from before the rename to Fors (ume_*) to fors_* (docs/upgrade-guide.md).
-- Run once as the Postgres superuser, with the gateway, admin API, Data API and Keycloak stopped (a role that is
-- connected cannot be renamed). Idempotent: a role already renamed, or never created (Data API, Keycloak), is skipped.
-- SCRAM passwords survive the rename, so the services' passwords stay the same. Grants, ownership and the databases
-- follow the role; the retention function is renamed by the migration ForsRename.
DO $$
DECLARE
  r text;
BEGIN
  FOREACH r IN ARRAY ARRAY['migrator', 'gateway', 'admin', 'data', 'keycloak'] LOOP
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'ume_' || r)
       AND NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'fors_' || r) THEN
      EXECUTE format('ALTER ROLE %I RENAME TO %I', 'ume_' || r, 'fors_' || r);
      RAISE NOTICE 'renamed role ume_% to fors_%', r, r;
    END IF;
  END LOOP;
END $$;
