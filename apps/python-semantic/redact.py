from __future__ import annotations

import itertools
import json
import logging
import os
import re
from time import perf_counter
from typing import Any, Dict, List, Optional, Tuple

from app import (
    DEFAULT_MODEL,
    SUPABASE_RULES_TABLE,
    SUPABASE_RULE_ACTIONS_TABLE,
    SUPABASE_SERVICE_ROLE_KEY,
    SUPABASE_URL,
    get_openai_client,
    get_supabase_headers,
    get_supabase_session,
)


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


def extract_deck_samples(
    deck: Dict[str, Any],
    strategy: str = "balanced",
) -> Tuple[List[Dict[str, Any]], Dict[str, Dict[str, Any]]]:
    """Extract representative content samples from deck."""
    slides = deck.get("slides", [])
    total_slides = len(slides)
    samples: List[Dict[str, Any]] = []
    paragraph_lookup: Dict[str, Dict[str, Any]] = {}
    paragraph_counter = itertools.count(1)

    if total_slides == 0:
        return samples, paragraph_lookup

    if total_slides <= 5:
        for i in range(total_slides):
            sample = _extract_slide_summary(slides[i], i, paragraph_lookup, paragraph_counter)
            if sample:
                samples.append(sample)
        return samples, paragraph_lookup

    for i in range(min(3, total_slides)):
        sample = _extract_slide_summary(slides[i], i, paragraph_lookup, paragraph_counter)
        if sample:
            samples.append(sample)

    if total_slides > 4:
        import random

        middle_start = 3
        middle_end = total_slides - 1
        sample_count = max(1, int((middle_end - middle_start) * 0.2))
        sampled_indices = random.sample(
            range(middle_start, middle_end),
            min(sample_count, middle_end - middle_start),
        )
        for idx in sampled_indices:
            sample = _extract_slide_summary(slides[idx], idx, paragraph_lookup, paragraph_counter)
            if sample:
                samples.append(sample)

    if total_slides > 3:
        sample = _extract_slide_summary(
            slides[-1],
            total_slides - 1,
            paragraph_lookup,
            paragraph_counter,
        )
        if sample:
            samples.append(sample)

    return samples, paragraph_lookup


def _extract_slide_summary(
    slide: Dict[str, Any],
    slide_idx: int,
    paragraph_lookup: Dict[str, Dict[str, Any]],
    paragraph_counter: itertools.count,
    max_chars: int = 1000,
) -> Optional[Dict[str, Any]]:
    paragraphs_for_sample: List[Dict[str, Any]] = []
    aggregated_lines: List[str] = []

    for element in slide.get("elements", []):
        element_type = element.get("type")
        element_key = element.get("key")

        if element_type == "textbox":
            paragraphs = element.get("paragraphs", [])
            for idx, paragraph in enumerate(paragraphs):
                text = paragraph.get("text", "")
                if text and text.strip():
                    cleaned = text.strip()
                    paragraph_id = f"S{slide_idx + 1}-P{next(paragraph_counter)}"
                    is_bullet = cleaned.lstrip().startswith("•")

                    paragraph_lookup[paragraph_id] = {
                        "paragraph_id": paragraph_id,
                        "slide_no": slide_idx + 1,
                        "element_key": element_key,
                        "paragraph_index": idx,
                        "text": cleaned,
                        "is_bullet": is_bullet,
                    }

                    paragraphs_for_sample.append(
                        {
                            "id": paragraph_id,
                            "text": cleaned,
                            "is_bullet": is_bullet,
                        }
                    )
                    aggregated_lines.append(cleaned)

        elif element_type == "table":
            for row in element.get("cells", []) or []:
                for cell in row or []:
                    if not cell:
                        continue
                    for paragraph in cell.get("paragraphs", []) or []:
                        text = paragraph.get("text", "")
                        if text and text.strip():
                            cleaned = text.strip()
                            aggregated_lines.append(cleaned)

    combined = "\n".join(aggregated_lines)
    if combined:
        return {
            "slide_no": slide_idx + 1,
            "text": combined[:max_chars],
            "paragraphs": paragraphs_for_sample,
        }

    return None


