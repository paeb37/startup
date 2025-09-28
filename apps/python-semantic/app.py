import json
import logging
import os
import re
from pathlib import Path
from time import perf_counter
from typing import Any, Dict, List, Optional, Tuple

import requests
from dotenv import load_dotenv
from flask import Flask, jsonify, request
from flask_cors import CORS
from openai import OpenAI
from pydantic import BaseModel, ValidationError


load_dotenv(Path(__file__).resolve().parents[2] / ".env")

DEFAULT_MODEL = os.environ.get("OPENAI_MODEL", "gpt-4.1-mini")
EMBEDDING_MODEL = os.environ.get("OPENAI_EMBEDDING_MODEL", "text-embedding-3-small")
SUPABASE_URL = os.environ.get("SUPABASE_URL")
SUPABASE_SERVICE_ROLE_KEY = os.environ.get("SUPABASE_SERVICE_ROLE_KEY")
SUPABASE_DECKS_TABLE = os.environ.get("SUPABASE_DECKS_TABLE", "decks")
SUPABASE_SLIDES_TABLE = os.environ.get("SUPABASE_SLIDES_TABLE", "slides")
SUPABASE_BUCKET = os.environ.get("SUPABASE_STORAGE_BUCKET", "decks")

logging.basicConfig(level=logging.INFO)

_openai_client: Optional[OpenAI] = None
_supabase_session = requests.Session()

SYSTEM_PROMPT = """You translate natural-language redaction instructions into structured JSON.
Return exactly one action inside the array, following this shape:
{
  "actions": [
    {
      "id": "example-action",
      "type": "replace" | "rewrite",
      "scope": {
        "slides": { "from": 1, "to": 5 } | { "list": [1, 3] } | {}
      },
      "match": {
        "mode": "keyword" | "regex",
        "tokens": ["<keyword>"],
        "pattern": "\\$\\d[\\d,]*"
      },
      "replacement": "[CLIENT]",
      "rewrite": {
        "instructions": "Summarize in two sentences.",
        "maxLengthRatio": 1.0
      }
    }
  ],
  "meta": {
    "notes": "optional clarifications"
  }
}
Rules:
- Always return exactly one action. No additional array items.
- If type == "replace": include a non-empty "replacement" string and omit the "rewrite" object.
- If type == "rewrite": include a "rewrite" object with at least "maxLengthRatio" and omit the "replacement" field.
- Populate tokens/patterns only with values inferred from the user instruction or provided context. Never reuse the placeholder "<keyword>" unless the user explicitly says so.
- Omit fields that are not relevant, but never invent new keys.
- If you are unsure how to fulfill the request, return "actions": [] and explain in meta.notes."""
class Payload(BaseModel):
    deckId: Optional[str] = None
    instructions: Optional[str] = None

app = Flask(__name__)
CORS(app, resources={r"/api/*": {"origins": "*"}})


def _get_openai_client() -> OpenAI:
    global _openai_client
    if _openai_client is None:
        api_key = os.environ.get("OPENAI_API_KEY")
        if not api_key:
            raise RuntimeError("OPENAI_API_KEY is not set")
        _openai_client = OpenAI(api_key=api_key)
    return _openai_client


def _get_supabase_headers(extra: Optional[Dict[str, str]] = None) -> Dict[str, str]:
    if not SUPABASE_URL or not SUPABASE_SERVICE_ROLE_KEY:
        raise RuntimeError("Supabase configuration is missing")
    headers = {
        "apikey": SUPABASE_SERVICE_ROLE_KEY,
        "Authorization": f"Bearer {SUPABASE_SERVICE_ROLE_KEY}",
    }
    if extra:
        headers.update(extra)
    return headers

