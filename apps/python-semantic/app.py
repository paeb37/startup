from __future__ import annotations

import logging
import os
from pathlib import Path
from typing import Any, Dict, Optional

import requests
from dotenv import load_dotenv
from flask import Flask, jsonify, request
from flask_cors import CORS
from openai import OpenAI
from pydantic import BaseModel, ValidationError

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


logging.basicConfig(level=logging.INFO)

import chat
import redact


class RedactRequest(BaseModel):
    deckId: str
    instructions: str
    deck: Dict[str, Any]


app = Flask(__name__)
CORS(
    app,
    resources={
        r"/api/*": {"origins": "*"},
        r"/redact": {"origins": ["http://localhost:5173"]},
    },
)


@app.post("/redact")
def redact_endpoint():
    try:
        data = request.get_json(force=True, silent=False)
        payload = RedactRequest.model_validate(data)
    except ValidationError as exc:
        return jsonify({"error": "bad payload", "details": exc.errors()}), 400
    except Exception as ex:
        return jsonify({"error": f"invalid JSON: {ex}"}), 400

    instructions = payload.instructions.strip()
    if not instructions:
        return jsonify({"error": "instructions required"}), 400

    try:
        result = redact.process_redaction(payload.deckId, instructions, payload.deck)
    except RuntimeError as exc:
        logging.error("Redaction processing failed: %s", exc)
        return jsonify({"error": "redaction_failed", "detail": str(exc)}), 500

    return jsonify(result)


@app.post("/api/chat")
def chat_endpoint():
    try:
        data = request.get_json(force=True, silent=False) or {}
    except Exception as ex:
        return jsonify({"error": f"invalid JSON: {ex}"}), 400

    text = (data.get("message") or "").strip()
    if not text:
        return jsonify({"error": "message required"}), 400

    reply, sources = chat.answer_question(text)
    return jsonify({"reply": reply, "sources": sources})


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=8000, debug=True)
