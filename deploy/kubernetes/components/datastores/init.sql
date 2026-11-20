SET log_statement = 'none';
DO $$
BEGIN
  EXECUTE format('CREATE ROLE fors_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
    btrim(pg_read_file('/run/postgres-secrets/migrator_password'), E'\r\n '));
  EXECUTE format('CREATE ROLE fors_gateway LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
    btrim(pg_read_file('/run/postgres-secrets/gateway_password'), E'\r\n '));
  EXECUTE format('CREATE ROLE fors_admin LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
    btrim(pg_read_file('/run/postgres-secrets/admin_password'), E'\r\n '));
  -- Read-only role for the optional Data API (docs/data-access.md); only when a password was provisioned.
  -- Test one field: a record IS NOT NULL only when every field is, and some are always null on Linux.
  IF (pg_stat_file('/run/postgres-secrets/data_password', true)).size IS NOT NULL THEN
    EXECUTE format('CREATE ROLE fors_data LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
      btrim(pg_read_file('/run/postgres-secrets/data_password'), E'\r\n '));
  END IF;
  -- Optional Keycloak (deploy/keycloak, docs/keycloak.md): its own role and database, only when provisioned.
  IF (pg_stat_file('/run/postgres-secrets/keycloak_password', true)).size IS NOT NULL THEN
    EXECUTE format('CREATE ROLE fors_keycloak LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L',
      btrim(pg_read_file('/run/postgres-secrets/keycloak_password'), E'\r\n '));
  END IF;
END $$;
SELECT 'CREATE DATABASE keycloak OWNER fors_keycloak' WHERE EXISTS (SELECT FROM pg_roles WHERE rolname = 'fors_keycloak')\gexec
CREATE DATABASE gatewaydb OWNER fors_migrator;
REVOKE ALL ON DATABASE gatewaydb FROM PUBLIC;
GRANT CONNECT ON DATABASE gatewaydb TO fors_gateway, fors_admin;
\connect gatewaydb
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO fors_migrator;
GRANT USAGE ON SCHEMA public TO fors_gateway, fors_admin;
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'fors_data') THEN
    GRANT CONNECT ON DATABASE gatewaydb TO fors_data;
    GRANT USAGE ON SCHEMA public TO fors_data;
  END IF;
END $$;