def interpret_instructions(instructions: str) -> List[Dict[str, object]]:
    """Heuristic fallback: derive simple replace actions."""

    actions: List[Dict[str, object]] = []
    if not instructions.strip():
        return actions

    low = instructions.lower()

    quoted = re.findall(r'"([^"\\]+)"', instructions)
    keywords = [kw.strip() for kw in quoted if kw.strip()]

    if not keywords:
        redact_chunks = re.findall(r"redact ([^.]+)", low)
        for chunk in redact_chunks:
            for part in re.split(r",|and", chunk):
                part = part.strip()
                if part:
                    keywords.append(part)

    dedup_keywords = list(dict.fromkeys(keywords))
    if dedup_keywords:
        actions.append(
            {
                "id": "replace-keywords",
                "type": "replace",
                "scope": {},
                "match": {
                    "mode": "keyword",
                    "tokens": dedup_keywords,
                },
                "replacement": "[REDACTED]",
            }
        )

    if any(token in low for token in ("revenue", "figure", "amount", "sales")):
        actions.append(
            {
                "id": "replace-numbers",
                "type": "replace",
                "scope": {},
                "match": {
                    "mode": "regex",
                    "pattern": r"\b\$?\d[\d,]*(?:\.\d+)?\b",
                },
                "replacement": "[AMOUNT]",
            }
        )

    return actions


def build_user_prompt(instructions: str) -> str:
    prompt_parts = [
        "# Instruction",
        instructions.strip() or "(none provided)",
        "\n# Output format",
        "Return strictly the JSON object described in the system message.",
    ]
    return "\n".join(prompt_parts)


def call_responses_api(instructions: str) -> Tuple[Dict[str, object], Dict[str, object]]:
    client = _get_openai_client()
    model = os.environ.get("OPENAI_MODEL", DEFAULT_MODEL)

    started = perf_counter()
    response = client.responses.create(
        model=model,
        instructions=SYSTEM_PROMPT,
        input=[
            {
                "role": "user",
                "content": build_user_prompt(instructions),
            }
        ],
    )
    latency = perf_counter() - started

    meta = {"source": "llm", "model": model, "responseSeconds": round(latency, 3)}

    try:
        parsed = json.loads(response.output_text)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"invalid JSON from model: {exc}")

    return parsed, meta


def _select_valid_action(actions: List[Dict[str, object]]) -> Optional[Dict[str, object]]:
    """Pick the first action that satisfies type-specific requirements."""

    for action in actions:
        if not isinstance(action, dict):
            continue

        action_type = action.get("type")
        if action_type == "replace":
            replacement = action.get("replacement")
            if isinstance(replacement, str) and replacement.strip():
                # Ensure rewrite block is absent
                action = dict(action)
                action.pop("rewrite", None)
                return action
        elif action_type == "rewrite":
            rewrite = action.get("rewrite")
            if isinstance(rewrite, dict) and "maxLengthRatio" in rewrite:
                action = dict(action)
                action.pop("replacement", None)
                return action

    return None


@app.post("/annotate")
def annotate():
    try:
        data = request.get_json(force=True, silent=False)
        payload = Payload.model_validate(data)
    except ValidationError as exc:
        return jsonify({"error": "bad payload", "details": exc.errors()}), 400
    except Exception as ex:
        return jsonify({"error": f"invalid JSON: {ex}"}), 400

    instructions = (payload.instructions or "").strip()

    actions = interpret_instructions(instructions)
    meta: Dict[str, object] = {"source": "heuristic" if actions else "none"}

    if instructions:
        try:
            llm_result, llm_meta = call_responses_api(instructions)
            if isinstance(llm_result, dict):
                llm_actions = llm_result.get("actions")
                if isinstance(llm_actions, list) and llm_actions:
                    validated = _select_valid_action(llm_actions)
                    if validated is not None:
                        actions = [validated]
                        meta = {}
                llm_meta_payload = llm_result.get("meta")
                if isinstance(llm_meta_payload, dict):
                    llm_meta.update(llm_meta_payload)
            meta.update(llm_meta)
        except Exception as ex:
            meta = {"source": "fallback", "error": str(ex)}

    return jsonify({
        "instructions": instructions,
        "deckId": payload.deckId,
        "actions": actions,
        "meta": meta
    })


def embed_query(text: str) -> List[float]:
    client = _get_openai_client()
    response = client.embeddings.create(model=EMBEDDING_MODEL, input=text)
    if not response.data:
        raise RuntimeError("No embedding data returned")
    vector = response.data[0].embedding
    if not vector:
        raise RuntimeError("Empty embedding vector returned")
    return vector


