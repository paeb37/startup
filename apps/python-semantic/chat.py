from __future__ import annotations

import logging
import os
import re
from typing import Any, Dict, List, Optional, Tuple

import requests

from deps import (
    DEFAULT_MODEL,
    EMBEDDING_MODEL,
    SLIDE_IMAGE_BASE,
    SUPABASE_BUCKET,
    SUPABASE_DECKS_TABLE,
    SUPABASE_SERVICE_ROLE_KEY,
    SUPABASE_URL,
    get_openai_client,
    get_supabase_headers,
    get_supabase_session,
)


def embed_query(text: str) -> List[float]:
    client = get_openai_client()
    response = client.embeddings.create(model=EMBEDDING_MODEL, input=text)
    if not response.data:
        raise RuntimeError("No embedding data returned")
    vector = response.data[0].embedding
    if not vector:
        raise RuntimeError("Empty embedding vector returned")
    return vector


def match_slides(
    query_embedding: List[float],
    match_count: int = 4,
    match_threshold: float = 0.2,
) -> List[Dict[str, Any]]:
    if not SUPABASE_URL or not SUPABASE_SERVICE_ROLE_KEY:
        return []

    url = f"{SUPABASE_URL}/rest/v1/rpc/match_slides"
    payload = {
        "query_embedding": query_embedding,
        "match_count": match_count,
        "match_threshold": match_threshold,
    }

    session = get_supabase_session()
    try:
        response = session.post(
            url,
            headers=get_supabase_headers(
                {"Content-Type": "application/json", "Accept": "application/json"}
            ),
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
        "select": "id,deck_name,redacted_json_path",
    }

    session = get_supabase_session()
    try:
        response = session.get(
            url,
            headers=get_supabase_headers({"Accept": "application/json"}),
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


def download_deck_json(storage_path: str) -> Optional[Dict[str, Any]]:
    if not storage_path:
        return None

    url = f"{SUPABASE_URL}/storage/v1/object/{SUPABASE_BUCKET}/{storage_path.strip('/')}"
    session = get_supabase_session()
    try:
        response = session.get(
            url,
            headers=get_supabase_headers({"Accept": "application/json"}),
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
        logging.error("Invalid JSON body for deck %s", storage_path)
        return None


def build_slide_image_url(deck_id: str, slide_no: int) -> str:
    base = (SLIDE_IMAGE_BASE or "").rstrip("/")
    return f"{base}/api/decks/{deck_id}/slides/{slide_no}"


def infer_deck_name(*paths: Optional[str]) -> Optional[str]:
    for candidate in paths:
        if not candidate or not isinstance(candidate, str):
            continue
        cleaned = candidate.strip().strip("/")
        if not cleaned:
            continue
        stem = os.path.splitext(os.path.basename(cleaned))[0]
        if stem:
            return stem
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


def build_context_from_matches(
    matches: List[Dict[str, Any]],
    max_contexts: int = 3,
) -> Tuple[List[Dict[str, Any]], List[Dict[str, Any]]]:
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
            deck_data = download_deck_json(deck_row.get("redacted_json_path", ""))
            deck_json_cache[deck_id] = deck_data
        else:
            deck_data = deck_json_cache[deck_id]

        if not deck_data:
            continue

        slide_no_raw = match.get("slide_no") or match.get("slide_number") or match.get("slide")
        if slide_no_raw is None:
            continue

        if isinstance(slide_no_raw, str):
            try:
                slide_index = int(slide_no_raw)
            except ValueError:
                continue
        elif isinstance(slide_no_raw, (int, float)) and not isinstance(slide_no_raw, bool):
            slide_index = int(slide_no_raw)
        else:
            continue

        if slide_index < 0:
            continue

        slide = next(
            (s for s in deck_data.get("slides", []) if s.get("index") == slide_index),
            None,
        )
        if not slide:
            continue

        slide_text = extract_slide_text(slide)
        if not slide_text:
            continue

        deck_name = (
            deck_row.get("deck_name")
            or infer_deck_name(
                deck_row.get("redacted_pptx_path"),
                deck_row.get("pptx_path"),
            )
            or deck_id
        )
        similarity = match.get("similarity") or match.get("score")
        human_slide_no = slide_index + 1

        contexts.append(
            {
                "deck_id": deck_id,
                "deck_name": deck_name,
                "slide_no": human_slide_no,
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
                "slideNumber": human_slide_no,
                "similarity": similarity,
                "thumbnailUrl": build_slide_image_url(deck_id, human_slide_no),
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

    client = get_openai_client()

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
