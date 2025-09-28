// App.tsx
import { useEffect, useRef, useState } from "react";
import { getDocument, GlobalWorkerOptions } from "pdfjs-dist";
import type { PDFDocumentProxy, PDFPageProxy } from "pdfjs-dist/types/src/display/api";

// Worker for pdf.js (Vite-friendly)
import workerSrc from "pdfjs-dist/build/pdf.worker.min.mjs?url";
GlobalWorkerOptions.workerSrc = workerSrc;

/* ----------------------------- Types ----------------------------- */

type BBox = { x?: number; y?: number; w?: number; h?: number };
type Paragraph = { level: number; text: string };

type ElementCommon = {
  key: string;
  id: number;
  name?: string;
  bbox: BBox;
  z: number;
};

type TextboxElement = ElementCommon & {
  type: "textbox";
  paragraphs: Paragraph[];
};

type PictureElement = ElementCommon & {
  type: "picture";
  imgPath?: string;
  bytes?: number;
};

type TableCell = {
  r: number;
  c: number;
  rowSpan: number;
  colSpan: number;
  paragraphs: Paragraph[];
  cellBox?: BBox;
};

type TableElement = ElementCommon & {
  type: "table";
  rows: number;
  cols: number;
  colWidths?: number[];
  rowHeights?: number[];
  cells: (TableCell | null)[][];
};

type Element = TextboxElement | PictureElement | TableElement;

type Slide = { index: number; elements: Element[] };
type Deck = { file: string; slideCount: number; slides: Slide[] };
type ExtractResponse = Deck | { error: string };

type DeckPreview = {
  id: string;
  title: string;
  description?: string;
  thumbnailUrl?: string;
  pdfUrl?: string;
  updatedAt?: string;
};

type RenderResult = {
  pdfUrl: string;
  doc: PDFDocumentProxy;
};

type BuilderSeed = {
  deck: ExtractResponse;
  render: RenderResult;
  instructions: string;
  fileName?: string;
};

const STORAGE_PUBLIC_BASE = (import.meta.env?.VITE_STORAGE_PUBLIC_BASE ?? "").trim();

/* ------------------------------ App ------------------------------ */

type View = "home" | "library" | "builder" | "upload";

export default function App() {
  const [view, setView] = useState<View>("home");
  const [builderSeed, setBuilderSeed] = useState<BuilderSeed | null>(null);

  useEffect(() => {
    const prevBodyOverflow = document.body.style.overflow;
    const prevHtmlOverflow = document.documentElement.style.overflow;
    document.body.style.overflow = "hidden";
    document.documentElement.style.overflow = "hidden";

    return () => {
      document.body.style.overflow = prevBodyOverflow;
      document.documentElement.style.overflow = prevHtmlOverflow;
    };
  }, []);

  return (
    <div
      style={{
        height: "100vh",
        display: "grid",
        gridTemplateRows: "auto 1fr",
        fontFamily: "system-ui",
        background: "#f9fafb",
        overflow: "hidden",
      }}
    >
      <TopNav activeView={view} onSelect={setView} />
      <div style={{ minHeight: 0, height: "100%", overflow: "hidden" }}>
        {view === "home" && <HomeView />}
        {view === "library" && <LibraryView />}
        {view === "upload" && (
          <UploadPrepView
            onCancel={() => setView("home")}
            onComplete={(payload) => {
              setBuilderSeed(payload);
              setView("builder");
            }}
          />
        )}
        {view === "builder" && <RuleBuilder seed={builderSeed} />}
      </div>
    </div>
  );
}

