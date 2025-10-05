from __future__ import annotations

import json
import logging
import os
import re
from pathlib import Path
from time import perf_counter
from typing import Any, Dict, Optional, Tuple, List

import requests
from dotenv import load_dotenv
from flask import Flask, jsonify, request
from flask_cors import CORS
from openai import OpenAI
from pydantic import BaseModel, ValidationError

from supabase_client import fetch_deck_json


load_dotenv(Path(__file__).resolve().parents[2] / ".env")

DEFAULT_MODEL = os.environ.get("OPENAI_MODEL", "gpt-4.1-mini")
EMBEDDING_MODEL = os.environ.get("OPENAI_EMBEDDING_MODEL", "text-embedding-3-small")
SUPABASE_URL = os.environ.get("SUPABASE_URL")
SUPABASE_SERVICE_ROLE_KEY = os.environ.get("SUPABASE_SERVICE_ROLE_KEY")
SUPABASE_DECKS_TABLE = os.environ.get("SUPABASE_DECKS_TABLE", "decks")
SUPABASE_SLIDES_TABLE = os.environ.get("SUPABASE_SLIDES_TABLE", "slides")
SUPABASE_BUCKET = os.environ.get("SUPABASE_STORAGE_BUCKET", "decks")
SUPABASE_RULES_TABLE = os.environ.get("SUPABASE_RULES_TABLE", "rules")
SUPABASE_RULE_ACTIONS_TABLE = os.environ.get("SUPABASE_RULE_ACTIONS_TABLE", "rule_actions")
SLIDE_IMAGE_BASE = os.environ.get("SLIDE_IMAGE_BASE", "http://localhost:5100")

logging.basicConfig(level=logging.INFO)

_openai_client: Optional[OpenAI] = None
_supabase_session = requests.Session()

ENTITY_IDENTIFICATION_PROMPT = """
# Instructions
You are an expert at identifying sensitive information in documents. 

Given:
1. A redaction instruction from the user
2. Sample content from a presentation deck

Your task:
- Identify specific entities (names, numbers, etc.) that need simple replacement
- Identify paragraphs that contain too much sensitive information and need complete rewriting
- Return structured JSON with both types of redactions. All keys must be present (use empty arrays if no results):

# Output Format
{
  "entity_redactions": [
    {
      "entity": string,           // The exact text to find (e.g., "Acme Corp")
      "type": string,             // Category (e.g., "client_name", "email", "person_name")
      "confidence": float,        // Must be > 0.85 (range 0.0-1.0)
      "replacement": string,      // Placeholder token (e.g., "[CLIENT]")
      "evidence": string          // Brief context explaining the identification
    }
  ],
  "paragraph_rewrites": [
    {
      "slide_no": integer,        // Slide number where paragraph appears (e.g. 3)
      "original_text": string,    // Complete original paragraph text
      "rewritten_text": string,   // Generalized version without sensitive details
      "reason": string,           // Why rewriting is better than entity replacement
      "confidence": float         // Must be > 0.85 (range 0.0-1.0)
    }
  ],
  "patterns": [
    {
      "regex": string,            // Regex pattern (e.g. "\\\\$\\\\d+(?:,\\\\d{3})*(?:\\\\.\\\\d{2})?[KMB]?")
      "type": string,             // Pattern category (e.g., "financial_figure")
      "confidence": float,        // Must be > 0.85 (range 0.0-1.0)
      "replacement": string,      // Replacement token (e.g., "[AMOUNT]")
      "reason": string            // Pattern explanation (e.g. "Pattern for currency amounts")
    }
  ]
}

# Guidelines for Entity Redactions
- Simple find-and-replace cases (names, specific numbers, identifiers)
- Confidence > 0.85 to include
- Provide appropriate replacement tokens

# Guidelines for Paragraph Rewrites
- Use when paragraph has multiple sensitive details intertwined
- Provide natural-sounding rewritten text that maintains tone and structure
- Remove specifics, keep general meaning
- Confidence > 0.85 to include

# When to Use Which
- Entity redaction: "Revenue from Acme Corp: $45M" → "Revenue from [CLIENT]: [AMOUNT]"
- Paragraph rewrite: Complex paragraph with multiple intertwined details → Fully rewritten text

Prefer entity redaction when possible. Use paragraph rewrite only when necessary for context preservation.
"""


class RedactRequest(BaseModel):
    deckId: str
    instructions: str
    deck: Dict[str, Any]


