import { useState } from "react";

import { uploadDeck } from "../../services/upload";
import { LIBRARY_INDUSTRIES, LIBRARY_TYPES } from "../../services/decks";
import type { BuilderSeed } from "../../types/app";

type UploadViewProps = {
  onComplete: (payload: BuilderSeed) => void;
  onCancel: () => void;
};

export function UploadView({ onComplete, onCancel }: UploadViewProps) {
  const [file, setFile] = useState<File | null>(null);
  const [instructions, setInstructions] = useState("");
  const [industry, setIndustry] = useState("");
  const [deckType, setDeckType] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (!file) {
      setError("Select a .pptx file first.");
      return;
    }
    if (!industry) {
      setError("Select an industry.");
      return;
    }
    if (!deckType) {
      setError("Select a deck type.");
      return;
    }

    setBusy(true);
    setError(null);

    try {
      const { deck, render, deckId } = await uploadDeck(file, instructions, industry, deckType);
      onComplete({
        deck,
        render,
        instructions: instructions.trim(),
        fileName: file.name,
        deckId,
      });
    } catch (err: any) {
      setError(err?.message ?? "Upload failed");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div
      style={{
        height: "100%",
        display: "grid",
        placeItems: "center",
        padding: 24,
      }}
    >
      <form
        onSubmit={handleSubmit}
        style={{
          width: "100%",
          maxWidth: 520,
          background: "#ffffff",
          borderRadius: 20,
          padding: "32px 36px",
          boxShadow: "0 20px 45px rgba(15,23,42,0.12)",
          display: "grid",
          gap: 20,
        }}
      >
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <h1 style={{ margin: 0, fontSize: 22, color: "#0f172a" }}>Upload slide</h1>
          <button
            type="button"
            onClick={onCancel}
            style={{
              background: "transparent",
              border: "none",
              color: "#2563eb",
              fontSize: 14,
              cursor: "pointer",
            }}
          >
            Cancel
          </button>
        </div>

        <label
          style={{
            display: "grid",
            gap: 8,
            fontSize: 14,
            color: "#1f2937",
          }}
        >
          <span style={{ fontWeight: 600 }}>Choose PowerPoint</span>
          <input
            type="file"
            accept=".pptx,application/vnd.openxmlformats-officedocument.presentationml.presentation"
            onChange={(event) => {
              setFile(event.target.files?.[0] ?? null);
              setError(null);
            }}
            disabled={busy}
          />
        </label>

        <div style={{ display: "grid", gap: 14, gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))" }}>
          <label style={{ display: "grid", gap: 6, fontSize: 14, color: "#1f2937" }}>
            <span style={{ fontWeight: 600 }}>Industry</span>
            <select
              value={industry}
              onChange={(event) => {
                setIndustry(event.target.value);
                setError(null);
              }}
              disabled={busy}
              style={{
                borderRadius: 12,
                border: "1px solid #d1d5db",
                padding: "10px 12px",
                fontSize: 14,
                color: industry ? "#0f172a" : "#9ca3af",
                background: "#fff",
              }}
            >
              <option value="" disabled>
                Select industry
              </option>
              {LIBRARY_INDUSTRIES.map((option) => (
                <option key={option} value={option}>
                  {option}
                </option>
              ))}
            </select>
          </label>

          <label style={{ display: "grid", gap: 6, fontSize: 14, color: "#1f2937" }}>
            <span style={{ fontWeight: 600 }}>Deck type</span>
            <select
              value={deckType}
              onChange={(event) => {
                setDeckType(event.target.value);
                setError(null);
              }}
              disabled={busy}
              style={{
                borderRadius: 12,
                border: "1px solid #d1d5db",
                padding: "10px 12px",
                fontSize: 14,
                color: deckType ? "#0f172a" : "#9ca3af",
                background: "#fff",
              }}
            >
              <option value="" disabled>
                Select deck type
              </option>
              {LIBRARY_TYPES.map((option) => (
                <option key={option} value={option}>
                  {option}
                </option>
              ))}
            </select>
          </label>
        </div>

        <label style={{ display: "grid", gap: 8, fontSize: 14, color: "#1f2937" }}>
          <span style={{ fontWeight: 600 }}>Redaction instructions</span>
          <textarea
            value={instructions}
            onChange={(event) => {
              setInstructions(event.target.value);
              setError(null);
            }}
            placeholder="e.g. Redact client names and revenue figures"
            rows={4}
            style={{
              resize: "vertical",
              padding: 12,
              borderRadius: 12,
              border: "1px solid #d1d5db",
              fontSize: 14,
              color: "#0f172a",
            }}
            disabled={busy}
          />
        </label>

        {error && <div style={{ color: "#b91c1c", fontSize: 13 }}>{error}</div>}

        <button
          type="submit"
          disabled={!file || busy}
          style={{
            background: "#111827",
            color: "#fff",
            border: "none",
            borderRadius: 999,
            padding: "12px 18px",
            fontSize: 15,
            fontWeight: 600,
            cursor: file && !busy ? "pointer" : "not-allowed",
            opacity: !file || busy ? 0.65 : 1,
          }}
        >
          {busy ? "Uploading..." : "Upload & open builder"}
        </button>
      </form>
    </div>
  );
}
