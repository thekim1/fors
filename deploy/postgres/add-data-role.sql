-- Adds the read-only Data API role to an existing installation (new installations get it from init.sql).
-- Run as the database superuser after init-deployment.sh (or the Portainer bootstrap) has created
-- secrets/postgres/data_password; see docs/upgrade-guide.md. Safe to run more than once.
SET log_statement = 'none';
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'fors_data') THEN
    EXECUTE format('CREATE ROLE fors_data LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
      btrim(pg_read_file('/run/postgres-secrets/data_password'), E'\r\n '));
  END IF;
END $$;
GRANT CONNECT ON DATABASE gatewaydb TO fors_data;
GRANT USAGE ON SCHEMA public TO fors_data;
-- pg_hba.conf now lists fors_data; reload so the new line applies without a restart.
SELECT pg_reload_conf();
