"""Supabase helpers for retrieving deck artifacts using supabase-py."""
from __future__ import annotations

import json
import os
from dataclasses import dataclass
from typing import Any, Dict

from dotenv import load_dotenv
from supabase import Client, create_client

# Load .env when this module is imported so standalone scripts work.
load_dotenv()


@dataclass
class SupabaseConfig:
    url: str
    service_role_key: str
    decks_table: str
    storage_bucket: str


_config_cache: SupabaseConfig | None = None
_client_cache: Client | None = None


def load_supabase_config() -> SupabaseConfig:
    global _config_cache
    if _config_cache is not None:
        return _config_cache

    url = os.environ.get("SUPABASE_URL")
    key = os.environ.get("SUPABASE_SERVICE_ROLE_KEY")
    table = os.environ.get("SUPABASE_DECKS_TABLE", "decks")
    bucket = os.environ.get("SUPABASE_STORAGE_BUCKET", "decks")

    if not url or not key or not bucket:
        raise RuntimeError(
            "Supabase storage is not configured. Set SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY, and SUPABASE_STORAGE_BUCKET."
        )

    _config_cache = SupabaseConfig(url=url.rstrip("/"), service_role_key=key, decks_table=table, storage_bucket=bucket)
    return _config_cache


def _get_supabase_client(config: SupabaseConfig) -> Client:
    global _client_cache
    if _client_cache is None:
        _client_cache = create_client(config.url, config.service_role_key)
    return _client_cache


def fetch_deck_json(deck_id: str) -> Dict[str, Any]:
    """Load the deck JSON payload for a given deck ID."""
    config = load_supabase_config()
    supabase = _get_supabase_client(config)

    response = supabase.table(config.decks_table).select("id,json_path").eq("id", deck_id).limit(1).execute()
    if not response.data:
        raise KeyError(f"deck_id '{deck_id}' not found")

    json_path = response.data[0].get("json_path")
    if not json_path:
        raise KeyError(f"deck_id '{deck_id}' missing json_path")

    file_bytes = supabase.storage.from_(config.storage_bucket).download(json_path)
    try:
        return json.loads(file_bytes.decode("utf-8"))
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"Failed to decode deck JSON for '{deck_id}': {exc}")