function TopNav({ activeView, onSelect }: { activeView: View; onSelect: (view: View) => void }) {
  return (
    <header
      style={{
        display: "flex",
        alignItems: "center",
        justifyContent: "space-between",
        padding: "10px 20px",
        borderBottom: "1px solid #e5e7eb",
        background: "#ffffffd9",
        backdropFilter: "blur(10px)",
        position: "sticky",
        top: 0,
        zIndex: 10,
      }}
    >
      <nav style={{ display: "flex", alignItems: "center", gap: 12 }}>
        <IconButton
          label="Home"
          active={activeView === "home"}
          onClick={() => onSelect("home")}
        >
          <HomeIcon />
        </IconButton>
        <IconButton
          label="Library"
          active={activeView === "library"}
          onClick={() => onSelect("library")}
        >
          <LibraryIcon />
        </IconButton>
        <IconButton
          label="New workspace"
          active={activeView === "upload"}
          onClick={() => onSelect("upload")}
        >
          <PlusIcon />
        </IconButton>
      </nav>
      <button
        type="button"
        style={{
          background: "transparent",
          border: "1px solid #d1d5db",
          borderRadius: 999,
          padding: "6px 14px",
          fontSize: 14,
          fontWeight: 500,
          color: "#111827",
          cursor: "pointer",
        }}
      >
        Log out
      </button>
    </header>
  );
}

function IconButton({
  label,
  active,
  onClick,
  children,
}: {
  label: string;
  active?: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={label}
      aria-label={label}
      style={{
        display: "inline-flex",
        alignItems: "center",
        justifyContent: "center",
        gap: 8,
        border: "1px solid " + (active ? "#2563eb" : "#d1d5db"),
        background: active ? "#ebf2ff" : "#ffffff",
        color: "#111827",
        borderRadius: 999,
        width: 40,
        height: 40,
        cursor: "pointer",
        boxShadow: active ? "0 2px 6px rgba(37,99,235,0.15)" : "0 1px 2px rgba(15,23,42,0.06)",
        transition: "all 0.15s ease",
      }}
      aria-pressed={active}
    >
      <span
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          lineHeight: 0,
        }}
      >
        {children}
      </span>
    </button>
  );
}

function HomeIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#111827" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M3 11l9-7 9 7" />
      <path d="M9 21V11H15V21" />
    </svg>
  );
}

function LibraryIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#111827" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M4 19V5a2 2 0 012-2h2" />
      <path d="M10 3h4" />
      <path d="M18 3h2a2 2 0 012 2v14" />
      <rect x="6" y="7" width="12" height="14" rx="2" />
    </svg>
  );
}

function PlusIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#111827" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 5v14" />
      <path d="M5 12h14" />
    </svg>
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

function toPublicUrl(path?: string) {
  if (!path || typeof path !== "string") return undefined;
  if (/^https?:\/\//i.test(path) || path.startsWith("data:")) return path;
  if (!STORAGE_PUBLIC_BASE) return undefined;

  let base = STORAGE_PUBLIC_BASE;
  while (base.endsWith("/")) {
    base = base.slice(0, -1);
  }

  let clean = path;
  while (clean.startsWith("/")) {
    clean = clean.slice(1);
  }

  if (!base) return undefined;

  return `${base}/${clean}`;
}

const FALLBACK_DECKS: DeckPreview[] = [
  {
    id: "fallback-onboarding",
    title: "Onboarding overview",
    description: "Sample deck to show the home layout.",
  },
  {
    id: "fallback-security",
    title: "Security briefing",
    description: "Placeholder deck - connect Supabase to replace it.",
  },
  {
    id: "fallback-metrics",
    title: "Q4 metrics recap",
    description: "Upload a deck to see the real preview here.",
  },
];

function formatRelativeDate(iso?: string) {
  if (!iso) return null;
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) return null;
  const now = new Date();
  const opts: Intl.DateTimeFormatOptions = { month: "short", day: "numeric" };
  if (parsed.getFullYear() !== now.getFullYear()) {
    opts.year = "numeric";
  }
  return parsed.toLocaleDateString(undefined, opts);
}

