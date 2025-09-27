#!/usr/bin/env python3
"""Prototype helper for deriving redaction rules via OpenAI Responses API.

Run this script manually while iterating on prompt design.  It reads
instructions from the command line (or stdin), builds a structured request
for the Responses API, and prints the parsed JSON response.  A trimmed deck
context file (JSON produced from /api/upload) can be passed to give the LLM
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
from pathlib import Path
from time import perf_counter
from typing import Any, Dict, Optional

try:
    from openai import OpenAI
except ImportError as exc:  # pragma: no cover - import guard for local dev
    raise SystemExit(
        "The OpenAI Python SDK is required. Install with 'pip install openai'."
    ) from exc

try:
    from dotenv import load_dotenv
except ImportError as exc:  # pragma: no cover - import guard for local dev
    raise SystemExit(
        "python-dotenv is required. Install with 'pip install python-dotenv'."
    ) from exc

DEFAULT_MODEL = os.environ.get("OPENAI_MODEL", "gpt-5")

SYSTEM_PROMPT = """You are an assistant that converts redaction instructions
into structured rules. Always respond with compact JSON matching this schema:
{
  "targetClients": ["Acme Incorporated"],
  "replacementToken": "[REDACTED_CLIENT]"
}
Only include client names that are explicitly implied by the instruction plus
the provided context. If unsure, return an empty list.
Do not hallucinate additional fields."""

def build_user_prompt(instructions: str) -> str:
    """Compose the user message fed into the Responses API."""

    prompt_parts = [
        "# Instruction",
        instructions.strip() or "(none provided)",
    ]

    prompt_parts.extend(
        [
            "\n# Output format",
            "Return strictly the JSON object described in the system message.",
        ]
    )

    return "\n".join(prompt_parts)

def load_env_files() -> None:
    """Load the repository-wide .env if present using python-dotenv."""

    root_env = Path(__file__).resolve().parents[2] / ".env"
    load_dotenv(root_env)


def call_responses_api(
    instructions: str,
    model: Optional[str] = None,
) -> Dict[str, Any]:
    """Invoke the OpenAI Responses API and parse the JSON payload."""

    api_key = os.environ.get("OPENAI_API_KEY")
    if not api_key:
        raise SystemExit(
            "OPENAI_API_KEY is not set. Export it before running this script."
        )

    if not model:
        model = os.environ.get("OPENAI_MODEL", DEFAULT_MODEL)

    client = OpenAI(api_key=api_key)
    user_message = build_user_prompt(instructions)

    # MAIN LLM CALL STARTS HERE
    started = perf_counter()
    response = client.responses.create(
        model=model,
        instructions=SYSTEM_PROMPT,
        input=[
            {
                "role": "user",
                "content": user_message,
            },
        ],
    )
    latency = perf_counter() - started

    content = response.output_text

    try:
        result = json.loads(content)
    except json.JSONDecodeError as exc:  # pragma: no cover
        raise RuntimeError(f"Model did not return valid JSON: {content}") from exc

    # print latency
    
    print(f"[timing] responses.create latency: {latency:.3f}s", file=sys.stderr)

    return result


def load_context(path: Optional[str]) -> Optional[Dict[str, Any]]:
    if not path:
        return None

    with open(path, "r", encoding="utf-8") as fh:
        data = json.load(fh)

    # Keep the context small to reduce token usage.
    # Expecting a dict with keys like {"slides": [...]} from /api/upload.
    return data


def main(argv: Optional[list[str]] = None) -> int:
    load_env_files()

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "instructions",
        help="Natural-language redaction request (wrap in quotes)",
    )

    args = parser.parse_args(argv)

    context_path = getattr(args, "context", None)
    model_arg = getattr(args, "model", os.environ.get("OPENAI_MODEL", DEFAULT_MODEL))

    context = load_context(context_path)
    result = call_responses_api(args.instructions, model=model_arg)
    json.dump(result, sys.stdout, indent=2, ensure_ascii=False)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