def format_samples_for_llm(samples: List[Dict[str, Any]]) -> str:
    formatted = []
    for sample in samples:
        slide_no = sample.get("slide_no")
        paragraphs = sample.get("paragraphs") or []
        if paragraphs:
            lines = [f"[{para['id']}] {para['text']}" for para in paragraphs]
            formatted.append(f"Slide {slide_no}:\n" + "\n".join(lines))
            continue

        text = sample.get("text", "")
        if text and text != "(empty)":
            formatted.append(f"Slide {slide_no}: {text}")
    return "\n\n".join(formatted)


def build_user_prompt_with_samples(instructions: str, deck_samples: List[Dict[str, Any]]) -> str:
    prompt_parts = [
        "# Instruction",
        instructions.strip() or "(none provided)",
        "\n# Deck Content Samples",
        format_samples_for_llm(deck_samples),
        "\n# Task",
        "Each paragraph is labeled with a paragraph_id like [S3-P4]. Use these IDs when recommending rewrites.",
        "Analyze the deck samples and identify:",
        "1. Specific entities (names, numbers) that need simple replacement",
        "2. Paragraphs that need complete rewriting due to intertwined sensitive details",
        "3. Patterns (like financial figures) that apply broadly",
        "\nReturn structured JSON as specified in the system message."
    ]
    return "\n".join(prompt_parts)


def call_responses_api(
    instructions: str,
    deck_samples: List[Dict[str, Any]],
) -> Tuple[Dict[str, object], Dict[str, object]]:
    client = get_openai_client()
    model = os.environ.get("OPENAI_MODEL", DEFAULT_MODEL)

    started = perf_counter()
    user_content = build_user_prompt_with_samples(instructions, deck_samples)

    logging.info("Samples sent to LLM:")
    for sample in deck_samples:
        logging.info("  Slide %s: %s...", sample.get("slide_no"), sample.get("text", "")[:100])

    logging.info("=" * 80)
    logging.info("FULL USER PROMPT:")
    logging.info(user_content)
    logging.info("=" * 80)
    logging.info("Instructions length: %s", len(instructions))
    logging.info("Samples count: %s", len(deck_samples))

    response = client.responses.create(
        model=model,
        instructions=ENTITY_IDENTIFICATION_PROMPT,
        input=[{"role": "user", "content": user_content}],
    )

    latency = perf_counter() - started
    meta = {
        "source": "llm_entity_extraction",
        "model": model,
        "responseSeconds": round(latency, 3),
        "samplesProvided": len(deck_samples),
    }

    try:
        parsed = json.loads(response.output_text)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"invalid JSON from model: {exc}")

    return parsed, meta


