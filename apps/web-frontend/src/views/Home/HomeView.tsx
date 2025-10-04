import { useEffect, useState } from "react";
import type { FormEvent } from "react";

import {
  FALLBACK_DECKS,
  buildSlideImageUrl,
  formatRelativeDate,
  normalizeDeckRecord,
  normalizeSlideReferences,
} from "../../services/decks";
import { usePdfDocument, usePageBitmap } from "../../hooks/usePdf";
import type { DeckPreview, SlideReference } from "../../types/deck";

type ChatMsg = {
  id: string;
  role: "user" | "assistant";
  content: string;
  sources?: SlideReference[];
};

type HomeMode = "landing" | "chat";

export function HomeView() {
  const [mode, setMode] = useState<HomeMode>("landing");
  const [query, setQuery] = useState("");
  const [messages, setMessages] = useState<ChatMsg[]>([]);
  const [sending, setSending] = useState(false);
  const [decks, setDecks] = useState<DeckPreview[]>([]);
  const [loadingDecks, setLoadingDecks] = useState(true);
  const [usedFallback, setUsedFallback] = useState(false);

  useEffect(() => {
    let cancelled = false;

    async function loadDecks() {
      setLoadingDecks(true);
      try {
        const res = await fetch("/api/decks?limit=3");
        if (!res.ok) {
          throw new Error(`deck request failed: ${res.status}`);
        }
        const payload = await res.json();
        const list = Array.isArray(payload)
          ? payload
          : Array.isArray(payload?.decks)
            ? payload.decks
            : [];
        const normalized = list
          .map((item: any) => normalizeDeckRecord(item))
          .filter(Boolean) as DeckPreview[];

        if (!cancelled) {
          setDecks(normalized);
          setUsedFallback(false);
        }
      } catch (err) {
        console.error("Failed to load decks", err);
        if (!cancelled) {
          setDecks(FALLBACK_DECKS);
          setUsedFallback(true);
        }
      } finally {
        if (!cancelled) {
          setLoadingDecks(false);
        }
      }
    }

    loadDecks();
    return () => {
      cancelled = true;
    };
  }, []);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    const text = query.trim();
    if (!text || sending) return;

    const userMsg: ChatMsg = { id: crypto.randomUUID(), role: "user", content: text };
    setMessages((prev) => [...prev, userMsg]);
    setQuery("");
    setMode("chat");
    setSending(true);

    try {
      const res = await fetch("http://localhost:8000/api/chat", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ message: text }),
      });
      const data = await res.json();
      console.debug("/api/chat response", data);
      const reply = typeof data?.reply === "string" ? data.reply : "Sorry, something went wrong.";
      const sources = normalizeSlideReferences(data?.sources);
      console.debug("normalized slide sources", sources);
      setMessages((prev) => [
        ...prev,
        { id: crypto.randomUUID(), role: "assistant", content: reply, sources },
      ]);
    } catch (err) {
      console.error("Chat request failed", err);
      setMessages((prev) => [
        ...prev,
        { id: crypto.randomUUID(), role: "assistant", content: "Network error. Try again." },
      ]);
    } finally {
      setSending(false);
    }
  }

  if (mode === "landing") {
    const decksToRender = decks.slice(0, 3);

    return (
      <div style={{ height: "100%", overflowY: "auto", background: "#f3f4f6" }}>
        <div
          style={{
            maxWidth: 900,
            margin: "0 auto",
            padding: "72px 24px 80px",
            display: "grid",
            gap: 48,
            textAlign: "center",
          }}
        >
          <div style={{ display: "grid", gap: 12 }}>
            <h1
              style={{
                margin: 0,
                fontSize: "clamp(48px, 10vw, 96px)",
                fontWeight: 700,
                letterSpacing: -1.5,
                color: "#0f172a",
              }}
            >
              Dexter
            </h1>
            <p
              style={{
                margin: 0,
                fontSize: "clamp(18px, 3vw, 24px)",
                color: "#475569",
              }}
            >
              All your information in one place.
            </p>
          </div>

          <form onSubmit={handleSubmit} style={{ width: "100%" }}>
            <div
              style={{
                display: "flex",
                alignItems: "center",
                gap: 16,
                background: "#ffffff",
                borderRadius: 999,
                border: "1px solid #d1d5db",
                padding: "16px 22px",
                boxShadow: "0 24px 48px rgba(15,23,42,0.12)",
              }}
            >
              <span style={{ display: "inline-flex", alignItems: "center", justifyContent: "center", width: 32, height: 32 }}>
                <SearchIcon />
              </span>
              <input
                type="text"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="Ask Dexter anything about your decks"
                style={{
                  flex: 1,
                  border: "none",
                  outline: "none",
                  fontSize: 18,
                  color: "#111827",
                  background: "transparent",
                }}
              />
              <button
                type="submit"
                disabled={!query.trim() || sending}
                style={{
                  background: "#111827",
                  color: "#fff",
                  border: 0,
                  borderRadius: 999,
                  padding: "10px 20px",
                  fontWeight: 600,
                  fontSize: 15,
                  cursor: query.trim() && !sending ? "pointer" : "not-allowed",
                  opacity: query.trim() && !sending ? 1 : 0.6,
                }}
              >
                {sending ? "Searching..." : "Search"}
              </button>
            </div>
          </form>

          <section style={{ display: "grid", gap: 18, textAlign: "left" }}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
              <h2 style={{ margin: 0, fontSize: 20, color: "#0f172a" }}>Recent decks</h2>
              {usedFallback ? (
                <span style={{ fontSize: 12, color: "#9ca3af" }}>Sample decks shown</span>
              ) : decksToRender.length > 0 ? (
                <span style={{ fontSize: 12, color: "#9ca3af" }}>
                  {decksToRender.length} {decksToRender.length === 1 ? "deck" : "decks"}
                </span>
              ) : null}
            </div>

            <div
              style={{
                display: "grid",
                gap: 18,
                gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))",
              }}
            >
              {loadingDecks ? (
                Array.from({ length: 3 }).map((_, idx) => <DeckThumbnailSkeleton key={idx} />)
              ) : decksToRender.length > 0 ? (
                decksToRender.map((deck) => <DeckThumbnailCard key={deck.id} deck={deck} />)
              ) : (
                <div
                  style={{
                    border: "1px dashed #d1d5db",
                    borderRadius: 16,
                    padding: "28px",
                    color: "#6b7280",
                    display: "flex",
                    alignItems: "center",
                    justifyContent: "center",
                  }}
                >
                  Upload a slide deck to see it here.
                </div>
              )}
            </div>
          </section>
        </div>
      </div>
    );
  }

  return (
    <div
      style={{
        height: "100%",
        display: "grid",
        gridTemplateRows: "1fr auto",
        background: "#f3f4f6",
      }}
    >
      <div
        style={{
          overflowY: "auto",
          padding: "40px 24px 32px",
          display: "flex",
          flexDirection: "column",
          gap: 18,
        }}
      >
        {messages.map((message) => (
          <ChatMessage key={message.id} message={message} />
        ))}
        {sending && <AssistantTypingIndicator />}
      </div>

      <form onSubmit={handleSubmit} style={{ padding: "16px 24px", background: "#ffffff", borderTop: "1px solid #e5e7eb" }}>
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: 16,
            background: "#f8fafc",
            borderRadius: 999,
            border: "1px solid #d1d5db",
            padding: "14px 18px",
            boxShadow: "0 18px 36px rgba(15,23,42,0.08)",
          }}
        >
          <span style={{ display: "inline-flex", alignItems: "center", justifyContent: "center", width: 28, height: 28 }}>
            <SearchIcon />
          </span>
          <input
            type="text"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Ask anything"
            style={{
              flex: 1,
              border: "none",
              outline: "none",
              fontSize: 16,
              background: "transparent",
              color: "#111827",
            }}
          />
          <button
            type="submit"
            disabled={!query.trim() || sending}
            style={{
              background: "#111827",
              color: "#fff",
              border: 0,
              borderRadius: 999,
              padding: "10px 20px",
              fontWeight: 600,
              cursor: query.trim() && !sending ? "pointer" : "not-allowed",
              opacity: query.trim() && !sending ? 1 : 0.6,
            }}
          >
            Send
          </button>
        </div>
      </form>
    </div>
  );
}

