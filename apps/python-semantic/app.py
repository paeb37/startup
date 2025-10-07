from __future__ import annotations

import logging
from typing import Any, Dict

from flask import Flask, jsonify, request
from flask_cors import CORS
from pydantic import BaseModel, ValidationError

import chat
import redact

logging.basicConfig(level=logging.INFO)


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
