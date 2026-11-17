"""Give eneo the support knowledge, as an administrator would through eneo's API, and print
"<assistant id> <api key> <chunks>" on the last line.

Runs inside the eneo-backend container (run.sh), as the default user (Owner):

1. a shared space "Support" (it gets the default models of seed_models.py);
2. a collection on the embedding model, and every PDF in /pdfs uploaded into it; waits until the worker has parsed and
   embedded each one (the job is complete), and fails on a failed job;
3. an assistant on glm-5.3-flash with the collection, knowledge injected into the prompt (eneo's default);
4. a service API key (sk_, ownership service) scoped to the assistant, rate limit raised so it is checked but never hit.
"""

import os
import pathlib
import sys
import time
from datetime import datetime, timedelta, timezone

import httpx

BASE = "http://localhost:8000/api/v1"
PDFS = sorted(pathlib.Path("/pdfs").glob("*.pdf"))
TIMEOUT_S = int(os.environ.get("INGEST_TIMEOUT_S", "1800"))


def ok(response: httpx.Response) -> httpx.Response:
    if response.is_error:
        sys.exit(f"{response.request.method} {response.request.url} -> {response.status_code} {response.text[:500]}")
    return response


with httpx.Client(base_url=BASE, timeout=120) as client:
    login = ok(client.post("/users/login/token/", data={"username": os.environ["DEFAULT_USER_EMAIL"], "password": os.environ["DEFAULT_USER_PASSWORD"]}))
    client.headers["Authorization"] = f"Bearer {login.json()['access_token']}"

    space = ok(client.post("/spaces/", json={"name": "Support"})).json()
    space_id = space["id"]
    models = ok(client.get(f"/spaces/{space_id}/")).json()
    embedding = (models.get("embedding_models") or [None])[0]
    completion = next((m for m in models.get("completion_models") or [] if m.get("name") == os.environ.get("CHAT_MODEL", "glm-5.3-flash")), None)
    if embedding is None or completion is None:
        sys.exit(f"space is missing the seeded models: {models.get('embedding_models')} {models.get('completion_models')}")

    group = ok(client.post(f"/spaces/{space_id}/knowledge/groups/", json={"name": "Vanliga fel", "embedding_model": {"id": embedding["id"]}})).json()
    print(f"[knowledge] {len(PDFS)} PDFs into collection {group['id']} ({embedding['name']})", flush=True)

    jobs = {}
    for pdf in PDFS:
        with pdf.open("rb") as f:
            job = ok(client.post(f"/groups/{group['id']}/info-blobs/upload/", files={"file": (pdf.name, f, "application/pdf")})).json()
        jobs[job["id"]] = pdf.name

    started = time.monotonic()
    pending = dict(jobs)
    while pending:
        if time.monotonic() - started > TIMEOUT_S:
            sys.exit(f"ingestion timed out; still pending: {sorted(pending.values())}")
        for job_id in list(pending):
            status = ok(client.get(f"/jobs/{job_id}/")).json()
            if status["status"] == "complete":
                pending.pop(job_id)
            elif status["status"] == "failed":
                sys.exit(f"ingestion failed for {pending[job_id]}: {status}")
        time.sleep(2)
    print(f"[knowledge] embedded in {time.monotonic() - started:.0f} s", flush=True)

    info = ok(client.get(f"/groups/{group['id']}/")).json()
    assistant = ok(client.post("/assistants/", json={"name": "Supportassistent", "space_id": space_id})).json()
    ok(client.post(f"/assistants/{assistant['id']}/", json={
        "groups": [{"id": group["id"]}],
        "completion_model": {"id": completion["id"]},
        "knowledge_mode": "inject",
    }))

    key = ok(client.post("/api-keys", json={
        "name": "Load test service key",
        "key_type": "sk_",
        "permission": "write",
        "scope_type": "assistant",
        "scope_id": assistant["id"],
        "ownership": "service",
        "rate_limit": 100_000_000,
        "expires_at": (datetime.now(timezone.utc) + timedelta(days=90)).isoformat(),
    })).json()
    print(f"[knowledge] {info.get('metadata', {}).get('num_info_blobs')} documents in the collection", flush=True)
    print(assistant["id"], key["secret"], flush=True)
