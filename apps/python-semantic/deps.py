from __future__ import annotations

import os
from pathlib import Path
from typing import Dict, Optional

import requests
from dotenv import load_dotenv
from openai import OpenAI

# Load shared environment variables
load_dotenv(Path(__file__).resolve().parents[2] / ".env")

DEFAULT_MODEL = os.environ.get("OPENAI_MODEL", "gpt-5-mini-2025-08-07")
EMBEDDING_MODEL = os.environ.get("OPENAI_EMBEDDING_MODEL", "text-embedding-3-small")
SUPABASE_URL = os.environ.get("SUPABASE_URL")
SUPABASE_SERVICE_ROLE_KEY = os.environ.get("SUPABASE_SERVICE_ROLE_KEY")
SUPABASE_DECKS_TABLE = os.environ.get("SUPABASE_DECKS_TABLE", "decks")
SUPABASE_SLIDES_TABLE = os.environ.get("SUPABASE_SLIDES_TABLE", "slides")
SUPABASE_BUCKET = os.environ.get("SUPABASE_STORAGE_BUCKET", "decks")
SUPABASE_RULES_TABLE = os.environ.get("SUPABASE_RULES_TABLE", "rules")
SUPABASE_RULE_ACTIONS_TABLE = os.environ.get("SUPABASE_RULE_ACTIONS_TABLE", "rule_actions")
SLIDE_IMAGE_BASE = os.environ.get("SLIDE_IMAGE_BASE", "http://localhost:5100")

_openai_client: Optional[OpenAI] = None
_supabase_session = requests.Session()


def get_openai_client() -> OpenAI:
    global _openai_client
    if _openai_client is None:
        api_key = os.environ.get("OPENAI_API_KEY")
        if not api_key:
            raise RuntimeError("OPENAI_API_KEY is not set")
        _openai_client = OpenAI(api_key=api_key)
    return _openai_client


def get_supabase_session() -> requests.Session:
    return _supabase_session


def get_supabase_headers(extra: Optional[Dict[str, str]] = None) -> Dict[str, str]:
    if not SUPABASE_URL or not SUPABASE_SERVICE_ROLE_KEY:
        raise RuntimeError("Supabase configuration is missing")
    headers = {
        "apikey": SUPABASE_SERVICE_ROLE_KEY,
        "Authorization": f"Bearer {SUPABASE_SERVICE_ROLE_KEY}",
    }
    if extra:
        headers.update(extra)
    return headers