def match_slides(query_embedding: List[float], match_count: int = 4, match_threshold: float = 0.2) -> List[Dict[str, Any]]:
    if not SUPABASE_URL or not SUPABASE_SERVICE_ROLE_KEY:
        return []

    url = f"{SUPABASE_URL}/rest/v1/rpc/match_slides"
    payload = {
        "query_embedding": query_embedding,
        "match_count": match_count,
        "match_threshold": match_threshold,
    }

    try:
        response = _supabase_session.post(
            url,
            headers=_get_supabase_headers({"Content-Type": "application/json", "Accept": "application/json"}),
            json=payload,
            timeout=20,
        )
    except requests.RequestException as exc:
        logging.error("Supabase RPC failed: %s", exc)
        return []

    if response.status_code >= 400:
        logging.error("Supabase RPC error %s: %s", response.status_code, response.text)
        return []

    try:
        return response.json()
    except ValueError:
        logging.error("Invalid JSON received from Supabase RPC")
        return []


def fetch_decks(deck_ids: List[str]) -> Dict[str, Dict[str, Any]]:
    if not deck_ids:
        return {}

    unique_ids = sorted(set(deck_ids))
    ids_param = ",".join(unique_ids)

    url = f"{SUPABASE_URL}/rest/v1/{SUPABASE_DECKS_TABLE}"
    params = {
        "id": f"in.({ids_param})",
        "select": "id,deck_name,original_filename,json_path"
    }

    try:
        response = _supabase_session.get(
            url,
            headers=_get_supabase_headers({"Accept": "application/json"}),
            params=params,
            timeout=20,
        )
    except requests.RequestException as exc:
        logging.error("Supabase decks fetch failed: %s", exc)
        return {}

    if response.status_code >= 400:
        logging.error("Supabase decks error %s: %s", response.status_code, response.text)
        return {}

    try:
        rows = response.json()
    except ValueError:
        logging.error("Invalid JSON received when fetching decks")
        return {}

    return {row.get("id"): row for row in rows if row.get("id")}


def download_deck_json(json_path: str) -> Optional[Dict[str, Any]]:
    if not json_path:
        return None

    url = f"{SUPABASE_URL}/storage/v1/object/{SUPABASE_BUCKET}/{json_path.strip('/')}"
    try:
        response = _supabase_session.get(
            url,
            headers=_get_supabase_headers({"Accept": "application/json"}),
            timeout=20,
        )
    except requests.RequestException as exc:
        logging.error("Failed to download deck JSON: %s", exc)
        return None

    if response.status_code >= 400:
        logging.error("Deck JSON download error %s: %s", response.status_code, response.text)
        return None

    try:
        return response.json()
    except ValueError:
        logging.error("Invalid JSON body for deck %s", json_path)
        return None


def extract_slide_text(slide: Dict[str, Any]) -> str:
    parts: List[str] = []

    for element in slide.get("elements", []):
        element_type = element.get("type")
        if element_type == "textbox":
            for paragraph in element.get("paragraphs", []) or []:
                text = paragraph.get("text")
                if text and text.strip():
                    parts.append(text.strip())
        elif element_type == "table":
            for row in element.get("cells", []) or []:
                for cell in row or []:
                    if not cell:
                        continue
                    for paragraph in cell.get("paragraphs", []) or []:
                        text = paragraph.get("text")
                        if text and text.strip():
                            parts.append(text.strip())
            summary = element.get("summary")
            if summary:
                parts.append(summary.strip())
        elif element_type == "picture":
            summary = element.get("summary") or element.get("description")
            if summary:
                parts.append(summary.strip())

    combined = "\n".join(p for p in parts if p)
    combined = re.sub(r"\s+\n", "\n", combined)
    return combined.strip()


