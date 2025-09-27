import json
import os
import re
from pathlib import Path
from time import perf_counter
from typing import Dict, List, Optional, Tuple

from dotenv import load_dotenv
from flask import Flask, jsonify, request
from flask_cors import CORS
from openai import OpenAI
from pydantic import BaseModel, ValidationError


load_dotenv(Path(__file__).resolve().parents[2] / ".env")

DEFAULT_MODEL = os.environ.get("OPENAI_MODEL", "gpt-4.1-mini")

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
        "tokens": ["Acme"],
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
- Omit fields that are not relevant, but never invent new keys.
- If you are unsure how to fulfill the request, return "actions": [] and explain in meta.notes."""

'''
@app.post("/api/chat")
def chat():
    try:
        data = request.get_json(force=True, silent=False) or {}
        text = (data.get("message") or "").strip()
        if not text:
            return jsonify({"error": "message required"}), 400
        return jsonify({
            "reply": f"{text} noted"  # trivial echo + "noted"
        })
    except Exception as ex:
        return jsonify({"error": f"invalid JSON: {ex}"}), 400
'''

class Payload(BaseModel):
    deckId: Optional[str] = None
    instructions: Optional[str] = None

app = Flask(__name__)
CORS(app, resources={r"/api/*": {"origins": "*"}})

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
    api_key = os.environ.get("OPENAI_API_KEY")
    if not api_key:
        raise RuntimeError("OPENAI_API_KEY is not set")

    client = OpenAI(api_key=api_key)
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


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=8000, debug=True)