function normalizeDeckRecord(raw: any): DeckPreview | null {
  if (!raw || typeof raw !== "object") return null;
  const id = raw.id ?? raw.deckId ?? raw.deck_id ?? raw.uuid ?? raw.slug;
  const title = raw.deck_name ?? raw.deckName ?? raw.title ?? raw.name ?? raw.original_filename;
  const pdfCandidate = raw.pdfUrl ?? raw.pdf_url ?? raw.pdf ?? raw.pdfPath ?? raw.pdf_path;
  const thumbnailCandidate = raw.thumbnailUrl ?? raw.thumbnail_url ?? raw.preview_url ?? raw.cover_image ?? raw.cover;
  const description = raw.description ?? raw.summary ?? raw.notes ?? undefined;
  const updated = raw.updatedAt ?? raw.updated_at ?? raw.modified_at ?? raw.modifiedAt ?? raw.created_at ?? raw.createdAt;

  const pdfUrl = typeof pdfCandidate === "string"
    ? toPublicUrl(pdfCandidate) ?? (pdfCandidate.startsWith("data:") ? pdfCandidate : undefined)
    : undefined;

  const thumbnailUrl = typeof thumbnailCandidate === "string"
    ? toPublicUrl(thumbnailCandidate) ?? (thumbnailCandidate.startsWith("data:") ? thumbnailCandidate : undefined)
    : undefined;

  const safeTitle = typeof title === "string" && title.trim().length > 0 ? title : "Untitled deck";
  const safeId = id
    ? String(id)
    : typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
      ? crypto.randomUUID()
      : Math.random().toString(36).slice(2);

  return {
    id: safeId,
    title: safeTitle,
    description: typeof description === "string" && description.trim().length > 0 ? description : undefined,
    pdfUrl,
    thumbnailUrl,
    updatedAt: typeof updated === "string" ? updated : undefined,
  };
}

type ChatMsg = { id: string; role: "user" | "assistant"; content: string };

type HomeMode = "landing" | "chat";

