#!/usr/bin/env python3
"""Give the RAG platform the support knowledge and an assistant to chat with; print "<model> <api key> <chunks>" last.

Run by run.sh on the host, against the rag-db container (docker exec psql), after the migrator:

1. the HNSW session settings production gives its roles (deploy/postgres/init.sql), which Staging doesn't apply;
2. rag-b47/scripts/dev/seed-url-list.sql: a department, a public collection on the embedding profile, a URL-list source
   with every PDF of PDF_DIR as served by the pdf-site container, the public level's policy, an internal client, and a
   manual sync job (the worker fetches, parses, chunks and embeds through the gateway);
3. seed_assistant.sql: the published assistant "support" on the collection, its model limits and a chat key; the
   collection's coverage floor lifted (fake vectors carry no meaning, see below);
4. waits until no sync, fetch, parse or embed job is queued or running, and fails on failed jobs.

Standard library only.
"""

import hashlib
import hmac
import os
import pathlib
import secrets
import subprocess
import sys
import time
import urllib.parse

HERE = pathlib.Path(__file__).resolve().parent
RAG_REPO = pathlib.Path(os.environ.get("RAG_REPO", HERE.parents[3] / "rag-b47"))
PDF_DIR = pathlib.Path(os.environ["PDF_DIR"])
PEPPER = os.environ.get("RAG_PEPPER", "knowledge-load-rag-pepper-0123456789abcdef0123")
MODEL = os.environ.get("RAG_MODEL", "ume/chat")
CONTEXT_WINDOW = int(os.environ.get("CONTEXT_WINDOW", "1000000"))
MAX_OUTPUT = int(os.environ.get("MAX_OUTPUT", "2048"))
DB = os.environ.get("RAG_DB_CONTAINER", "knowledge-load-rag-db-1")
TIMEOUT_S = int(os.environ.get("INGEST_TIMEOUT_S", "3600"))
ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"


def psql(sql: str, variables: dict[str, str] | None = None) -> str:
    args = ["docker", "exec", "-i", DB, "psql", "-U", "postgres", "-d", "ragdb", "-v", "ON_ERROR_STOP=1", "-qtA"]
    for name, value in (variables or {}).items():
        args += ["-v", f"{name}={value}"]
    done = subprocess.run(args, input=sql, capture_output=True, text=True)
    if done.returncode != 0:
        sys.exit(f"psql failed: {done.stderr.strip()}")
    return done.stdout.strip()


def literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


# 1. Retrieval as in production (init.sql sets these per role; Staging runs as the superuser).
psql("""
ALTER DATABASE ragdb SET hnsw.ef_search = 100;
ALTER DATABASE ragdb SET hnsw.iterative_scan = relaxed_order;
ALTER DATABASE ragdb SET hnsw.max_scan_tuples = 100000;
""")

# 2. The collection and its URL-list source, by the repo's own dev seed.
pdfs = sorted(PDF_DIR.glob("*.pdf"))
if not pdfs:
    sys.exit(f"no PDFs in {PDF_DIR}")
urls = "[" + ",".join(f'"http://pdf-site/{urllib.parse.quote(p.name)}"' for p in pdfs) + "]"
settings = {
    "ume_seed.urls": urls,
    "ume_seed.profile": "pilot-embedding",
    "ume_seed.department_name": "IT-support",
    "ume_seed.collection_slug": "vanliga-fel",
    "ume_seed.collection_name": "Vanliga fel",
    "ume_seed.source_name": "Vanliga fel (PDF)",
    "ume_seed.pii_mode": "report",
    "ume_seed.enqueue": "true",
    "ume_seed.client_name": "Supportverktyget",
}
prelude = "".join(f"SELECT set_config({literal(k)}, {literal(v)}, false);\n" for k, v in settings.items())
seed_sql = (RAG_REPO / "scripts" / "dev" / "seed-url-list.sql").read_text(encoding="utf-8")
print(psql(prelude + seed_sql).splitlines()[-1], flush=True)

# The fake LLM's vectors are hashed words, not meaning: no chunk comes within the coverage floor (CoverageMaxDistance
# 0.50), so every question would get the not-found text without a model call. Real embeddings find the matching guide
# for these on-topic tickets; lifting the floor for this collection gives that case: the normal top-K, a model call.
psql(f"""UPDATE config."Collections" SET "RetrievalSettings" = '{{"coverageMaxDistance": 2.0}}'
         WHERE "Slug" = {literal(settings['ume_seed.collection_slug'])};""")

# The client is created only together with a key in seed-url-list.sql; make sure it exists for the chat key.
psql(f"""
INSERT INTO config."Clients" ("Id", "Name", "Kind", "ClassificationCeiling", "StoresHistory", "AllowedOrigins", "ToolPolicy", "IsEnabled",
                              "UpdatedAt", "UpdatedBy")
SELECT gen_random_uuid(), {literal(settings['ume_seed.client_name'])}, 'internal', 'public', false, '{{}}', 'none', true, now(), 'knowledge-load'
WHERE NOT EXISTS (SELECT 1 FROM config."Clients" WHERE "Name" = {literal(settings['ume_seed.client_name'])});
INSERT INTO config."DataProtectionPolicies" ("Classification", "AllowedHistoryModes", "DefaultHistoryMode", "RetentionDefaultDays",
    "RetentionMaxDays", "EncryptionRequired", "ReviewSecondApprover", "ClientsWithoutHistoryOnly", "BlobMasterKeyName", "UpdatedAt", "UpdatedBy")
VALUES ('public', ARRAY['none']::varchar(20)[], 'none', 90, 90, false, false, false, NULL, now(), 'knowledge-load')
ON CONFLICT ("Classification") DO NOTHING;
""")

# 3. The assistant and its chat key (KeyHasher: "ume-rag-sk-" + 43 base62, HMAC-SHA256 under the pepper).
key = "ume-rag-sk-" + "".join(secrets.choice(ALPHABET) for _ in range(43))
print(psql((HERE / "seed_assistant.sql").read_text(encoding="utf-8"), {
    "collection_slug": settings["ume_seed.collection_slug"],
    "client_name": settings["ume_seed.client_name"],
    "model": MODEL,
    "context_window": str(CONTEXT_WINDOW),
    "max_output": str(MAX_OUTPUT),
    "key_hash": hmac.new(PEPPER.encode(), key.encode(), hashlib.sha256).hexdigest(),
    "key_prefix": key[:15],
}).splitlines()[-1], flush=True)

# 4. Ingestion: until no job is open twice in a row (benchmarks/release-test/ingest.sh).
OPEN = """SELECT count(*) FROM content."Jobs" WHERE "State" IN ('queued','running') AND "Class" IN ('sync','fetch','parse','embed')"""
started = time.monotonic()
idle = 0
while idle < 2:
    if time.monotonic() - started > TIMEOUT_S:
        sys.exit("ingestion timed out")
    idle = idle + 1 if psql(OPEN) == "0" else 0
    time.sleep(5)
failed = psql("""SELECT "Class" || ' ' || "State" || ': ' || count(*) FROM content."Jobs" WHERE "State" NOT IN ('done','succeeded','completed','queued','running')
                 GROUP BY "Class", "State" ORDER BY 1""")
chunks = psql("""SELECT count(*) FROM content."Chunks" """) if psql("""SELECT to_regclass('content."Chunks"') IS NOT NULL""") == "t" else "?"
print(f"[rag] ingested in {time.monotonic() - started:.0f} s, {chunks} chunks; jobs not done: {failed or 'none'}", flush=True)
print("support", key, chunks, flush=True)
