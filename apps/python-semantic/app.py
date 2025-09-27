import re
from typing import Dict, List, Optional

from flask import Flask, request, jsonify
from flask_cors import CORS
from pydantic import BaseModel, ValidationError

app = Flask(__name__)
CORS(app, resources={r"/api/*": {"origins": "*"}})  # dev-friendly CORS

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
        "keywords": keywords
    }


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=8000, debug=True)