def build_context_from_matches(matches: List[Dict[str, Any]], max_contexts: int = 3) -> Tuple[List[Dict[str, Any]], List[Dict[str, Any]]]:
    contexts: List[Dict[str, Any]] = []
    references: List[Dict[str, Any]] = []
    if not matches:
        return contexts, references

    deck_ids = [m.get("deck_id") for m in matches if m.get("deck_id")]
    decks = fetch_decks(deck_ids)
    deck_json_cache: Dict[str, Any] = {}

    for match in matches:
        if len(contexts) >= max_contexts:
            break

        deck_id = match.get("deck_id")
        if not deck_id:
            continue

        deck_row = decks.get(deck_id)
        if not deck_row:
            continue

        if deck_id not in deck_json_cache:
            deck_data = download_deck_json(deck_row.get("json_path", ""))
            deck_json_cache[deck_id] = deck_data
        else:
            deck_data = deck_json_cache[deck_id]

        if not deck_data:
            continue

        slide_no = match.get("slide_no") or match.get("slide_number") or match.get("slide")
        if slide_no is None:
            continue

        slide = next((s for s in deck_data.get("slides", []) if s.get("index") == slide_no), None)
        if not slide:
            continue

        slide_text = extract_slide_text(slide)
        if not slide_text:
            continue

        deck_name = deck_row.get("deck_name") or deck_row.get("original_filename") or deck_id
        similarity = match.get("similarity") or match.get("score")

        contexts.append(
            {
                "deck_id": deck_id,
                "deck_name": deck_name,
                "slide_no": slide_no,
                "slide_id": match.get("id"),
                "similarity": similarity,
                "body": slide_text[:2000],
            }
        )

        references.append(
            {
                "slideId": match.get("id"),
                "deckId": deck_id,
                "deckName": deck_name,
                "slideNumber": slide_no,
                "similarity": similarity,
            }
        )

    return contexts, references


ANSWER_SYSTEM_PROMPT = """You are Dexter, an assistant that answers questions about slide decks using provided context snippets.
- Use only the supplied context; do not invent details.
- When you reference a slide, cite it inline using the format [Deck Name Slide 3].
- If the context is insufficient, say you do not have the information.
- Keep answers concise (2-4 sentences) unless the question demands more detail.
"""


def generate_answer(question: str, contexts: List[Dict[str, Any]]) -> str:
    if not contexts:
        return "I couldn't find any slides that address that yet. Try uploading more decks or asking something else."

    client = _get_openai_client()

    formatted_context = []
    for idx, ctx in enumerate(contexts, start=1):
        formatted_context.append(
            f"{idx}. Deck: {ctx['deck_name']} (Slide {ctx['slide_no']})\n{ctx['body'].strip()}"
        )

    user_content = (
        "Question:\n"
        f"{question}\n\n"
        "Context:\n"
        f"{'\n\n'.join(formatted_context)}\n\n"
        "Remember to cite slides using [Deck Name Slide X]."
    )

    response = client.responses.create(
        model=os.environ.get("OPENAI_MODEL", DEFAULT_MODEL),
        input=[
            {"role": "system", "content": ANSWER_SYSTEM_PROMPT},
            {"role": "user", "content": user_content},
        ],
        temperature=0.3,
    )

    return response.output_text.strip()


def answer_question(question: str) -> Tuple[str, List[Dict[str, Any]]]:
    if not question:
        return "", []

    try:
        embedding = embed_query(question)
    except Exception as exc:
        logging.error("Embedding failed: %s", exc)
        return "I couldn't process that question right now. Please try again in a moment.", []

    matches = match_slides(embedding, match_count=5)
    contexts, references = build_context_from_matches(matches)

    if not contexts:
        return (
            "I couldn't find any relevant slides yet. Try refining the question or upload more decks.",
            references,
        )

    try:
        answer = generate_answer(question, contexts)
    except Exception as exc:
        logging.error("Answer generation failed: %s", exc)
        fallback_lines = [
            f"- {ref['deckName']} slide {ref['slideNumber']}" for ref in references[:3]
        ]
        fallback = "Here are the slides that seem most relevant:\n" + "\n".join(fallback_lines)
        return fallback, references

    return answer, references


@app.post("/api/chat")
def chat():
    try:
        data = request.get_json(force=True, silent=False) or {}
        text = (data.get("message") or "").strip()
        if not text:
            return jsonify({"error": "message required"}), 400
        answer, sources = answer_question(text)
        return jsonify({
            "reply": answer,
            "sources": sources,
        })
    except Exception as ex:
        return jsonify({"error": f"invalid JSON: {ex}"}), 400
    
if __name__ == "__main__":
    app.run(host="0.0.0.0", port=8000, debug=True)