app = Flask(__name__)
CORS(app, resources={
    r"/api/*": {"origins": "*"},
    r"/redact": {"origins": ["http://localhost:5173"]},
})


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


def extract_deck_samples(deck: Dict[str, Any], strategy: str = "balanced") -> List[Dict[str, Any]]:
    """Extract representative content samples from deck."""
    slides = deck.get("slides", [])
    total_slides = len(slides)
    samples = []
    
    if total_slides == 0:
        return samples
    
    # For small decks, sample all slides
    if total_slides <= 5:
        for i in range(total_slides):
            sample = _extract_slide_summary(slides[i], i)
            if sample:
                samples.append(sample)
        return samples
    
    # Balanced strategy: first 3 + random 20% middle + last
    # First 3 slides
    for i in range(min(3, total_slides)):
        sample = _extract_slide_summary(slides[i], i)
        if sample:
            samples.append(sample)
    
    # Sample 20% from middle
    if total_slides > 4:
        import random
        middle_start = 3
        middle_end = total_slides - 1
        sample_count = max(1, int((middle_end - middle_start) * 0.2))
        sampled_indices = random.sample(range(middle_start, middle_end), min(sample_count, middle_end - middle_start))
        for idx in sampled_indices:
            sample = _extract_slide_summary(slides[idx], idx)
            if sample:
                samples.append(sample)
    
    # Last slide
    if total_slides > 3:
        sample = _extract_slide_summary(slides[-1], total_slides - 1)
        if sample:
            samples.append(sample)
    
    return samples


def _extract_slide_summary(slide: Dict[str, Any], slide_idx: int, max_chars: int = 300) -> Optional[Dict[str, Any]]:
    """Extract first paragraph or first N characters from a slide."""
    for element in slide.get("elements", []):
        if element.get("type") == "textbox":
            paragraphs = element.get("paragraphs", [])
            if paragraphs:
                text = paragraphs[0].get("text", "")
                if text and text.strip():
                    return {
                        "slide_no": slide_idx + 1,
                        "text": text[:max_chars]
                    }
    return None


def format_samples_for_llm(samples: List[Dict[str, Any]]) -> str:
    """Format samples into readable string for LLM prompt."""
    formatted = []
    for sample in samples:
        slide_no = sample.get("slide_no")
        text = sample.get("text", "")
        if text and text != "(empty)":
            formatted.append(f"Slide {slide_no}: {text}")
    return "\n\n".join(formatted)


def build_user_prompt_with_samples(instructions: str, deck_samples: List[Dict[str, Any]]) -> str:
    """Build user prompt including instruction and deck samples."""
    prompt_parts = [
        "# Instruction",
        instructions.strip() or "(none provided)",
        "\n# Deck Content Samples",
        format_samples_for_llm(deck_samples),
        "\n# Task",
        "Analyze the deck samples and identify:",
        "1. Specific entities (names, numbers) that need simple replacement",
        "2. Paragraphs that need complete rewriting due to intertwined sensitive details",
        "3. Patterns (like financial figures) that apply broadly",
        "\nReturn structured JSON as specified in the system message."
    ]
    return "\n".join(prompt_parts)


def call_responses_api(instructions: str, deck_samples: List[Dict[str, Any]]) -> Tuple[Dict[str, object], Dict[str, object]]:
    """Use LLM to identify entities and paragraphs to redact from deck samples."""
    client = _get_openai_client()
    model = os.environ.get("OPENAI_MODEL", DEFAULT_MODEL)
    
    started = perf_counter()
    
    user_content = build_user_prompt_with_samples(instructions, deck_samples)
    
    # DEBUG: Log samples sent to LLM
    logging.info("Samples sent to LLM:")
    for sample in deck_samples:
        logging.info(f"  Slide {sample.get('slide_no')}: {sample.get('text', '')[:100]}...")
    
    # DEBUG: Log full user prompt
    logging.info("="*80)
    logging.info("FULL USER PROMPT:")
    logging.info(user_content)
    logging.info("="*80)
    logging.info(f"Instructions length: {len(instructions)}")
    logging.info(f"Samples count: {len(deck_samples)}")
    
    response = client.responses.create(
        model=model,
        instructions=ENTITY_IDENTIFICATION_PROMPT,
        input=[{
            "role": "user",
            "content": user_content
        }]
    )
    
    latency = perf_counter() - started
    meta = {
        "source": "llm_entity_extraction",
        "model": model,
        "responseSeconds": round(latency, 3),
        "samplesProvided": len(deck_samples)
    }
    
    try:
        parsed = json.loads(response.output_text)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"invalid JSON from model: {exc}")
    
    return parsed, meta


