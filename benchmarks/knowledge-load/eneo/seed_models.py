"""Seed the eneo tenant with glm-5.3-flash and an embedding model, both served by the fake LLM.

As benchmarks/compare/eneo/seed.py (adapted from eneo's own e2e/seed.py), for eneo v2.3.1 with knowledge:

- the completion model "glm-5.3-flash" with production's context window, 1 000 000 tokens. eneo injects up to
  max_input_tokens / CHUNK_SIZE / 2 chunks of knowledge (assistants/references.py), so this decides the prompt size;
- the embedding model "fake-embed-1024": the fake LLM gives 1024-dimension vectors for that name (its `-<n>` suffix).
  `dimensions` stays unset: LiteLLM refuses it on an "openai" provider except for text-embedding-3.

Created before any space, so a new space gets both. Plaintext credentials: ENCRYPTION_KEY unset and
TENANT_CREDENTIALS_ENABLED=false, as eneo's e2e stack. Idempotent.
"""

import asyncio
import os

from sqlalchemy import select

from eneo.database.database import sessionmanager
from eneo.database.tables.ai_models_table import CompletionModels, EmbeddingModels
from eneo.database.tables.model_providers_table import ModelProviders
from eneo.database.tables.tenant_table import Tenants
from eneo.main.config import get_settings

ENDPOINT = os.environ.get("FAKE_LLM_ENDPOINT", "http://fake-llm:8080/v1")
CHAT_MODEL = os.environ.get("CHAT_MODEL", "glm-5.3-flash")
CONTEXT_WINDOW = int(os.environ.get("CONTEXT_WINDOW", "1000000"))
EMBEDDING_MODEL = os.environ.get("EMBEDDING_MODEL", "fake-embed-1024")
TENANT_NAME = os.environ["DEFAULT_TENANT_NAME"]
PROVIDER_NAME = "Fake LLM"


async def main() -> None:
    sessionmanager.init(get_settings().database_url)
    async with sessionmanager.session() as session, session.begin():
        tenant_id = (await session.execute(select(Tenants.id).where(Tenants.name == TENANT_NAME))).scalar_one()
        if (
            await session.execute(
                select(ModelProviders.id).where(ModelProviders.tenant_id == tenant_id, ModelProviders.name == PROVIDER_NAME)
            )
        ).scalar_one_or_none():
            print("[seed] models already present, skipping", flush=True)
            return

        provider = ModelProviders(
            tenant_id=tenant_id,
            name=PROVIDER_NAME,
            provider_type="openai",
            credentials={"api_key": "fake-key", "endpoint": ENDPOINT},
            config={"endpoint": ENDPOINT},
            is_active=True,
        )
        session.add(provider)
        await session.flush()

        session.add(
            CompletionModels(
                name=CHAT_MODEL,
                nickname="GLM 5.3 Flash (fake)",
                max_input_tokens=CONTEXT_WINDOW,
                max_output_tokens=2048,
                family="openai",
                stability="stable",
                hosting="usa",
                org="OpenAI",
                vision=False,
                reasoning=False,
                supports_tool_calling=False,  # knowledge is injected, not searched with a tool
                base_url=ENDPOINT,
                litellm_model_name=CHAT_MODEL,
                tenant_id=tenant_id,
                provider_id=provider.id,
                is_enabled=True,
                is_default=True,
            )
        )
        session.add(
            EmbeddingModels(
                name=EMBEDDING_MODEL,
                nickname="Fake embedding (1024)",
                open_source=False,
                dimensions=None,
                max_input=8191,
                family="openai",
                stability="stable",
                hosting="usa",
                org="OpenAI",
                tenant_id=tenant_id,
                provider_id=provider.id,
                is_enabled=True,
                is_default=True,
            )
        )
        print(f"[seed] provider, {CHAT_MODEL} ({CONTEXT_WINDOW} tokens) and {EMBEDDING_MODEL}", flush=True)


if __name__ == "__main__":
    asyncio.run(main())
