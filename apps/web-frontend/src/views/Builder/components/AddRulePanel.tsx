import { useEffect, useRef, useState } from "react";

import { SEMANTIC_URL } from "../../../config/env";
import type { Deck } from "../../../types/deck";

export type AddRulePanelProps = {
  deckId: string | null;
  deck: Deck | null;
  initialInstructions?: string;
  supabaseConfigured: boolean;
  reloadRules?: () => void;
  reloadRuleActions?: () => void;
  onRuleCreated?: () => void;
};

export function AddRulePanel({
  deckId,
  deck,
  initialInstructions,
  supabaseConfigured,
  reloadRules,
  reloadRuleActions,
  onRuleCreated,
}: AddRulePanelProps) {
  const [showOverlay, setShowOverlay] = useState(false);
  const [instructions, setInstructions] = useState(initialInstructions?.trim() ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);

  const disabledReason = !supabaseConfigured
    ? "Connect Supabase to add redaction rules."
    : !deckId
      ? "Upload a deck first."
      : !deck
        ? "Preview unavailable for this deck."
        : null;

  useEffect(() => {
    setInstructions(initialInstructions?.trim() ?? "");
  }, [initialInstructions, deckId]);

  useEffect(() => {
    if (!showOverlay) {
      setBusy(false);
      return;
    }

    setError(null);
    requestAnimationFrame(() => textareaRef.current?.focus());
  }, [showOverlay]);

  const handleClose = () => {
    if (busy) return;
    setShowOverlay(false);
    setError(null);
  };

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (busy) return;

    const trimmed = instructions.trim();
    if (!trimmed) {
      setError("Enter redaction instructions.");
      return;
    }
    if (!deckId) {
      setError("Upload a deck before adding rules.");
      return;
    }
    if (!deck) {
      setError("Deck preview data missing; upload again and retry.");
      return;
    }
    if (!supabaseConfigured) {
      setError("Supabase configuration missing.");
      return;
    }

    setBusy(true);
    setError(null);

    const payload = {
      deckId,
      instructions: trimmed,
      deck,
    };

    try {
      const response = await fetch(`${SEMANTIC_URL}/redact`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });

      let responseJson: any = null;
      try {
        responseJson = await response.json();
      } catch {
        responseJson = null;
      }

      if (!response.ok) {
        const message =
          responseJson?.error ?? responseJson?.message ?? `Semantic redaction failed (${response.status})`;
        throw new Error(message);
      }

      reloadRules?.();
      reloadRuleActions?.();
      onRuleCreated?.();
      setShowOverlay(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to create rule");
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <button
        type="button"
        onClick={() => {
          if (disabledReason) return;
          setShowOverlay(true);
        }}
        disabled={Boolean(disabledReason)}
        style={{
          appearance: "none",
          border: "1px solid #2563eb",
          borderRadius: 10,
          padding: "10px 14px",
          background: disabledReason ? "#e0e7ff" : "#eff6ff",
          color: disabledReason ? "#94a3b8" : "#1d4ed8",
          fontSize: 14,
          fontWeight: 600,
          cursor: disabledReason ? "not-allowed" : "pointer",
          boxShadow: disabledReason ? "none" : "0 6px 16px rgba(59,130,246,0.18)",
          transition: "transform 0.15s ease",
        }}
      >
        + Add rule
      </button>
      {disabledReason && (
        <div style={{ fontSize: 12, color: "#9ca3af", marginTop: 6 }}>{disabledReason}</div>
      )}

      {showOverlay && (
        <div
          style={{
            position: "fixed",
            inset: 0,
            background: "rgba(15, 23, 42, 0.45)",
            display: "grid",
            placeItems: "center",
            zIndex: 1000,
            padding: 16,
          }}
        >
          <div
            style={{
              width: "min(520px, 90vw)",
              background: "#ffffff",
              borderRadius: 20,
              padding: "26px 28px",
              boxShadow: "0 24px 60px rgba(15,23,42,0.35)",
              display: "grid",
              gap: 18,
            }}
          >
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
              <h2 style={{ margin: 0, fontSize: 19, color: "#0f172a" }}>Add redaction rule</h2>
              <button
                type="button"
                onClick={handleClose}
                style={{
                  appearance: "none",
                  border: "none",
                  background: "transparent",
                  fontSize: 18,
                  color: "#64748b",
                  cursor: busy ? "not-allowed" : "pointer",
                }}
                disabled={busy}
              >
                ✕
              </button>
            </div>

            <div style={{ fontSize: 14, color: "#475569", lineHeight: 1.5 }}>
              Describe what should be redacted. Dexter will generate rule actions, store them, and refresh the preview.
            </div>

            <form onSubmit={handleSubmit} style={{ display: "grid", gap: 16 }}>
              <label style={{ display: "grid", gap: 8, fontSize: 14, color: "#0f172a" }}>
                <span style={{ fontWeight: 600 }}>Redaction instructions</span>
                <textarea
                  ref={textareaRef}
                  value={instructions}
                  onChange={(event) => setInstructions(event.target.value)}
                  rows={5}
                  placeholder="e.g. Remove all client names and blur any revenue numbers."
                  style={{
                    resize: "vertical",
                    padding: 12,
                    borderRadius: 12,
                    border: "1px solid #d1d5db",
                    fontSize: 14,
                    color: "#0f172a",
                    lineHeight: 1.5,
                  }}
                  disabled={busy}
                />
              </label>

              {error && <div style={{ color: "#b91c1c", fontSize: 13 }}>{error}</div>}

              <div style={{ display: "flex", justifyContent: "flex-end", gap: 12 }}>
                <button
                  type="button"
                  onClick={handleClose}
                  disabled={busy}
                  style={{
                    border: "none",
                    background: "transparent",
                    color: "#64748b",
                    fontWeight: 600,
                    fontSize: 14,
                    cursor: busy ? "not-allowed" : "pointer",
                  }}
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={busy}
                  style={{
                    border: "none",
                    borderRadius: 10,
                    padding: "10px 18px",
                    fontSize: 14,
                    fontWeight: 600,
                    color: "#fff",
                    background: busy ? "#94a3b8" : "#111827",
                    cursor: busy ? "not-allowed" : "pointer",
                    minWidth: 140,
                  }}
                >
                  {busy ? "Saving…" : "Save rule"}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </>
  );
}