def convert_llm_response_to_actions(llm_result: Dict[str, Any]) -> List[Dict[str, Any]]:
    """Convert LLM entity identification + paragraph rewrites to unified action format."""
    actions = []
    
    # Convert entity redactions to keyword actions
    entity_redactions = llm_result.get("entity_redactions", [])
    for entity_redaction in entity_redactions:
        if entity_redaction.get("confidence", 0) < 0.85:
            continue
        
        actions.append({
            "id": f"entity-{len(actions)}",
            "type": "replace",
            "scope": {},
            "match": {
                "mode": "keyword",
                "tokens": [entity_redaction["entity"]]
            },
            "replacement": entity_redaction.get("replacement", "[REDACTED]"),
            "metadata": {
                "confidence": entity_redaction.get("confidence"),
                "entity_type": entity_redaction.get("type"),
                "evidence": entity_redaction.get("evidence"),
                "source": "llm_entity_extraction",
                "action_category": "entity_replacement"
            }
        })
    
    # Convert paragraph rewrites to exact match actions
    paragraph_rewrites = llm_result.get("paragraph_rewrites", [])
    for rewrite in paragraph_rewrites:
        if rewrite.get("confidence", 0) < 0.85:
            continue
        
        slide_no = rewrite.get("slide_no")
        actions.append({
            "id": f"rewrite-{len(actions)}",
            "type": "replace",
            "scope": {"slides": {"list": [slide_no]}} if slide_no else {},
            "match": {
                "mode": "exact",
                "text": rewrite["original_text"]
            },
            "replacement": rewrite["rewritten_text"],
            "metadata": {
                "confidence": rewrite.get("confidence"),
                "reason": rewrite.get("reason"),
                "source": "llm_paragraph_rewrite",
                "action_category": "paragraph_rewrite"
            }
        })
    
    # Convert patterns to regex actions
    patterns = llm_result.get("patterns", [])
    for pattern in patterns:
        if pattern.get("confidence", 0) < 0.85:
            continue
        
        actions.append({
            "id": f"pattern-{len(actions)}",
            "type": "replace",
            "scope": {},
            "match": {
                "mode": "regex",
                "pattern": pattern["regex"]
            },
            "replacement": pattern.get("replacement", "[REDACTED]"),
            "metadata": {
                "confidence": pattern.get("confidence"),
                "pattern_type": pattern.get("type"),
                "reason": pattern.get("reason"),
                "source": "llm_pattern_extraction",
                "action_category": "pattern_match"
            }
        })
    
    return actions


@app.post("/redact")
def redact():
    """
    This is the main method called by the frontend service

    Frontend hits /upload on C#, which parses OpenXML and extracts the deck content.
    Creates the JSON, stores in Supabase, returns to frontend

    Frontend hits /redact and passes in the deckID, query, and deck JSON

    
    
    """
    try:
        data = request.get_json(force=True, silent=False)
        payload = RedactRequest.model_validate(data)
    except ValidationError as exc:
        return jsonify({"error": "bad payload", "details": exc.errors()}), 400
    except Exception as ex:
        return jsonify({"error": f"invalid JSON: {ex}"}), 400

    deck_id = payload.deckId
    instructions = payload.instructions.strip()
    deck = payload.deck # Full JSON structure

    if not instructions:
        return jsonify({"error": "instructions required"}), 400

    # Extract samples and call LLM with deck content
    try:
        deck_samples = extract_deck_samples(deck, strategy="balanced")
        logging.info(f"Extracted {len(deck_samples)} samples from deck")
        
        # CALL LLM HERE
        llm_result, llm_meta = call_responses_api(instructions, deck_samples)
        
        # DEBUG: Log what LLM identified
        logging.info(f"LLM Result: {json.dumps(llm_result, indent=2)}")
        
        actions = convert_llm_response_to_actions(llm_result)
        
        # DEBUG: Log generated actions
        logging.info(f"Generated {len(actions)} actions:")
        for i, action in enumerate(actions):
            logging.info(f"  Action {i+1}: {json.dumps(action, indent=2)}")
        
        meta = llm_meta
    except Exception as ex:
        logging.error("LLM entity identification failed: %s", ex)
        # Fallback to heuristic
        actions = interpret_instructions(instructions)
        meta = {"source": "fallback_heuristic", "error": str(ex)}

    generated_actions = generate_rule_actions(deck, actions)
    
    # DEBUG: Log how many rule actions were generated
    logging.info(f"Generated {len(generated_actions)} rule_actions to apply")

    try:
        title = generate_rule_title(instructions)
        rule_id = insert_rule(deck_id, title, instructions)
    except Exception as ex:
        logging.error("Failed to insert rule: %s", ex)
        return jsonify({"error": "rule_insert_failed", "detail": str(ex)}), 500

    inserted_rows = 0
    if generated_actions:
        try:
            for row in generated_actions:
                row["rule_id"] = rule_id
                row["deck_id"] = deck_id
            inserted_rows = insert_rule_actions(generated_actions)
        except Exception as ex:
            logging.error("Failed to insert rule actions: %s", ex)
            return jsonify({"error": "rule_actions_insert_failed", "detail": str(ex)}), 500

    return jsonify({
        "deckId": deck_id,
        "ruleId": rule_id,
        "actionCount": inserted_rows,
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
        "select": "id,deck_name,redacted_json_path"
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


def download_deck_json(storage_path: str) -> Optional[Dict[str, Any]]:
    if not storage_path:
        return None

    url = f"{SUPABASE_URL}/storage/v1/object/{SUPABASE_BUCKET}/{storage_path.strip('/')}"
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

        slide = next((s for s in deck_data.get("slides", []) if s.get("index") == slide_index), None)
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
        ]
    )

    # removed temperature param

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