function HomeView() {
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

  async function handleSubmit(e: React.FormEvent) {
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
      const reply = typeof data?.reply === "string" ? data.reply : "Sorry, something went wrong.";
      setMessages((prev) => [
        ...prev,
        { id: crypto.randomUUID(), role: "assistant", content: reply },
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
    return <AssistantMessage text={message.content} />;
  }
  return <UserMessage text={message.content} />;
}

function AssistantMessage({ text }: { text: string }) {
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
          color: "#0f172a",
          maxWidth: "min(720px, 80%)",
          lineHeight: 1.65,
          whiteSpace: "pre-wrap",
          fontSize: 16,
        }}
      >
        {text}
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

function DeckThumbnailCard({ deck }: { deck: DeckPreview }) {
  const pdfDoc = usePdfDocument(deck.thumbnailUrl ? undefined : deck.pdfUrl);
  const pdfThumb = usePageBitmap(pdfDoc, 1, 320);
  const previewSrc = deck.thumbnailUrl ?? pdfThumb ?? null;
  const initials = deck.title
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part.slice(0, 1).toUpperCase())
    .join("") || "DX";
  const updated = formatRelativeDate(deck.updatedAt);

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
        {updated && <div style={{ fontSize: 12, color: "#9ca3af" }}>Updated {updated}</div>}
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

function LibraryView() {
  return (
    <div
      style={{
        height: "100%",
        display: "grid",
        placeItems: "center",
        color: "#6b7280",
        fontSize: 16,
      }}
    >
      Library space coming soon.
    </div>
  );
}

function UploadPrepView({
  onComplete,
  onCancel,
}: {
  onComplete: (payload: BuilderSeed) => void;
  onCancel: () => void;
}) {
  const [file, setFile] = useState<File | null>(null);
  const [instructions, setInstructions] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!file) {
      setError("Select a .pptx file first.");
      return;
    }

    setBusy(true);
    setError(null);

    try {
      const { deck, render } = await uploadDeck(file, instructions);
      onComplete({
        deck,
        render,
        instructions: instructions.trim(),
        fileName: file.name,
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
            onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            disabled={busy}
          />
        </label>

        <label style={{ display: "grid", gap: 8, fontSize: 14, color: "#1f2937" }}>
          <span style={{ fontWeight: 600 }}>Redaction instructions</span>
          <textarea
            value={instructions}
            onChange={(e) => setInstructions(e.target.value)}
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

        {error && (
          <div style={{ color: "#b91c1c", fontSize: 13 }}>{error}</div>
        )}

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

function RuleBuilder({ seed }: { seed: BuilderSeed | null }) {
  const [file, setFile] = useState<File | null>(null);
  const [fileLabel, setFileLabel] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<ExtractResponse | null>(seed?.deck ?? null);
  const [render, setRender] = useState<RenderResult | null>(seed?.render ?? null);
  const [selectedPage, setSelectedPage] = useState<number>(1);
  const [instructions, setInstructions] = useState<string>(seed?.instructions ?? "");

  useEffect(() => {
    return () => {
      if (render?.pdfUrl) URL.revokeObjectURL(render.pdfUrl);
    };
  }, [render?.pdfUrl]);

  useEffect(() => {
    if (!seed) return;
    setResult(seed.deck);
    setRender(seed.render);
    setSelectedPage(1);
    setFile(null);
    setFileLabel(seed.fileName ?? (isDeck(seed.deck) ? seed.deck.file : null));
    setInstructions(seed.instructions ?? "");
  }, [seed]);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!file) return;

    setBusy(true);
    setResult(null);
    setRender(null);
    setSelectedPage(1);

    try {
      const { deck, render } = await uploadDeck(file, instructions);
      setResult(deck);
      setRender(render);
      setSelectedPage(1);
      setFileLabel(file.name);
    } catch (err: any) {
      setResult({ error: err?.message ?? "Upload failed" });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div style={{ height: "100%", display: "grid", gridTemplateRows: "auto 1fr", minHeight: 0 }}>
      <header style={{ padding: "16px 20px", borderBottom: "1px solid #e5e7eb", background: "#fff" }}>
        <h1 style={{ margin: 0, fontSize: 20 }}>Rule Builder</h1>
      </header>
      <div style={{ display: "grid", gridTemplateColumns: "280px 1fr 220px", height: "100%", minHeight: 0 }}>
        <aside style={{ padding: 16, borderRight: "1px solid #eee", overflow: "auto", background: "#fff" }}>
          <Uploader busy={busy} file={file} fileLabel={file?.name ?? fileLabel ?? undefined} setFile={setFile} onSubmit={onSubmit} />

          {instructions && (
            <div style={{ marginTop: 20 }}>
              <h3 style={{ fontSize: 14, margin: "0 0 8px" }}>Redaction Notes</h3>
              <div
                style={{
                  whiteSpace: "pre-wrap",
                  background: "#f1f5f9",
                  border: "1px solid #e2e8f0",
                  borderRadius: 10,
                  padding: 12,
                  fontSize: 13,
                  color: "#1f2937",
                }}
              >
                {instructions}
              </div>
            </div>
          )}

          <h2 style={{ fontSize: 18, margin: "20px 0 12px" }}>Active Rules</h2>
          <RuleListPlaceholder />
          <hr style={{ margin: "20px 0" }} />
          <h3 style={{ fontSize: 14, margin: "12px 0" }}>Raw JSON</h3>
          <JSONViewer result={result} />
        </aside>

        <main style={{ display: "grid", gridTemplateRows: "auto 1fr", minHeight: 0, background: "#f9fafb" }}>
          <section style={{ padding: 16, borderBottom: "1px solid #eee" }}>
            <RuleEditorPlaceholder />
          </section>

          <section style={{ minHeight: 0, position: "relative" }}>
            {render?.doc ? (
              <MainSlideViewer doc={render.doc} pageNum={selectedPage} />
            ) : (
              <EmptyState message="Upload a .pptx to see a large preview here." />
            )}
          </section>
        </main>

        <aside style={{ padding: 12, borderLeft: "1px solid #eee", overflow: "auto", background: "#fff" }}>
          <h2 style={{ fontSize: 18, margin: "8px 8px 12px" }}>Slide Deck</h2>
          {render?.doc ? (
            <ThumbRail doc={render.doc} selected={selectedPage} onSelect={setSelectedPage} />
          ) : (
            <EmptyState small message="No slides yet." />
          )}
        </aside>
      </div>
    </div>
  );
}

/* ---------------------------- Components ---------------------------- */

function Uploader({
  busy,
  file,
  fileLabel,
  setFile,
  onSubmit,
}: {
  busy: boolean;
  file: File | null;
  fileLabel?: string;
  setFile: (f: File | null) => void;
  onSubmit: (e: React.FormEvent) => void;
}) {
  return (
    <form onSubmit={onSubmit} style={{ display: "grid", gap: 8 }}>
      <label style={{ fontSize: 12, color: "#6b7280" }}>Upload PowerPoint</label>
      <input
        type="file"
        accept=".pptx,application/vnd.openxmlformats-officedocument.presentationml.presentation"
        onChange={(e) => setFile(e.target.files?.[0] ?? null)}
      />
      <button type="submit" disabled={!file || busy} style={btnStyle}>
        {busy ? "Uploading..." : "Upload & Render"}
      </button>
      {file && <div style={{ fontSize: 12, color: "#6b7280" }}>{file.name}</div>}
      {!file && fileLabel && <div style={{ fontSize: 12, color: "#6b7280" }}>{fileLabel}</div>}
    </form>
  );
}

const btnStyle: React.CSSProperties = {
  appearance: "none",
  background: "#111827",
  color: "#fff",
  border: 0,
  borderRadius: 8,
  padding: "8px 12px",
  fontWeight: 600,
  cursor: "pointer",
};

function RuleListPlaceholder() {
  const items = [
    { title: "Rule 1", subtitle: "Redact dates" },
    { title: "Rule 2", subtitle: "Redact client" },
    { title: "Rule 3", subtitle: "Redact revenue" },
    { title: "Rule 4", subtitle: "Redact client name" },
  ];
  return (
    <div style={{ display: "grid", gap: 10 }}>
      {items.map((it, i) => (
        <div key={i} style={ruleCard}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
            <strong>{it.title}</strong>
            <a style={{ fontSize: 12, color: "#2563eb", textDecoration: "none", cursor: "pointer" }}>Edit</a>
          </div>
          <div style={{ fontSize: 14, color: "#374151", opacity: 0.9 }}>
            Redact <span style={{ fontWeight: 700 }}>{it.subtitle.split(" ")[1] || it.subtitle}</span>
          </div>
        </div>
      ))}
    </div>
  );
}
const ruleCard: React.CSSProperties = {
  background: "#e0ecff",
  borderRadius: 12,
  padding: 12,
  border: "1px solid #c7dbff",
};

function RuleEditorPlaceholder() {
  return (
    <div
      style={{
        background: "#e0ecff",
        border: "1px solid #c7dbff",
        borderRadius: 16,
        padding: 20,
      }}
    >
      <div style={{ fontWeight: 700, marginBottom: 12 }}>Rule Editor</div>
      <div style={{ fontSize: 14, color: "#374151", opacity: 0.9 }}>
        (UI for "if ... then ..." rules goes here)
      </div>
    </div>
  );
}

function JSONViewer({ result }: { result: ExtractResponse | null }) {
  if (!result) return null;
  return (
    <pre
      style={{
        background: "#f6f8fa",
        border: "1px solid #e5e7eb",
        borderRadius: 8,
        padding: 8,
        maxHeight: 260,
        overflow: "auto",
        fontSize: 12,
        lineHeight: 1.4,
      }}
    >
      {JSON.stringify(result, null, 2)}
    </pre>
  );
}

function EmptyState({ message, small }: { message: string; small?: boolean }) {
  return (
    <div
      style={{
        height: "100%",
        display: "grid",
        placeItems: "center",
        color: "#6b7280",
        fontSize: small ? 12 : 14,
      }}
    >
      {message}
    </div>
  );
}

/* ---------------------- PDF rendering helpers ---------------------- */

function isDeck(res: ExtractResponse): res is Deck {
  return (res as Deck)?.slides !== undefined;
}

async function uploadDeck(file: File, instructions?: string): Promise<{ deck: ExtractResponse; render: RenderResult }> {
  const form = new FormData();
  form.append("file", file);
  if (instructions && instructions.trim()) {
    form.append("instructions", instructions.trim());
  }

  const response = await fetch("/api/upload", {
    method: "POST",
    body: form,
    headers: { Accept: "application/json" },
  });

  let payload: any;
  try {
    payload = await response.json();
  } catch {
    throw new Error("Failed to parse upload response");
  }

  if (!response.ok) {
    throw new Error(payload?.error ?? "Upload failed");
  }

  const deck = (payload?.deck ?? payload) as ExtractResponse;
  if ((deck as any)?.error) {
    throw new Error((deck as any).error);
  }

  const pdfInfo = payload?.pdf;
  if (!pdfInfo?.base64) {
    throw new Error("PDF missing from upload response");
  }

  const byteString = atob(pdfInfo.base64);
  const pdfBuffer = new Uint8Array(byteString.length);
  for (let i = 0; i < byteString.length; i++) {
    pdfBuffer[i] = byteString.charCodeAt(i);
  }

  const pdfBlob = new Blob([pdfBuffer], { type: "application/pdf" });
  const pdfUrl = URL.createObjectURL(pdfBlob);

  try {
    const doc = await getDocument({ data: pdfBuffer }).promise;
    return { deck, render: { pdfUrl, doc } };
  } catch (err) {
    URL.revokeObjectURL(pdfUrl);
    throw err;
  }
}

function usePdfDocument(src?: string) {
  const [doc, setDoc] = useState<PDFDocumentProxy | null>(null);

  useEffect(() => {
    let cancelled = false;
    let loadingTask: any = null;

    if (!src) {
      setDoc(null);
      return () => {
        cancelled = true;
        if (loadingTask && typeof loadingTask.destroy === "function") {
          try {
            loadingTask.destroy();
          } catch {
            /* ignore */
          }
        }
      };
    }

    async function load(currentSrc: string) {
      try {
        if (currentSrc.startsWith("data:")) {
          const comma = currentSrc.indexOf(",");
          const base64 = comma >= 0 ? currentSrc.slice(comma + 1) : currentSrc;
          const binary = atob(base64);
          const bytes = new Uint8Array(binary.length);
          for (let i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
          }
          loadingTask = getDocument({ data: bytes });
        } else {
          loadingTask = getDocument({ url: currentSrc });
        }

        const loaded: PDFDocumentProxy = await loadingTask.promise;
        if (!cancelled) {
          setDoc(loaded);
        }
      } catch (error) {
        console.error("Failed to load PDF preview", error);
        if (!cancelled) {
          setDoc(null);
        }
      }
    }

    load(src);

    return () => {
      cancelled = true;
      setDoc(null);
      if (loadingTask && typeof loadingTask.destroy === "function") {
        try {
          loadingTask.destroy();
        } catch {
          /* ignore */
        }
      }
    };
  }, [src]);

  return doc;
}

// High-DPI rasterization for thumbnails
function usePageBitmap(doc: PDFDocumentProxy | null, pageNum: number, cssWidth: number) {
  const [src, setSrc] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    if (!doc || !pageNum || cssWidth <= 0) {
      setSrc(null);
      return;
    }

    (async () => {
      const page = await doc.getPage(pageNum);
      const baseViewport = page.getViewport({ scale: 1 });

      const dpr = window.devicePixelRatio || 1; // crisp on retina
      const scale = cssWidth / baseViewport.width;
      const renderViewport = page.getViewport({ scale: scale * dpr });

      const canvas = document.createElement("canvas");
      canvas.width = Math.max(1, Math.floor(renderViewport.width));
      canvas.height = Math.max(1, Math.floor(renderViewport.height));

      const ctx = canvas.getContext("2d")!;
      ctx.imageSmoothingQuality = "high";

      const renderTask = page.render({ canvasContext: ctx, viewport: renderViewport, canvas });
      await renderTask.promise;

      if (!cancelled) setSrc(canvas.toDataURL("image/png"));
    })();

    return () => {
      cancelled = true;
    };
  }, [doc, pageNum, cssWidth]);

  return src;
}

function ThumbRail({
  doc,
  selected,
  onSelect,
}: {
  doc: PDFDocumentProxy;
  selected: number;
  onSelect: (n: number) => void;
}) {
  const [pageCount, setPageCount] = useState(0);
  useEffect(() => setPageCount(doc.numPages), [doc]);

  const THUMB_W = 200;

  return (
    <div style={{ display: "grid", gap: 10 }}>
      {Array.from({ length: pageCount }, (_, i) => {
        const pageNum = i + 1;
        return (
          <Thumb
            key={pageNum}
            doc={doc}
            pageNum={pageNum}
            width={THUMB_W}
            active={pageNum === selected}
            onClick={() => onSelect(pageNum)}
          />
        );
      })}
    </div>
  );
}

function Thumb({
  doc,
  pageNum,
  width,
  active,
  onClick,
}: {
  doc: PDFDocumentProxy;
  pageNum: number;
  width: number;
  active?: boolean;
  onClick: () => void;
}) {
  const src = usePageBitmap(doc, pageNum, width);
  return (
    <div
      onClick={onClick}
      style={{
        borderRadius: 10,
        border: `2px solid ${active ? "#2563eb" : "#e5e7eb"}`,
        padding: 6,
        cursor: "pointer",
        background: "#fff",
      }}
    >
      {src ? (
        <img src={src} alt={`Slide ${pageNum}`} style={{ width: "100%", display: "block", borderRadius: 6 }} />
      ) : (
        <div style={{ height: 160, display: "grid", placeItems: "center", color: "#9ca3af" }}>Loading...</div>
      )}
      <div style={{ fontSize: 12, color: "#6b7280", marginTop: 6 }}>Slide {pageNum}</div>
    </div>
  );
}

// Big center preview (fits to area; crisp; no resize loop)
function MainSlideViewer({ doc, pageNum }: { doc: PDFDocumentProxy; pageNum: number }) {
  const wrapRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [box, setBox] = useState({ w: 0, h: 0 });

  // Track available width/height
  useEffect(() => {
    const el = wrapRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const r = entries[0].contentRect;
      setBox({ w: r.width, h: r.height });
    });
    ro.observe(el);
    setBox({ w: el.clientWidth, h: el.clientHeight });
    return () => ro.disconnect();
  }, []);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      if (!doc || !pageNum || box.w <= 0 || box.h <= 0) return;

      const page: PDFPageProxy = await doc.getPage(pageNum);
      const vp = page.getViewport({ scale: 1 });

      // Fit-to-box scaling (contain), clamp to sane max CSS width
      const MAX_CSS_W = 1200; // cap so it never dwarfs UI
      const availW = Math.min(box.w - 32, MAX_CSS_W); // 16px padding each side
      const availH = box.h - 32;
      const cssScale = Math.max(0.1, Math.min(availW / vp.width, availH / vp.height));

      // HiDPI rendering
      const dpr = window.devicePixelRatio || 1;
      const renderScale = cssScale * dpr;
      const renderVp = page.getViewport({ scale: renderScale });

      const canvas = canvasRef.current!;
      canvas.width = Math.floor(renderVp.width);
      canvas.height = Math.floor(renderVp.height);
      canvas.style.width = `${Math.floor(renderVp.width / dpr)}px`;
      canvas.style.height = `${Math.floor(renderVp.height / dpr)}px`;

      const ctx = canvas.getContext("2d")!;
      ctx.imageSmoothingQuality = "high";

      const task = page.render({ canvasContext: ctx, viewport: renderVp, canvas });
      await task.promise;
      if (cancelled) return;
    })();

    return () => {
      cancelled = true;
    };
  }, [doc, pageNum, box.w, box.h]);

  return (
    <div
      ref={wrapRef}
      style={{
        height: "100%",
        overflow: "auto",
        background: "#f9fafb",
        padding: 16,
        display: "flex",
        justifyContent: "center",
        alignItems: "flex-start",
      }}
    >
      <canvas
        ref={canvasRef}
        style={{
          boxShadow: "0 2px 14px rgba(0,0,0,.08)",
          borderRadius: 8,
          background: "#fff",
        }}
      />
    </div>
  );
}