function ChatMessage({ message }: { message: ChatMsg }) {
  if (message.role === "assistant") {
    return <AssistantMessage text={message.content} sources={message.sources} />;
  }
  return <UserMessage text={message.content} />;
}

function AssistantMessage({ text, sources }: { text: string; sources?: SlideReference[] }) {
  return (
    <div style={{ display: "flex", gap: 12, alignItems: "flex-start" }}>
      <div
        style={{
          width: 36,
          height: 36,
          borderRadius: "50%",
          background: "#111827",
          color: "#fff",
          display: "grid",
          placeItems: "center",
          fontWeight: 600,
        }}
      >
        D
      </div>
      <div style={{ display: "grid", gap: 12, maxWidth: "min(720px, 80%)" }}>
        <div
          style={{
            color: "#0f172a",
            lineHeight: 1.65,
            whiteSpace: "pre-wrap",
            fontSize: 16,
          }}
        >
          {text}
        </div>
        {sources && sources.length > 0 && <SlideReferenceList references={sources} />}
      </div>
    </div>
  );
}

function AssistantTypingIndicator() {
  return (
    <div style={{ display: "flex", gap: 12, alignItems: "flex-start" }}>
      <div
        style={{
          width: 36,
          height: 36,
          borderRadius: "50%",
          background: "#111827",
          color: "#fff",
          display: "grid",
          placeItems: "center",
          fontWeight: 600,
        }}
      >
        D
      </div>
      <div
        style={{
          display: "inline-flex",
          alignItems: "center",
          color: "#6b7280",
          fontSize: 16,
          fontStyle: "italic",
        }}
      >
        Thinking...
      </div>
    </div>
  );
}

