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

SYSTEM_PROMPT = """You are an assistant that converts redaction instructions into structured rules. Respond with JSON matching:
{
  "targetClients": ["Acme Incorporated"],
  "replacementToken": "[REDACTED_CLIENT]",
  "notes": "optional clarification"
}
Only include clients explicitly referenced. Leave targetClients empty if unsure."""

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
    instructions: Optional[str] = None

app = Flask(__name__)
CORS(app, resources={r"/api/*": {"origins": "*"}})

def interpret_instructions(instructions: str) -> Dict[str, object]:
    low = instructions.lower()
    mask_client_names = "client" in low and "name" in low
    mask_revenue = any(token in low for token in ("revenue", "figure", "amount", "sales"))
    mask_emails = "email" in low or "mail" in low

    keywords: List[str] = []

    quoted = re.findall(r'"([^"\\]+)"', instructions)
    if quoted:
        keywords.extend(quoted)
    else:
        redact_chunks = re.findall(r"redact ([^.]+)", low)
        for chunk in redact_chunks:
            tokens = [token.strip() for token in re.split(r",|and", chunk) if token.strip()]
            keywords.extend(tokens)

    keywords = [kw for kw in {kw.strip(): None for kw in keywords}.keys() if kw]

    return {
        "maskClientNames": mask_client_names,
        "maskRevenue": mask_revenue,
        "maskEmails": mask_emails,
        "keywords": keywords,
        "replacementToken": "[REDACTED]"
    }


def build_user_prompt(instructions: str) -> str:
    prompt_parts = [
        "# Instruction",
        instructions.strip() or "(none provided)",
        "\n# Output format",
        "Return strictly the JSON object described in the system message.",
    ]
    return "\n".join(prompt_parts)


def call_responses_api(instructions: str) -> Tuple[Optional[Dict[str, object]], Dict[str, object]]:
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

    meta = {"model": model, "responseSeconds": round(latency, 3)}

    try:
        parsed = json.loads(response.output_text)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"invalid JSON from model: {exc}")

    return parsed, meta


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
    rules = interpret_instructions(instructions)
    llm_meta: Dict[str, object] = {}

    if instructions:
        try:
            llm_result, meta = call_responses_api(instructions)
            llm_meta = meta
            if isinstance(llm_result, dict):
                targets = llm_result.get("targetClients") or []
                replacement = llm_result.get("replacementToken") or rules.get("replacementToken")

                if targets:
                    rules["keywords"] = [t for t in targets if isinstance(t, str) and t.strip()] # type: ignore
                    if rules["keywords"]:
                        rules["maskClientNames"] = True

                if replacement:
                    rules["replacementToken"] = replacement

                notes = llm_result.get("notes")
                if notes:
                    llm_meta["notes"] = notes
        except Exception as ex:
            llm_meta = {"error": str(ex)}

    return jsonify({
        "instructions": instructions,
        "rules": rules,
        "meta": llm_meta
    })


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=8000, debug=True)