def generate_rule_actions(deck: Dict[str, Any], actions: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """
    This is the KEY translation
    
    Takes LLM's abstract action like:
      { "match": { "tokens": ["Acme Corp"] }, "replacement": "[REDACTED]" }
    
    And converts it to concrete replacements:
      [
        { slide_no: 3, element_key: "...", original_text: "Revenue from Acme Corp", 
          new_text: "Revenue from [REDACTED]" },
        { slide_no: 7, element_key: "...", original_text: "Acme Corp partnership", 
          new_text: "[REDACTED] partnership" },
        ...
      ]

    The element key is the specific textbox #
    ppt/slides/slide5.xml#textbox#5
    """
    if not actions:
        return []

    slides = deck.get("slides") or []
    results: List[Dict[str, Any]] = []

    for slide in slides:
        slide_index = slide.get("index")
        if slide_index is None:
            continue
        try:
            slide_no = int(slide_index) + 1
        except (TypeError, ValueError):
            continue

        elements = slide.get("elements") or []
        for element in elements:
            # for now we only focus on the textbox
            if element.get("type") != "textbox":
                continue

            element_key = element.get("key")
            if not element_key:
                continue

            bbox = element.get("bbox")
            paragraphs = element.get("paragraphs") or []

            for paragraph in paragraphs:
                original_text = paragraph.get("text") or ""
                if not original_text or not isinstance(original_text, str):
                    continue

                new_text = original_text
                changed = False
                applied_action_metadata = None

                for action in actions:
                    if not slide_in_scope(action.get("scope"), slide_no):
                        continue

                    new_text, action_changed = apply_text_action(new_text, action)
                    if action_changed:
                        changed = True
                        applied_action_metadata = action.get("metadata")
                        break  # Stop after first match

                if changed and new_text != original_text:
                    result = {
                        "slide_no": slide_no,
                        "element_key": element_key,
                        "bbox": bbox,
                        "original_text": original_text,
                        "new_text": new_text,
                    }
                    
                    # Include metadata if available
                    if applied_action_metadata:
                        result["metadata"] = applied_action_metadata
                    
                    results.append(result)

    return results


def slide_in_scope(scope: Optional[Dict[str, Any]], slide_no: int) -> bool:
    if not scope or not isinstance(scope, dict):
        return True

    slides = scope.get("slides")
    if not slides or not isinstance(slides, dict):
        return True

    slide_list = slides.get("list")
    if isinstance(slide_list, list) and len(slide_list) > 0:
        normalized = []
        for value in slide_list:
            try:
                normalized.append(int(value))
            except (TypeError, ValueError):
                continue
        if normalized and slide_no in normalized:
            return True
        if normalized:
            return False

    from_value = slides.get("from")
    to_value = slides.get("to")
    if from_value is not None or to_value is not None:
        try:
            start = int(from_value) if from_value is not None else 1
        except (TypeError, ValueError):
            start = 1
        try:
            end = int(to_value) if to_value is not None else slide_no
        except (TypeError, ValueError):
            end = slide_no
        if slide_no < start or slide_no > end:
            return False

    return True


def apply_text_action(text: str, action: Dict[str, Any]) -> Tuple[str, bool]:
    """Apply action to text. Supports keyword, regex, and exact match modes."""
    replacement = action.get("replacement")
    if not isinstance(replacement, str) or not replacement.strip():
        replacement = "[REDACTED]"

    match = action.get("match") or {}
    mode = (match.get("mode") or "keyword").lower()

    updated_text = text
    changed = False

    # Exact match mode (for paragraph rewrites)
    if mode == "exact":
        exact_text = match.get("text", "")
        if exact_text and text.strip() == exact_text.strip():
            updated_text = replacement
            changed = True
        return updated_text, changed

    # Regex match mode
    if mode == "regex":
        pattern = match.get("pattern")
        if isinstance(pattern, str) and pattern.strip():
            try:
                regex = re.compile(pattern, re.IGNORECASE)
                if regex.search(updated_text):
                    updated_text = regex.sub(replacement, updated_text)
                    changed = updated_text != text
            except re.error as exc:
                logging.warning("Invalid regex pattern '%s': %s", pattern, exc)
    
    # Keyword match mode (default)
    else:
        tokens = match.get("tokens") or []
        token_list = [t.strip() for t in tokens if isinstance(t, str) and t.strip()]
        if token_list:
            for token in token_list:
                regex = re.compile(re.escape(token), re.IGNORECASE)
                if regex.search(updated_text):
                    updated_text = regex.sub(replacement, updated_text)
                    changed = True

    return updated_text, changed


def generate_rule_title(instructions: str) -> str:
    instruction_text = instructions.strip()
    if not instruction_text:
        return "Untitled rule"

    client = _get_openai_client()
    prompt = (
        "Summarize this redaction instruction in 3 to 6 words. "
        "Return only the summary text with no punctuation at the end.\n\n"
        f"Instruction: {instruction_text}"
    )

    try:
        response = client.responses.create(
            model=os.environ.get("OPENAI_MODEL", DEFAULT_MODEL),
            input=[{"role": "user", "content": prompt}]
        )
        summary = response.output_text.strip()
        if summary:
            return summary[:120]
    except Exception as exc:
        logging.warning("Failed to generate rule title: %s", exc)

    fallback = instruction_text
    if len(fallback) > 120:
        fallback = fallback[:117] + "..."
    return fallback or "Untitled rule"


def insert_rule(deck_id: str, title: str, instructions: str) -> str:
    """
    Put the rule into Supabase
    Later fetched by the C# service

    """

    if not SUPABASE_URL or not SUPABASE_SERVICE_ROLE_KEY:
        raise RuntimeError("Supabase configuration missing")

    payload = [
        {
            "deck_id": deck_id,
            "title": title,
            "user_query": instructions,
        }
    ]

    url = f"{SUPABASE_URL}/rest/v1/{SUPABASE_RULES_TABLE}"
    response = _supabase_session.post(
        url,
        headers=_get_supabase_headers({
            "Content-Type": "application/json",
            "Prefer": "return=representation",
            "Accept": "application/json",
        }),
        json=payload,
        timeout=20,
    )

    if response.status_code >= 400:
        raise RuntimeError(f"Supabase rule insert failed ({response.status_code}): {response.text}")

    try:
        rows = response.json()
    except ValueError as exc:
        raise RuntimeError(f"Invalid JSON from Supabase rule insert: {exc}")

    if not rows or not rows[0].get("id"):
        raise RuntimeError("Supabase rule insert returned no id")

    return rows[0]["id"]


def insert_rule_actions(rows: List[Dict[str, Any]]) -> int:
    if not rows:
        return 0

    if not SUPABASE_URL or not SUPABASE_SERVICE_ROLE_KEY:
        raise RuntimeError("Supabase configuration missing")

    url = f"{SUPABASE_URL}/rest/v1/{SUPABASE_RULE_ACTIONS_TABLE}"
    response = _supabase_session.post(
        url,
        headers=_get_supabase_headers({
            "Content-Type": "application/json",
            "Prefer": "return=minimal",
        }),
        json=rows,
        timeout=30,
    )

    if response.status_code >= 400:
        raise RuntimeError(f"Supabase rule_actions insert failed ({response.status_code}): {response.text}")

    return len(rows)


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