def convert_llm_response_to_actions(
    llm_result: Dict[str, Any],
    paragraph_lookup: Dict[str, Dict[str, Any]],
) -> List[Dict[str, Any]]:
    actions: List[Dict[str, Any]] = []

    entity_redactions = llm_result.get("entity_redactions", [])
    for entity_redaction in entity_redactions:
        if entity_redaction.get("confidence", 0) < 0.85:
            continue

        actions.append(
            {
                "id": f"entity-{len(actions)}",
                "type": "replace",
                "scope": {},
                "match": {
                    "mode": "keyword",
                    "tokens": [entity_redaction["entity"]],
                },
                "replacement": entity_redaction.get("replacement", "[REDACTED]"),
                "metadata": {
                    "confidence": entity_redaction.get("confidence"),
                    "entity_type": entity_redaction.get("type"),
                    "evidence": entity_redaction.get("evidence"),
                    "source": "llm_entity_extraction",
                    "action_category": "entity_replacement",
                },
            }
        )

    paragraph_rewrites = llm_result.get("paragraph_rewrites", [])
    for rewrite in paragraph_rewrites:
        if rewrite.get("confidence", 0) < 0.85:
            continue

        paragraph_id = rewrite.get("paragraph_id")
        paragraph_info: Optional[Dict[str, Any]] = None
        if isinstance(paragraph_id, str):
            paragraph_info = paragraph_lookup.get(paragraph_id)

        if paragraph_info is None:
            original_text = rewrite.get("original_text")
            if isinstance(original_text, str) and original_text.strip():
                normalized_original = original_text.strip()
                matches = [
                    info
                    for info in paragraph_lookup.values()
                    if info.get("text") == normalized_original
                ]
                if len(matches) == 1:
                    paragraph_info = matches[0]
                    paragraph_id = paragraph_info.get("paragraph_id")
                else:
                    logging.warning(
                        "Unable to uniquely map paragraph rewrite to paragraph: %s",
                        paragraph_id or normalized_original[:80],
                    )
                    continue
            else:
                logging.warning(
                    "Paragraph rewrite missing paragraph_id and original_text: %s",
                    rewrite,
                )
                continue

        replacement_text = rewrite.get("rewritten_text")
        if not isinstance(replacement_text, str):
            logging.warning("Rewrite missing rewritten_text for paragraph %s", paragraph_id)
            continue
        replacement_text = replacement_text.strip()

        if not replacement_text:
            logging.warning("Rewrite produced empty text for paragraph %s", paragraph_id)
            continue

        original_text = paragraph_info.get("text", "")
        if not original_text:
            continue

        if paragraph_info.get("is_bullet"):
            stripped = replacement_text.lstrip()
            if not stripped.startswith("•"):
                replacement_text = f"• {stripped}" if stripped else "•"

        slide_no = paragraph_info.get("slide_no")
        actions.append(
            {
                "id": f"rewrite-{len(actions)}",
                "type": "replace",
                "scope": {"slides": {"list": [slide_no]}} if slide_no else {},
                "match": {
                    "mode": "exact",
                    "text": original_text,
                },
                "replacement": replacement_text,
                "metadata": {
                    "confidence": rewrite.get("confidence"),
                    "reason": rewrite.get("reason"),
                    "source": "llm_paragraph_rewrite",
                    "action_category": "paragraph_rewrite",
                    "paragraph_id": paragraph_id,
                },
            }
        )

    patterns = llm_result.get("patterns", [])
    for pattern in patterns:
        if pattern.get("confidence", 0) < 0.85:
            continue

        actions.append(
            {
                "id": f"pattern-{len(actions)}",
                "type": "replace",
                "scope": {},
                "match": {
                    "mode": "regex",
                    "pattern": pattern["regex"],
                },
                "replacement": pattern.get("replacement", "[REDACTED]"),
                "metadata": {
                    "confidence": pattern.get("confidence"),
                    "pattern_type": pattern.get("type"),
                    "reason": pattern.get("reason"),
                    "source": "llm_pattern_extraction",
                    "action_category": "pattern_match",
                },
            }
        )

    return actions