function UserMessage({ text }: { text: string }) {
  return (
    <div style={{ display: "flex", justifyContent: "flex-end" }}>
      <div
        style={{
          background: "#e5e7eb",
          color: "#111827",
          borderRadius: 18,
          borderBottomRightRadius: 4,
          padding: "12px 16px",
          maxWidth: "min(620px, 80%)",
          lineHeight: 1.6,
          whiteSpace: "pre-wrap",
          border: "1px solid #d1d5db",
        }}
      >
        {text}
      </div>
    </div>
  );
}

function SlideReferenceList({ references }: { references: SlideReference[] }) {
  if (references.length === 0) return null;

  const [primary, ...rest] = references;

  return (
    <div style={{ display: "grid", gap: 18 }}>
      <SlideReferenceCard reference={primary} large key={`primary-${primary.slideId ?? primary.deckId ?? 0}`} />
      {rest.length > 0 && (
        <div
          style={{
            display: "grid",
            gap: 16,
            gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
          }}
        >
          {rest.map((ref, idx) => (
            <SlideReferenceCard
              reference={ref}
              key={ref.slideId ?? `${ref.deckId ?? "deck"}-${ref.slideNumber ?? idx}`}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function SlideReferenceCard({ reference, large }: { reference: SlideReference; large?: boolean }) {
  const thumb = reference.thumbnailUrl || buildSlideImageUrl(reference.deckId, reference.slideNumber);
  const cardPadding = large ? "18px 20px" : "14px 16px";
  const titleSize = large ? 16 : 15;
  const aspectRatio = large ? "16 / 9" : "16 / 10";

  return (
    <div
      style={{
        display: "grid",
        gap: large ? 12 : 8,
        padding: cardPadding,
        borderRadius: large ? 18 : 16,
        border: "1px solid #dbe2f0",
        background: "#f8fafc",
        boxShadow: large ? "0 16px 38px rgba(15,23,42,0.12)" : "0 10px 25px rgba(15,23,42,0.08)",
      }}
    >
      <div
        style={{
          width: "100%",
          aspectRatio,
          borderRadius: large ? 14 : 12,
          overflow: "hidden",
          background: "linear-gradient(135deg, #e2e8f0, #f1f5f9)",
        }}
      >
        {thumb ? (
          <img
            src={thumb}
            alt={`${reference.deckName ?? "Deck"} slide ${reference.slideNumber ?? ""}`}
            style={{ width: "100%", height: "100%", objectFit: "cover" }}
          />
        ) : (
          <div
            style={{
              display: "grid",
              placeItems: "center",
              height: "100%",
              color: "#94a3b8",
              fontSize: 12,
            }}
          >
            No image
          </div>
        )}
      </div>
      <div style={{ display: "grid", gap: 6 }}>
        <div style={{ fontSize: titleSize, fontWeight: 600, color: "#0f172a" }}>
          {reference.deckName ?? "Deck"}
        </div>
        {reference.slideNumber && (
          <div style={{ fontSize: 13, color: "#475569" }}>Slide {reference.slideNumber}</div>
        )}
        {typeof reference.similarity === "number" && !Number.isNaN(reference.similarity) && (
          <div style={{ fontSize: 12, color: "#64748b" }}>
            {(() => {
              const clamped = Math.max(0, Math.min(1, reference.similarity ?? 0));
              return `Similarity ${(clamped * 100).toFixed(1)}%`;
            })()}
          </div>
        )}
      </div>
    </div>
  );
}

function DeckThumbnailCard({ deck }: { deck: DeckPreview }) {
  const primaryThumb = deck.coverThumbnailUrl ?? deck.thumbnailUrl;
  const pdfDoc = usePdfDocument(primaryThumb ? undefined : deck.pdfUrl);
  const pdfThumb = usePageBitmap(pdfDoc, 1, 320);
  const previewSrc = primaryThumb ?? pdfThumb ?? null;
  const initials = deck.title
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part.slice(0, 1).toUpperCase())
    .join("") || "DX";
  const updated = formatRelativeDate(deck.updatedAt);

  const statusChip = deck.finalized
    ? { label: "Redacted", fg: "#065f46", bg: "#d1fae5" }
    : { label: "Awaiting redaction", fg: "#92400e", bg: "#fef3c7" };

  const slideChip = typeof deck.slideCount === "number" && Number.isFinite(deck.slideCount)
    ? {
        label: `${deck.slideCount} slide${deck.slideCount === 1 ? "" : "s"}`,
        fg: "#1f2937",
        bg: "#e5e7eb",
      }
    : null;

  const chips = slideChip ? [statusChip, slideChip] : [statusChip];

  const externalPdfUrl = deck.pdfUrl;

  return (
    <article
      style={{
        display: "grid",
        gap: 12,
        background: "#ffffff",
        borderRadius: 18,
        border: "1px solid #e2e8f0",
        boxShadow: "0 16px 40px rgba(15,23,42,0.08)",
        padding: 16,
      }}
    >
      <div
        style={{
          position: "relative",
          width: "100%",
          aspectRatio: "16 / 9",
          borderRadius: 12,
          overflow: "hidden",
          background: "linear-gradient(135deg, #e0e7ff, #f3f4f6)",
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
        }}
      >
        {previewSrc ? (
          <img
            src={previewSrc}
            alt={`${deck.title} preview`}
            style={{ width: "100%", height: "100%", objectFit: "cover" }}
          />
        ) : (
          <span style={{ fontSize: 32, fontWeight: 700, color: "#4338ca", letterSpacing: 1 }}>{initials}</span>
        )}
      </div>

      <div style={{ display: "grid", gap: 6 }}>
        <div style={{ fontSize: 16, fontWeight: 600, color: "#0f172a" }}>{deck.title}</div>
        {deck.description && <div style={{ fontSize: 13, color: "#64748b" }}>{deck.description}</div>}
        {chips.length > 0 && (
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            {chips.map((chip) => (
              <span
                key={chip.label}
                style={{
                  fontSize: 11,
                  fontWeight: 600,
                  color: chip.fg,
                  background: chip.bg,
                  padding: "4px 8px",
                  borderRadius: 999,
                  textTransform: "uppercase",
                  letterSpacing: 0.5,
                }}
              >
                {chip.label}
              </span>
            ))}
          </div>
        )}
        {updated && <div style={{ fontSize: 12, color: "#9ca3af" }}>Updated {updated}</div>}
        {externalPdfUrl && deck.finalized && (
          <a
            href={externalPdfUrl}
            target="_blank"
            rel="noopener noreferrer"
            style={{ fontSize: 12, color: "#2563eb", fontWeight: 600, textDecoration: "none" }}
          >
            View redacted PDF
          </a>
        )}
      </div>
    </article>
  );
}

function DeckThumbnailSkeleton() {
  return (
    <article
      style={{
        display: "grid",
        gap: 12,
        background: "#ffffff",
        borderRadius: 18,
        border: "1px solid #e5e7eb",
        padding: 16,
        boxShadow: "0 16px 36px rgba(15,23,42,0.05)",
      }}
    >
      <div
        style={{
          width: "100%",
          aspectRatio: "16 / 9",
          borderRadius: 12,
          background: "linear-gradient(135deg, #e2e8f0, #f1f5f9)",
        }}
      />
      <div style={{ display: "grid", gap: 8 }}>
        <div style={{ height: 14, background: "#e5e7eb", borderRadius: 999 }} />
        <div style={{ height: 12, background: "#eef2f7", borderRadius: 999 }} />
        <div style={{ height: 12, background: "#f1f5f9", borderRadius: 999, width: "60%" }} />
      </div>
    </article>
  );
}

function SearchIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="#111827" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="11" cy="11" r="7" />
      <line x1="16.65" y1="16.65" x2="21" y2="21" />
    </svg>
  );
}
