#!/usr/bin/env python3
"""Prototype helper for deriving redaction rules via OpenAI Responses API.

Run this script manually while iterating on prompt design.  It reads
instructions from the command line (or stdin), builds a structured request
for the Responses API, and prints the parsed JSON response.  A trimmed deck
context file (JSON produced from /api/extract) can be passed to give the LLM
more signal when resolving ambiguous references such as "the client".

Usage examples:

    export OPENAI_API_KEY=sk-...
    python3 llm_redaction_prompt.py "Redact the client name with [CLIENT]"

    python3 llm_redaction_prompt.py \
        --context deck_preview.json \
        "Mask the primary client and leave everything else intact"

The script never mutates files.  It simply echoes the JSON dictionary returned
by the Responses API so you can inspect and adapt it before wiring it into the
Flask service.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any, Dict, Optional
from pathlib import Path

try:
    from openai import OpenAI
except ImportError as exc:  # pragma: no cover - import guard for local dev
    raise SystemExit(
        "The OpenAI Python SDK is required. Install with 'pip install openai'."
    ) from exc


DEFAULT_MODEL = os.environ.get("OPENAI_MODEL", "gpt-5")

SYSTEM_PROMPT = """You are an assistant that converts redaction instructions
into structured rules. Always respond with compact JSON matching this schema:
{
  "targetClients": ["Acme Incorporated"],
  "replacementToken": "[REDACTED_CLIENT]"
}
Only include client names that are explicitly implied by the instruction plus
the provided context. If unsure, return an empty list and describe the
ambiguity in the notes field.  Do not hallucinate additional fields."""


def build_user_prompt(instructions: str, context: Optional[Dict[str, Any]]) -> str:
    """Compose the user message fed into the Responses API."""
    prompt_parts = [
        "# Instruction",
        instructions.strip() or "(none provided)",
    ]

    if context:
        pretty = json.dumps(context, indent=2, ensure_ascii=False)
        prompt_parts.extend([
            "\n# Deck context",
            pretty,
            "\n# Guidance",
            "Use the context only to resolve which single client/company the user"
            " is referencing. Ignore any text marked as already redacted.",
        ])
    else:
        prompt_parts.append(
            "\n(No additional deck context supplied. Default to parsing the"
            " instruction alone.)"
        )

    prompt_parts.extend(
        [
            "\n# Output format",
            "Return strictly the JSON object described in the system message.",
        ]
    )

    return "\n".join(prompt_parts)


def load_env_files() -> None:
    """Best-effort load of .env files near the script and project root."""

    root_env = Path(__file__).resolve().parents[1] / ".env"

    if not root_env.exists() or not root_env.is_file():
        return

    for raw_line in root_env.read_text(encoding="utf-8").splitlines():
            line = raw_line.strip()
            if not line or line.startswith("#"):
                continue
            if "=" not in line:
                continue
            key, value = line.split("=", 1)
            key = key.strip()
            value = value.strip().strip('"').strip("'")
            os.environ.setdefault(key, value)


def call_responses_api(
    instructions: str,
    context: Optional[Dict[str, Any]] = None,
    model: str = DEFAULT_MODEL,
) -> Dict[str, Any]:
    """Invoke the OpenAI Responses API and parse the JSON payload."""

    api_key = os.environ.get("OPENAI_API_KEY")
    if not api_key:
        raise SystemExit(
            "OPENAI_API_KEY is not set. Export it before running this script."
        )

    client = OpenAI(api_key=api_key)
    user_message = build_user_prompt(instructions, context)

    # MAIN LLM CALL STARTS HERE
    response = client.responses.create(
        model=model,
        input=[
            {
                "role": "system",
                "content": [
                    {"type": "text", "text": SYSTEM_PROMPT},
                ],
            },
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": user_message},
                ],
            },
        ],
        response_format={"type": "json_object"},
        max_output_tokens=400,
        temperature=0,
    )

    content = response.output_text

    try:
        return json.loads(content)
    except json.JSONDecodeError as exc:  # pragma: no cover
        raise RuntimeError(f"Model did not return valid JSON: {content}") from exc


def load_context(path: Optional[str]) -> Optional[Dict[str, Any]]:
    if not path:
        return None

    with open(path, "r", encoding="utf-8") as fh:
        data = json.load(fh)

    # Keep the context small to reduce token usage.
    # Expecting a dict with keys like {"slides": [...]} from /api/extract.
    return data


def main(argv: Optional[list[str]] = None) -> int:
    load_env_files()

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "instructions",
        help="Natural-language redaction request (wrap in quotes)",
    )

    '''
    parser.add_argument(
        "--context",
        help="Optional path to JSON snippet (e.g., trimmed /api/extract output)",
    )
    parser.add_argument(
        "--model",
        default=DEFAULT_MODEL,
        help=f"Responses model to use (default: {DEFAULT_MODEL})",
    )
    '''
    
    args = parser.parse_args(argv)

    context = load_context(args.context)
    result = call_responses_api(args.instructions, context=context, model=args.model)
    json.dump(result, sys.stdout, indent=2, ensure_ascii=False)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())

