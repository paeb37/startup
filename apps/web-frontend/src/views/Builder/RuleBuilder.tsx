import { useEffect, useMemo, useRef, useState } from "react";
import { getDocument } from "pdfjs-dist";

import { SUPABASE_CONFIGURED } from "../../config/env";
import { EmptyState } from "../../components/common/EmptyState";
import { isDeckResponse } from "../../types/deck";
import type { BuilderSeed, RenderResult } from "../../types/app";
import type { RuleActionRecord, RedactResponsePayload } from "../../types/rules";
import { useSupabaseRules, useSupabaseRuleActions } from "../../hooks/useRules";
import { AddRulePanel } from "./components/AddRulePanel";
import { ActiveRulesPanel } from "./components/ActiveRulesPanel";
import { UploadDeckButton } from "./components/UploadDeckButton";
import { PreviewToggleButton } from "./components/PreviewToggleButton";
import { ThumbRail } from "./pdf/ThumbRail";
import { MainSlideViewer } from "./pdf/MainSlideViewer";

export function RuleBuilder({ seed }: { seed: BuilderSeed | null }) {
  const [render, setRender] = useState<RenderResult | null>(seed?.render ?? null);
  const [selectedPage, setSelectedPage] = useState(1);
  const [previewEnabled, setPreviewEnabled] = useState(false);
  const [previewRender, setPreviewRender] = useState<RenderResult | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);

  const previewAbortRef = useRef<AbortController | null>(null);

  const deckId = seed?.deckId ?? null;
  const deckPayload = seed && isDeckResponse(seed.deck) ? seed.deck : null;

  const rulesState = useSupabaseRules(deckId);
  const {
    actions: ruleActions,
    loading: actionsLoading,
    error: actionsError,
    reload: reloadRuleActions,
  } = useSupabaseRuleActions(deckId, previewEnabled);

  const slideActions: RuleActionRecord[] = useMemo(() => {
    if (!previewEnabled || !ruleActions.length) return [];
    return ruleActions.filter((action) => action.slide_no === selectedPage);
  }, [previewEnabled, ruleActions, selectedPage]);

  const currentSlideCount = slideActions.length;
  const previewAvailable = Boolean(deckId && SUPABASE_CONFIGURED);
  const previewDisabledMessage = deckId && !SUPABASE_CONFIGURED ? "Connect Supabase to preview." : null;
  const noActionsForSlide = previewEnabled && !actionsLoading && currentSlideCount === 0;

  const [redactBusy, setRedactBusy] = useState(false);
  const [redactError, setRedactError] = useState<string | null>(null);
  const [redactResult, setRedactResult] = useState<RedactResponsePayload | null>(null);

  const handleRedactDeck = async () => {
    if (!deckId) {
      setRedactError("Upload a deck first.");
      return;
    }
    if (!SUPABASE_CONFIGURED) {
      setRedactError("Supabase configuration missing.");
      return;
    }

    setRedactBusy(true);
    setRedactError(null);
    setRedactResult(null);

    try {
      const response = await fetch(`/api/decks/${deckId}/redact`, {
        method: "POST",
        headers: { Accept: "application/json" },
      });

      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        throw new Error(payload?.error ?? `Redaction failed (${response.status})`);
      }

      setRedactResult(payload as RedactResponsePayload);
      setRedactError(null);
      reloadRuleActions();
    } catch (err) {
      setRedactError(err instanceof Error ? err.message : "Failed to create redacted deck");
      setRedactResult(null);
    } finally {
      setRedactBusy(false);
    }
  };

  useEffect(() => {
    return () => {
      if (render?.pdfUrl) URL.revokeObjectURL(render.pdfUrl);
    };
  }, [render?.pdfUrl]);

  useEffect(() => {
    if (!seed) return;
    setRender(seed.render);
    setSelectedPage(1);
    setPreviewEnabled(false);
    setPreviewRender(null);
    setPreviewError(null);
    setPreviewLoading(false);
    previewAbortRef.current?.abort();
    previewAbortRef.current = null;
    setRedactResult(null);
    setRedactError(null);
    setRedactBusy(false);
  }, [seed]);

  const baseDoc = render?.doc ?? null;
  const previewDoc = previewRender?.doc ?? null;
  const activeDoc = previewEnabled && previewDoc ? previewDoc : baseDoc;
  const viewerKey = previewEnabled && previewDoc ? `preview-${selectedPage}` : "original";
  const thumbnailDoc = baseDoc;
  const viewerPageNum = previewEnabled && previewDoc ? 1 : selectedPage;

  useEffect(() => {
    if (!baseDoc) return;
    const maxPage = Math.max(1, baseDoc.numPages);
    if (selectedPage > maxPage) {
      setSelectedPage(maxPage);
    } else if (selectedPage < 1) {
      setSelectedPage(1);
    }
  }, [baseDoc, selectedPage]);

  useEffect(() => {
    if (previewEnabled) {
      setPreviewEnabled(false);
    }
  }, [selectedPage]);

  useEffect(() => {
    if (!previewEnabled) {
      previewAbortRef.current?.abort();
      previewAbortRef.current = null;
      if (previewRender?.pdfUrl) {
        URL.revokeObjectURL(previewRender.pdfUrl);
      }
      setPreviewRender(null);
      setPreviewError(null);
      setPreviewLoading(false);
    }
  }, [previewEnabled, previewRender?.pdfUrl]);

  useEffect(() => {
    return () => {
      if (previewRender?.pdfUrl) {
        URL.revokeObjectURL(previewRender.pdfUrl);
      }
    };
  }, [previewRender?.pdfUrl]);

  const slideActionFingerprint = useMemo(() => {
    if (!previewEnabled) return "";
    return slideActions
      .map((action) => `${action.id}:${action.new_text ?? ""}:${action.original_text ?? ""}`)
      .join("|");
  }, [previewEnabled, slideActions]);

  useEffect(() => {
    if (!deckId || !previewEnabled || actionsLoading || slideActions.length === 0) {
      return;
    }

    const controller = new AbortController();
    previewAbortRef.current?.abort();
    previewAbortRef.current = controller;

    const loadPreview = async () => {
      setPreviewLoading(true);
      setPreviewError(null);

      try {
        const response = await fetch(`/api/decks/${deckId}/preview`, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
          },
          body: JSON.stringify({ slide: selectedPage }),
          signal: controller.signal,
        });

        if (!response.ok) {
          const message = await response.text().catch(() => "");
          throw new Error(message || `Preview request failed (${response.status})`);
        }

        const arrayBuffer = await response.arrayBuffer();
        const bytes = new Uint8Array(arrayBuffer);
        const blob = new Blob([bytes], { type: "application/pdf" });
        const pdfUrl = URL.createObjectURL(blob);
        const doc = await getDocument({ data: bytes }).promise;
        setPreviewRender({ pdfUrl, doc });
      } catch (err) {
        if (controller.signal.aborted) return;
        console.error("Preview generation failed", err);
        setPreviewRender(null);
        setPreviewError(err instanceof Error ? err.message : "Failed to generate preview");
      } finally {
        if (!controller.signal.aborted) {
          setPreviewLoading(false);
        }
      }
    };

    loadPreview();

    return () => {
      controller.abort();
    };
  }, [deckId, previewEnabled, selectedPage, slideActionFingerprint, actionsLoading]);

  return (
    <div style={{ height: "100%", display: "grid", gridTemplateRows: "auto 1fr", minHeight: 0 }}>
      <div style={{ display: "grid", gridTemplateColumns: "240px 1fr 220px", height: "100%", minHeight: 0 }}>
        <aside
          style={{
            padding: "16px 16px 18px",
            borderRight: "1px solid #eee",
            background: "#fff",
            display: "grid",
            gridTemplateRows: "auto 1fr auto",
            gap: 16,
            minHeight: 0,
          }}
        >
          <AddRulePanel
            deckId={deckId}
            deck={deckPayload}
            initialInstructions={seed?.instructions ?? ""}
            supabaseConfigured={rulesState.configured}
            reloadRules={rulesState.reload}
            reloadRuleActions={reloadRuleActions}
            onRuleCreated={() => setPreviewEnabled(true)}
          />

          <div style={{ gap: 8, overflowY: "auto", minHeight: 0 }}>
            <ActiveRulesPanel deckId={deckId} rulesState={rulesState} />
          </div>

          <div style={{ display: "grid", gap: 8 }}>
            <UploadDeckButton disabled={!deckId || redactBusy} busy={redactBusy} onClick={handleRedactDeck} />
            {redactError && <div style={{ fontSize: 12, color: "#b91c1c" }}>{redactError}</div>}
            {redactResult && (
              <div style={{ fontSize: 12, color: "#047857", display: "grid", gap: 4 }}>
                <span>
                  Saved redacted deck
                  {typeof redactResult.actionsApplied === "number"
                    ? ` • ${redactResult.actionsApplied} change${redactResult.actionsApplied === 1 ? "" : "s"} applied`
                    : ""}
                  .
                </span>
                {redactResult.downloadUrl && (
                  <a
                    href={redactResult.downloadUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    style={{ color: "#2563eb", textDecoration: "none", fontWeight: 600 }}
                  >
                    Download redacted deck
                  </a>
                )}
              </div>
            )}
          </div>
        </aside>

        <main style={{ display: "grid", gridTemplateRows: "auto 1fr", minHeight: 0, background: "#f9fafb" }}>
          <section style={{ padding: 16, borderBottom: "1px solid #eee" }}>
            <div
              style={{
                display: "flex",
                alignItems: "center",
                justifyContent: "space-between",
                gap: 12,
                flexWrap: "wrap",
              }}
            >
              <div style={{ fontSize: 16, fontWeight: 600, color: "#0f172a" }}>Slide Preview</div>
              <div style={{ display: "flex", alignItems: "center", gap: 10, flexWrap: "wrap" }}>
                <PreviewToggleButton
                  enabled={previewEnabled}
                  onToggle={() => setPreviewEnabled((prev) => !prev)}
                  busy={previewEnabled && (previewLoading || actionsLoading)}
                  disabled={!previewAvailable}
                />
                {previewEnabled && !previewLoading && !actionsLoading && currentSlideCount > 0 && (
                  <span style={{ fontSize: 12, color: "#1d4ed8" }}>
                    {`${currentSlideCount} redaction${currentSlideCount === 1 ? "" : "s"} displayed`}
                  </span>
                )}
                {previewEnabled && previewLoading && (
                  <span style={{ fontSize: 12, color: "#64748b" }}>Generating preview…</span>
                )}
                {noActionsForSlide && (
                  <span style={{ fontSize: 12, color: "#64748b" }}>No redactions on this slide.</span>
                )}
                {previewEnabled && actionsError && (
                  <span style={{ fontSize: 12, color: "#b91c1c" }}>{actionsError}</span>
                )}
                {previewEnabled && previewError && (
                  <span style={{ fontSize: 12, color: "#b91c1c" }}>{previewError}</span>
                )}
                {previewDisabledMessage && (
                  <span style={{ fontSize: 12, color: "#b91c1c" }}>{previewDisabledMessage}</span>
                )}
              </div>
            </div>
          </section>

          <section style={{ minHeight: 0, position: "relative" }}>
            {actionsLoading && previewEnabled && (
              <div
                style={{
                  position: "absolute",
                  top: 16,
                  left: "50%",
                  transform: "translateX(-50%)",
                  background: "rgba(15,23,42,0.9)",
                  color: "#fff",
                  padding: "6px 12px",
                  borderRadius: 999,
                  fontSize: 12,
                  boxShadow: "0 12px 24px rgba(15,23,42,0.2)",
                }}
              >
                Loading rules…
              </div>
            )}

            {activeDoc ? (
              <MainSlideViewer
                key={viewerKey}
                doc={activeDoc}
                pageNum={viewerPageNum}
                highlights={slideActions}
                previewEnabled={previewEnabled}
                loadingHighlights={actionsLoading || previewLoading}
                showHighlights={false}
              />
            ) : (
              <EmptyState message="Upload a .pptx to see a large preview here." />
            )}
          </section>
        </main>

        <aside style={{ padding: 12, borderLeft: "1px solid #eee", overflow: "auto", background: "#fff" }}>
          <h2 style={{ fontSize: 18, margin: "8px 8px 12px" }}>Slide Deck</h2>
          {thumbnailDoc ? (
            <ThumbRail
              key={`thumb-${render?.pdfUrl ?? "original"}`}
              doc={thumbnailDoc}
              selected={selectedPage}
              onSelect={setSelectedPage}
            />
          ) : (
            <EmptyState small message="No slides yet." />
          )}
        </aside>
      </div>
    </div>
  );
}