def generate_rule_actions(deck: Dict[str, Any], actions: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    if not actions:
        return []

    def _action_priority(action: Dict[str, Any]) -> int:
        mode = (action.get("match") or {}).get("mode", "").lower()
        if mode == "exact":
            return 0
        if mode == "regex":
            return 1
        return 2

    ordered_actions = sorted(actions, key=_action_priority)

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

                for action in ordered_actions:
                    if not slide_in_scope(action.get("scope"), slide_no):
                        continue

                    updated_text, action_changed = apply_text_action(new_text, action)
                    if not action_changed:
                        continue

                    new_text = updated_text
                    changed = True
                    applied_action_metadata = action.get("metadata")

                    mode = (action.get("match") or {}).get("mode", "").lower()
                    if mode == "exact":
                        break

                if changed and new_text != original_text:
                    result = {
                        "slide_no": slide_no,
                        "element_key": element_key,
                        "bbox": bbox,
                        "original_text": original_text,
                        "new_text": new_text,
                    }

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
    replacement = action.get("replacement")
    if not isinstance(replacement, str) or not replacement.strip():
        replacement = "[REDACTED]"

    match = action.get("match") or {}
    mode = (match.get("mode") or "keyword").lower()

    updated_text = text
    changed = False

    if mode == "exact":
        exact_text = match.get("text", "")
        if exact_text and text.strip() == exact_text.strip():
            updated_text = replacement
            changed = True
        return updated_text, changed

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

    client = get_openai_client()
    prompt = (
        "Summarize this redaction instruction in 3 to 6 words. "
        "Return only the summary text with no punctuation at the end.\n\n"
        f"Instruction: {instruction_text}"
    )

    try:
        response = client.responses.create(
            model=os.environ.get("OPENAI_MODEL", DEFAULT_MODEL),
            input=[{"role": "user", "content": prompt}],
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
    session = get_supabase_session()
    response = session.post(
        url,
        headers=get_supabase_headers(
            {
                "Content-Type": "application/json",
                "Prefer": "return=representation",
                "Accept": "application/json",
            }
        ),
        json=payload,
        timeout=20,
    )

    if response.status_code >= 400:
        raise RuntimeError(
            f"Supabase rule insert failed ({response.status_code}): {response.text}"
        )

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
    session = get_supabase_session()
    response = session.post(
        url,
        headers=get_supabase_headers(
            {
                "Content-Type": "application/json",
                "Prefer": "return=minimal",
            }
        ),
        json=rows,
        timeout=30,
    )

    if response.status_code >= 400:
        raise RuntimeError(
            f"Supabase rule_actions insert failed ({response.status_code}): {response.text}"
        )

    return len(rows)


def process_redaction(deck_id: str, instructions: str, deck: Dict[str, Any]) -> Dict[str, Any]:
    deck_samples, paragraph_lookup = extract_deck_samples(deck, strategy="balanced")
    logging.info("Extracted %s samples from deck", len(deck_samples))
    logging.info("Captured %s paragraph entries for targeting", len(paragraph_lookup))

    try:
        llm_result, llm_meta = call_responses_api(instructions, deck_samples)
        logging.info("LLM Result: %s", json.dumps(llm_result, indent=2))
        actions = convert_llm_response_to_actions(llm_result, paragraph_lookup)
        logging.info("Generated %s actions", len(actions))
        for i, action in enumerate(actions):
            logging.info("  Action %s: %s", i + 1, json.dumps(action, indent=2))
        meta = llm_meta
    except Exception as ex:
        logging.error("LLM entity identification failed: %s", ex)
        actions = interpret_instructions(instructions)
        meta = {"source": "fallback_heuristic", "error": str(ex)}

    generated_actions = generate_rule_actions(deck, actions)
    logging.info("Generated %s rule_actions to apply", len(generated_actions))

    title = generate_rule_title(instructions)
    rule_id = insert_rule(deck_id, title, instructions)

    inserted_rows = 0
    if generated_actions:
        session_rows = []
        for row in generated_actions:
            row["rule_id"] = rule_id
            row["deck_id"] = deck_id
            session_rows.append(row)
        inserted_rows = insert_rule_actions(session_rows)

    return {
        "deckId": deck_id,
        "ruleId": rule_id,
        "actionCount": inserted_rows,
        "meta": meta,
    }


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
      "entity": string,
      "type": string,
      "confidence": float,
      "replacement": string,
      "evidence": string
    }
  ],
  "paragraph_rewrites": [
    {
      "paragraph_id": string,
      "slide_no": integer,
      "rewritten_text": string,
      "reason": string,
      "confidence": float
    }
  ],
  "patterns": [
    {
      "regex": string,
      "type": string,
      "confidence": float,
      "replacement": string,
      "reason": string
    }
  ]
}

# Guidelines for Entity Redactions
- Simple find-and-replace cases (names, specific numbers, identifiers)
- Confidence > 0.85 to include
- Provide appropriate replacement tokens
- DO NOT redact: List numbers (1., 2., 3.), ordinals, page numbers, formatting numbers
- FOCUS ON: Client names, client employees, engagement team names, financial amounts, production volumes, capacity figures, operational metrics, addresses
- Note: Client names include company name and any people associated with the client or engagement team

# Guidelines for Paragraph Rewrites
- Treat each paragraph/bullet independently; never merge or split them
- Reference paragraphs by the provided paragraph_id (e.g., "S3-P2")
- Use when paragraph has multiple sensitive details intertwined OR when context remains identifiable after entity replacement
- Trigger scenarios: Sentences with any location, date, or superlative combos (e.g., "Gripe is Hawaii's largest industrial equipment manufacturer, operating since 1987.")
- Provide natural-sounding rewritten text that maintains tone and structure while keeping the same bullet/sentence format
- Remove specifics, keep general meaning
- Confidence > 0.85 to include

# Guidelines for Patterns
- Use patterns (regex) for structural data: emails, phone numbers, dates
- Never use entity redaction for emails or phones - always use regex patterns to match complete format
- Example: Email pattern should match entire email address, not just parts

# When to Use Which
- Entity redaction: "Revenue from Acme Corp: $45M" → "Revenue from [CLIENT]: [AMOUNT]"
- Paragraph rewrite: "Acme is Hawaii's largest manufacturer since 1987" → "Client is a longstanding manufacturer"
- Pattern: "contact@acme.com" → "[EMAIL]" (using regex, not keyword)

Prefer entity redaction when possible. Use paragraph rewrite when context remains identifying. Use patterns for structural data.
"""
