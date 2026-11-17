-- The RAG platform's chat assistant on the support collection, as an admin's publish leaves it (rag-api reads these rows
-- in PolicySnapshotStore.LoadAsync): an assistant with a published version that links the collection, its answer model,
-- the client of seed-url-list.sql as its audience, the model's limits, and a secret key of that client scoped to the
-- assistant. Run after rag-b47/scripts/dev/seed-url-list.sql, in the same database, by run.sh (psql variables below).
-- The admin API would do the same, but needs an OIDC sign-in; seeding by SQL is the repo's own dev path.
--
--   :collection_slug   the collection seed-url-list.sql made
--   :client_name       its client
--   :model             the gateway alias the assistant answers with
--   :context_window    the model's context window (glm-5.3-flash: 1 000 000)
--   :max_output        the model's max output tokens
--   :key_hash          hex HMAC-SHA256 of the key under Security:KeyPepper
--   :key_prefix        the key's first 15 characters

BEGIN;
-- The admin's config write: the version lock first, so the change is one config version.
SELECT 1 FROM config."ConfigVersion" WHERE "Id" = 1 FOR UPDATE;

-- The answer model's limits (chat design §6.3): the knowledge budget and the answer's max tokens come from them.
INSERT INTO config."GatewayModels" ("Name", "Kind", "Residencies", "Source", "SupportsTools", "SupportsToolsSource", "ContextWindow",
                                    "MaxOutputTokens", "ModelKind", "LimitsSource", "SyncedAt")
VALUES (:'model', 'route', ARRAY['Eu']::varchar(10)[], 'manual', false, 'manual', :context_window, :max_output, 'chat', 'manual', now())
ON CONFLICT ("Name") DO UPDATE SET "ContextWindow" = EXCLUDED."ContextWindow", "MaxOutputTokens" = EXCLUDED."MaxOutputTokens";

CREATE TEMP TABLE seed_ids ON COMMIT DROP AS
SELECT gen_random_uuid() AS assistant_id, gen_random_uuid() AS version_id, gen_random_uuid() AS key_id,
       c."Id" AS collection_id, c."DepartmentId" AS department_id,
       (SELECT k."Id" FROM config."Clients" k WHERE k."Name" = :'client_name') AS client_id
FROM config."Collections" c WHERE c."Slug" = :'collection_slug';

INSERT INTO config."Assistants" ("Id", "Slug", "Name", "Description", "DepartmentId", "Classification", "HistoryMode", "RetentionDays",
                                 "AllowedOrigins", "State", "UpdatedAt", "UpdatedBy")
SELECT assistant_id, 'support', 'Supportassistent', 'Load test: the IT support assistant on Vanliga fel', department_id, 'public', 'none',
       90, '{}', 'active', now(), 'knowledge-load'
FROM seed_ids;

INSERT INTO config."AssistantVersions" ("Id", "AssistantId", "Number", "State", "GatewayModel", "SystemPrompt", "QuerySteps", "NotFound",
                                        "CollectionLinks", "CreatedBy", "CreatedAt", "UpdatedAt", "UpdatedBy", "PublishedBy", "PublishedAt")
SELECT version_id, assistant_id, 1, 'published', :'model',
       'Du är en supportassistent för kommunens IT-support. Svara utifrån källorna.', '{}', '{}',
       jsonb_build_array(jsonb_build_object('collectionId', collection_id, 'tags', '[]'::jsonb)),
       'knowledge-load', now(), now(), 'knowledge-load', 'knowledge-load', now()
FROM seed_ids;

UPDATE config."Assistants" a SET "PublishedVersionId" = s.version_id FROM seed_ids s WHERE a."Id" = s.assistant_id;

INSERT INTO config."AssistantAudiences" ("Id", "AssistantId", "Kind", "SubjectId")
SELECT gen_random_uuid(), assistant_id, 'client', client_id FROM seed_ids;

INSERT INTO config."Keys" ("Id", "ClientId", "Kind", "Name", "Description", "Prefix", "KeyHash", "Scopes", "AllowedOrigins", "AllowedIpRanges",
                           "IsEnabled", "CreatedAt", "CreatedBy", "ExpiresAt")
SELECT key_id, client_id, 'sk', 'Load test chat key', 'Chat with the support assistant', :'key_prefix', :'key_hash',
       jsonb_build_object('assistantIds', jsonb_build_array(assistant_id), 'collectionIds', '[]'::jsonb), '{}', '{}',
       true, now(), 'knowledge-load', now() + interval '30 days'
FROM seed_ids;

SELECT 'assistant ' || assistant_id || ' (slug support) on collection ' || collection_id || ', client ' || client_id FROM seed_ids;
COMMIT;
