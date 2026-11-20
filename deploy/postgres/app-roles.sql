-- Applied by the migration service on every start. The service roles' table privileges are revoked and granted again in
-- one transaction, so each role holds exactly what this file lists (a privilege removed here is removed from existing
-- installations too) and running services see either the old or the new grants, never none. Tables are named one by
-- one: a new table is readable by no service until it is added here (DatabaseRoleTests checks the effective grants).
-- No curly braces: the migration service runs this file with EF Core's ExecuteSqlRaw, which reads them as placeholders.
BEGIN;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM fors_gateway, fors_admin;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM fors_gateway, fors_admin;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO fors_gateway, fors_admin;

-- The settings row exists before any service runs, so the admin role needs no INSERT on it (an insert could set
-- UsageRetainedFrom, which only the retention function may move).
INSERT INTO "GatewaySettings" ("Id", "TrustedProxies", "AuditRetentionDays") VALUES (1, ARRAY[]::text[], 365) ON CONFLICT ("Id") DO NOTHING;
-- The same for the admin console's branding (organisation name and logo; the name's default is the column default).
INSERT INTO "Branding" ("Id") VALUES (1) ON CONFLICT ("Id") DO NOTHING;
-- And for the admin console's stored sign-in values (all unset until setup mode stores them).
INSERT INTO "SignInSettings" ("Id", "ExtraScopes") VALUES (1, ARRAY[]::text[]) ON CONFLICT ("Id") DO NOTHING;

-- Gateway (faces the clients): what it authenticates, routes and accounts with. Not the audit log or Data API keys. It reads
-- the Data Protection key ring but does not add keys (it only decrypts; the admin API creates and rotates them).
GRANT SELECT ON "Departments", "Teams", "VirtualKeys", "Integrations", "ProviderAccounts", "ModelDeployments", "ModelPrices",
  "RouteAliases", "RouteTargets", "RoutingRules", "RoutingRuleTargets", "Budgets", "GatewaySettings",
  "UsageRecords", "UsageMonthlyTotals", "AlertEvents", "AuthFailures", "DataProtectionKeys" TO fors_gateway;
GRANT INSERT ON "UsageRecords", "AlertEvents", "AuthFailures" TO fors_gateway;
GRANT UPDATE ("LastUsedAt") ON "VirtualKeys" TO fors_gateway;
-- The organisation's name for integrations' context; never the logo.
GRANT SELECT ("OrganisationName") ON "Branding" TO fors_gateway;
-- The sign-in provider's address for integrations' context; never the client id or the encrypted client secret.
GRANT SELECT ("Authority") ON "SignInSettings" TO fors_gateway;

-- Admin API: reads everything it shows (and the schema version for Operations); changes configuration, appends audit
-- entries, creates Data Protection keys. Settings column by column, without UsageRetainedFrom.
GRANT SELECT ON "Departments", "Teams", "VirtualKeys", "Integrations", "DataApiKeys", "ProviderAccounts", "ModelDeployments", "ModelPrices",
  "RouteAliases", "RouteTargets", "RoutingRules", "RoutingRuleTargets", "Budgets", "GatewaySettings",
  "UsageRecords", "UsageMonthlyTotals", "AuditLog", "AlertEvents", "AuthFailures", "DataProtectionKeys",
  "Branding", "SignInSettings", "SetupSessions", "SetupTests", "__EFMigrationsHistory" TO fors_admin;
GRANT INSERT, UPDATE, DELETE ON "Departments", "Teams", "VirtualKeys", "Integrations", "ProviderAccounts",
  "ModelDeployments", "ModelPrices", "RouteAliases", "RouteTargets", "RoutingRules", "RoutingRuleTargets",
  "Budgets", "DataApiKeys" TO fors_admin;
GRANT UPDATE ("TrustedProxies", "LogAllRequests", "LogAllRequestsUntil", "UpdatedAt", "UsageRetentionDays", "AuditRetentionDays",
  "RetentionLastRunAt", "RetentionLastRunDeleted", "RetentionLastRunError") ON "GatewaySettings" TO fors_admin;
GRANT UPDATE ("OrganisationName", "Logo", "LogoContentType", "UpdatedAt") ON "Branding" TO fors_admin;
GRANT UPDATE ("Authority", "ClientId", "RoleClaim", "DepartmentClaim", "TeamClaim", "GroupClaim", "ExtraScopes", "EncryptedClientSecret",
  "ClientSecretSetAt", "ClientSecretExpiresOn", "UpdatedAt", "SetupCompletedAt", "SetupNonce", "SetupOpenedAt", "SetupReopenConsumed")
  ON "SignInSettings" TO fors_admin;
-- Setup mode's short-lived sessions and test sign-ins (deleted when their opening ends).
GRANT INSERT, UPDATE, DELETE ON "SetupSessions", "SetupTests" TO fors_admin;
GRANT INSERT ON "AuditLog", "DataProtectionKeys" TO fors_admin;
GRANT UPDATE ("Acknowledged") ON "AlertEvents" TO fors_admin;
-- Data retention: old rows are removed only through this function (owner's rights, minimum 90 days); the admin role
-- has no DELETE on usage, refused-credential, alert or audit rows.
GRANT EXECUTE ON FUNCTION fors_retention_step(integer) TO fors_admin;
-- Data API (optional): read-only, and column by column where a table holds secrets, so the role cannot
-- read key hashes, encrypted key secrets or provider credentials even through a bug. Skipped while the
-- role does not exist (it is created by init.sql on new installs; see docs/upgrade-guide.md for existing ones).
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'fors_data') THEN
    GRANT CONNECT ON DATABASE gatewaydb TO fors_data;
    GRANT USAGE ON SCHEMA public TO fors_data;
    GRANT SELECT ON "UsageRecords", "AuthFailures", "AuditLog", "Departments", "Teams", "ModelDeployments",
      "ModelPrices", "Budgets", "UsageMonthlyTotals" TO fors_data;
    GRANT SELECT ("UsageRetainedFrom") ON "GatewaySettings" TO fors_data;
    GRANT SELECT ("Id", "TeamId", "Name", "Description", "Prefix", "IsEnabled", "CreatedAt", "CreatedBy", "ExpiresAt",
      "RevokedAt", "GraceUntil", "RotatedToKeyId", "LastUsedAt", "AllowedModels", "AllowedResidencies", "AllowedProviders",
      "AllowedIpRanges", "PiiPolicy", "AttachmentPolicy", "RequestsPerMinute", "TokensPerMinute", "AllowProviderTools",
      "AllowProviderStorage", "MaxConcurrentRequests", "IntegrationId", "IntegrationRole") ON "VirtualKeys" TO fors_data;
    GRANT SELECT ("Id", "Name", "DisplayName", "Type", "Residency", "IsEnabled") ON "ProviderAccounts" TO fors_data;
    -- Its own credentials: the key hash (SHA-256 of a 256-bit random key) to authenticate Data API keys, and the
    -- only column it may write, the approximate last use.
    GRANT SELECT ("Id", "Name", "Prefix", "KeyHash", "Permissions", "ExpiresAt", "RevokedAt", "LastUsedAt") ON "DataApiKeys" TO fors_data;
    GRANT UPDATE ("LastUsedAt") ON "DataApiKeys" TO fors_data;
  END IF;
END $$;
COMMIT;
