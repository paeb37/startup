// App.tsx
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
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
  coverThumbnailUrl?: string;
  pdfUrl?: string;
  pdfPath?: string;
  updatedAt?: string;
  pptxPath?: string;
  redactedPptxPath?: string;
  redactedPdfPath?: string;
  redactedJsonPath?: string;
  slideCount?: number;
  finalized?: boolean;
};

type SlideReference = {
  slideId?: string;
  deckId?: string;
  deckName?: string;
  slideNumber?: number;
  similarity?: number;
  thumbnailUrl?: string;
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
  deckId?: string;
};

type RuleRecord = {
  id: string;
  deck_id: string;
  title: string;
  user_query: string;
  created_at?: string;
  updated_at?: string;
};

type RuleActionRecord = {
  id: string;
  rule_id: string;
  deck_id: string;
  slide_no: number;
  element_key: string;
  bbox?: BBox | null;
  original_text?: string | null;
  new_text?: string | null;
  created_at?: string;
};

type RedactResponsePayload = {
  success?: boolean;
  actionsApplied?: number;
  path?: string;
  fileName?: string;
  downloadUrl?: string;
  generated?: boolean;
};

function isDeckResponse(value: ExtractResponse | null | undefined): value is Deck {
  if (!value || typeof value !== "object") return false;
  const candidate = value as Deck;
  return Array.isArray(candidate.slides) && typeof candidate.slideCount === "number";
}

const STORAGE_PUBLIC_BASE = (import.meta.env?.VITE_STORAGE_PUBLIC_BASE ?? "").trim();
const SLIDE_IMAGE_BASE = (import.meta.env?.VITE_DECK_API_BASE ?? "").trim();
const SUPABASE_URL = (import.meta.env?.VITE_SUPABASE_URL ?? "").trim();
const SUPABASE_ANON_KEY = (import.meta.env?.VITE_SUPABASE_ANON_KEY ?? "").trim();
const SUPABASE_RULES_TABLE = (import.meta.env?.VITE_SUPABASE_RULES_TABLE ?? "rules").trim();
const SUPABASE_RULE_ACTIONS_TABLE = (import.meta.env?.VITE_SUPABASE_RULE_ACTIONS_TABLE ?? "rule_actions").trim();
const SEMANTIC_URL_RAW = (import.meta.env?.VITE_SEMANTIC_URL ?? "http://localhost:8000").trim();
const SEMANTIC_URL = (SEMANTIC_URL_RAW || "http://localhost:8000").replace(/\/$/, "");
const SUPABASE_CONFIGURED = Boolean(
  SUPABASE_URL &&
  SUPABASE_ANON_KEY &&
  SUPABASE_RULES_TABLE &&
  SUPABASE_RULE_ACTIONS_TABLE,
);

const EMU_PER_POINT = 12700; // 1 point = 1/72", 1 inch = 914400 EMU

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

function resolveAssetUrl(raw?: string) {
  if (!raw || typeof raw !== "string") return undefined;
  const trimmed = raw.trim();
  if (!trimmed) return undefined;

  const publicPath = toPublicUrl(trimmed);
  if (publicPath) return publicPath;
  if (/^(?:https?:|data:|blob:)/i.test(trimmed)) return trimmed;
  if (trimmed.startsWith("/")) return trimmed;
  return `/${trimmed}`;
}

function buildSlideImageUrl(deckId?: string, slideNumber?: number, provided?: string) {
  if (provided && provided.startsWith("data:")) return provided;
  if (provided && /^https?:\/\//i.test(provided)) return provided;
  if (!deckId || !slideNumber) return provided;

  const base = SLIDE_IMAGE_BASE || "";
  const origin = base ? base.replace(/\/$/, "") : "";
  return `${origin}/api/decks/${deckId}/slides/${slideNumber}`;
}

const FALLBACK_DECKS: DeckPreview[] = [
  {
    id: "fallback-onboarding",
    title: "Onboarding overview",
    description: "Sample deck to show the home layout.",
    thumbnailUrl: "https://placehold.co/640x360/4c51bf/ffffff?text=Dexter",
    coverThumbnailUrl: "https://placehold.co/640x360/4c51bf/ffffff?text=Dexter",
  },
  {
    id: "fallback-security",
    title: "Security briefing",
    description: "Placeholder deck - connect Supabase to replace it.",
    thumbnailUrl: "https://placehold.co/640x360/2563eb/ffffff?text=Deck",
    coverThumbnailUrl: "https://placehold.co/640x360/2563eb/ffffff?text=Deck",
  },
  {
    id: "fallback-metrics",
    title: "Q4 metrics recap",
    description: "Upload a deck to see the real preview here.",
    thumbnailUrl: "https://placehold.co/640x360/1e293b/ffffff?text=Slide",
    coverThumbnailUrl: "https://placehold.co/640x360/1e293b/ffffff?text=Slide",
  },
];

type LibraryDeck = DeckPreview & {
  industry: string;
  category: string;
  summary: string;
};

const LIBRARY_INDUSTRIES = [
  "Technology",
  "Healthcare",
  "Finance",
  "Education",
  "Retail",
  "Manufacturing",
];

const LIBRARY_TYPES = [
  "Overview",
  "Pitch",
  "Report",
  "Training",
  "Strategy",
];

function enrichDeckForLibrary(deck: DeckPreview, index: number): LibraryDeck {
  const industry = LIBRARY_INDUSTRIES[index % LIBRARY_INDUSTRIES.length];
  const category = LIBRARY_TYPES[index % LIBRARY_TYPES.length];
  const summary = deck.description?.trim() || "No summary available yet.";

  return {
    ...deck,
    industry,
    category,
    summary,
  };
}

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

  const rawId = raw.id ?? raw.deckId ?? raw.deck_id ?? raw.uuid ?? raw.slug;
  const deckId = rawId != null ? String(rawId) : undefined;

  const pptxCandidate = raw.pptx_path ?? raw.pptxPath ?? raw.original_path;
  const redactedPptxCandidate = raw.redacted_pptx_path ?? raw.redactedPptxPath;
  const redactedPdfCandidate = raw.redacted_pdf_url ?? raw.redacted_pdf_path ?? raw.redactedPdfUrl ?? raw.redactedPdfPath;
  const redactedJsonCandidate = raw.redacted_json_path ?? raw.redactedJsonPath;

  const pdfCandidate = redactedPdfCandidate
    ?? raw.pdfUrl
    ?? raw.pdf_url
    ?? raw.pdf
    ?? raw.pdfPath
    ?? raw.pdf_path;

  const thumbnailCandidate =
    raw.cover_thumbnail_url
    ?? raw.coverThumbnailUrl
    ?? raw.thumbnailUrl
    ?? raw.thumbnail_url
    ?? raw.preview_url
    ?? raw.cover_image
    ?? raw.cover;

  const description = raw.description ?? raw.summary ?? raw.notes ?? undefined;
  const updated = raw.updatedAt ?? raw.updated_at ?? raw.modified_at ?? raw.modifiedAt ?? raw.created_at ?? raw.createdAt;

  let slideCount: number | undefined;
  const slideCandidates = [raw.slide_count, raw.slideCount];
  for (const candidate of slideCandidates) {
    if (typeof candidate === "number" && Number.isFinite(candidate)) {
      slideCount = candidate;
      break;
    }
    if (typeof candidate === "string") {
      const parsed = Number.parseInt(candidate, 10);
      if (!Number.isNaN(parsed)) {
        slideCount = parsed;
        break;
      }
    }
  }

  const rawThumb = typeof thumbnailCandidate === "string" && thumbnailCandidate.trim().length > 0
    ? thumbnailCandidate
    : undefined;
  const providedThumb = resolveAssetUrl(rawThumb);

  const finalized = [redactedPptxCandidate, redactedPdfCandidate, redactedJsonCandidate].some(
    (value) => typeof value === "string" && value.trim().length > 0,
  );

  const generatedThumb = finalized ? buildSlideImageUrl(deckId, 1, rawThumb) : undefined;
  const thumbnailUrl = finalized
    ? resolveAssetUrl(generatedThumb)
    : providedThumb;

  const pdfUrl = typeof pdfCandidate === "string"
    ? toPublicUrl(pdfCandidate) ?? (pdfCandidate.startsWith("data:") ? pdfCandidate : undefined)
    : undefined;

  const titleCandidate = raw.deck_name
    ?? raw.deckName
    ?? raw.title
    ?? raw.name
    ?? (typeof pptxCandidate === "string" ? inferDeckNameFromPath(pptxCandidate) : undefined);

  const safeTitle = typeof titleCandidate === "string" && titleCandidate.trim().length > 0
    ? titleCandidate.trim()
    : deckId ?? "Untitled deck";

  const safeId = deckId
    ?? (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
      ? crypto.randomUUID()
      : Math.random().toString(36).slice(2));

  return {
    id: safeId,
    title: safeTitle,
    description: typeof description === "string" && description.trim().length > 0 ? description : undefined,
    pdfUrl,
    pdfPath: typeof pdfCandidate === "string" ? pdfCandidate : undefined,
    thumbnailUrl,
    coverThumbnailUrl: providedThumb ?? thumbnailUrl,
    updatedAt: typeof updated === "string" ? updated : undefined,
    pptxPath: typeof pptxCandidate === "string" ? pptxCandidate : undefined,
    redactedPptxPath: typeof redactedPptxCandidate === "string" ? redactedPptxCandidate : undefined,
    redactedPdfPath: typeof redactedPdfCandidate === "string" ? redactedPdfCandidate : undefined,
    redactedJsonPath: typeof redactedJsonCandidate === "string" ? redactedJsonCandidate : undefined,
    slideCount,
    finalized,
  };
}

function inferDeckNameFromPath(path: string): string | undefined {
  if (!path) return undefined;
  const segments = path.split("/").filter(Boolean);
  if (segments.length === 0) return undefined;
  const last = segments[segments.length - 1];
  const dot = last.lastIndexOf(".");
  const stem = dot >= 0 ? last.slice(0, dot) : last;
  return stem.trim() || undefined;
}

function normalizeSlideReference(raw: any): SlideReference | null {
  if (!raw || typeof raw !== "object") return null;

  const slideId = typeof raw.slideId === "string" ? raw.slideId : typeof raw.id === "string" ? raw.id : undefined;
  const deckIdRaw = raw.deckId ?? raw.deck_id ?? raw.deckID ?? raw.deck;
  const deckId = typeof deckIdRaw === "string" ? deckIdRaw : undefined;
  const deckName = typeof raw.deckName === "string" ? raw.deckName : undefined;

  let slideNumber: number | undefined;
  const slideRaw = raw.slideNumber ?? raw.slide_no ?? raw.slide ?? raw.number;
  if (typeof slideRaw === "number" && Number.isFinite(slideRaw)) {
    slideNumber = slideRaw;
  } else if (typeof slideRaw === "string") {
    const parsed = parseInt(slideRaw, 10);
    if (!Number.isNaN(parsed)) slideNumber = parsed;
  }

  const similarity = typeof raw.similarity === "number" ? raw.similarity : undefined;
  const providedThumb = typeof raw.thumbnailUrl === "string"
    ? raw.thumbnailUrl
    : typeof raw.thumbnail_url === "string"
      ? raw.thumbnail_url
      : undefined;

  const thumbnailUrl = buildSlideImageUrl(deckId, slideNumber, providedThumb ?? undefined);
  console.log("normalizeSlideReference", {
    slideId,
    deckId,
    deckName,
    slideNumber,
    similarity,
    providedThumb,
    thumbnailUrl,
  });

  return {
    slideId,
    deckId,
    deckName,
    slideNumber,
    similarity,
    thumbnailUrl,
  };
}

function normalizeSlideReferences(raw: any): SlideReference[] | undefined {
  if (!Array.isArray(raw)) return undefined;
  const list: SlideReference[] = [];
  for (const item of raw) {
    const normalized = normalizeSlideReference(item);
    if (normalized) list.push(normalized);
  }
  return list.length > 0 ? list : undefined;
}

type ChatMsg = {
  id: string;
  role: "user" | "assistant";
  content: string;
  sources?: SlideReference[];
};

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

function useLibraryDecks(limit = 60) {
  const [decks, setDecks] = useState<LibraryDeck[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      setLoading(true);
      setError(null);
      try {
        const res = await fetch(`/api/decks?limit=${limit}`);
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
        const enriched = normalized.map(enrichDeckForLibrary);
        if (!cancelled) {
          setDecks(enriched);
        }
      } catch (err) {
        console.error("Failed to load library decks", err);
        if (!cancelled) {
          const enrichedFallback = FALLBACK_DECKS.map(enrichDeckForLibrary);
          setDecks(enrichedFallback);
          setError(err instanceof Error ? err.message : "Failed to load decks");
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    load();
    return () => {
      cancelled = true;
    };
  }, [limit, revision]);

  const reload = useCallback(() => {
    setRevision((value) => value + 1);
  }, []);

  return { decks, loading, error, reload };
}

function LibraryView() {
  const { decks, loading, error, reload } = useLibraryDecks();
  const [searchTerm, setSearchTerm] = useState("");
  const [selectedIndustries, setSelectedIndustries] = useState<string[]>([]);
  const [selectedTypes, setSelectedTypes] = useState<string[]>([]);

  const filteredDecks = useMemo(() => {
    const term = searchTerm.trim().toLowerCase();
    return decks.filter((deck) => {
      const industryMatch = selectedIndustries.length === 0 || selectedIndustries.includes(deck.industry);
      const typeMatch = selectedTypes.length === 0 || selectedTypes.includes(deck.category);
      const termMatch =
        term.length === 0 ||
        deck.title.toLowerCase().includes(term) ||
        deck.summary.toLowerCase().includes(term);
      return industryMatch && typeMatch && termMatch;
    });
  }, [decks, selectedIndustries, selectedTypes, searchTerm]);

  const toggleIndustry = useCallback((value: string) => {
    setSelectedIndustries((prev) =>
      prev.includes(value) ? prev.filter((item) => item !== value) : [...prev, value],
    );
  }, []);

  const toggleType = useCallback((value: string) => {
    setSelectedTypes((prev) =>
      prev.includes(value) ? prev.filter((item) => item !== value) : [...prev, value],
    );
  }, []);

  return (
    <div
      style={{
        height: "100%",
        display: "grid",
        gridTemplateColumns: "260px 1fr",
        minHeight: 0,
      }}
    >
      <aside
        style={{
          borderRight: "1px solid #e5e7eb",
          background: "#ffffff",
          display: "grid",
          gridTemplateRows: "auto 1fr auto",
          padding: "24px 20px",
          gap: 24,
          minHeight: 0,
        }}
      >
        <div style={{ display: "grid", gap: 6 }}>
          <h2 style={{ margin: 0, fontSize: 18, color: "#0f172a" }}>Filters</h2>
          <div style={{ fontSize: 13, color: "#6b7280" }}>Narrow down decks by industry, type, or keywords.</div>
        </div>

        <div style={{ display: "grid", gap: 24, overflowY: "auto" }}>
          <FilterSection
            title="Industry"
            options={LIBRARY_INDUSTRIES}
            selected={selectedIndustries}
            onToggle={toggleIndustry}
          />
          <FilterSection
            title="Deck type"
            options={LIBRARY_TYPES}
            selected={selectedTypes}
            onToggle={toggleType}
          />
        </div>

        <button
          type="button"
          onClick={reload}
          style={{
            border: "1px solid #111827",
            background: "#111827",
            color: "#fff",
            borderRadius: 999,
            padding: "10px 18px",
            fontSize: 14,
            fontWeight: 600,
            cursor: "pointer",
          }}
        >
          Apply
        </button>
      </aside>

      <main
        style={{
          display: "grid",
          gridTemplateRows: "auto 1fr",
          minHeight: 0,
          padding: "24px 28px",
          gap: 24,
          background: "#f9fafb",
        }}
      >
        <div
          style={{
            display: "flex",
            flexWrap: "wrap",
            gap: 16,
            alignItems: "center",
            justifyContent: "space-between",
          }}
        >
          <div style={{ display: "grid", gap: 4 }}>
            <h1 style={{ margin: 0, fontSize: 24, color: "#0f172a" }}>Deck library</h1>
            <div style={{ fontSize: 13, color: "#6b7280" }}>
              {loading ? "Loading decks…" : `${filteredDecks.length} deck${filteredDecks.length === 1 ? "" : "s"} found.`}
            </div>
          </div>

          <div style={{ display: "flex", gap: 12, flexWrap: "wrap" }}>
            <input
              type="search"
              value={searchTerm}
              onChange={(event) => setSearchTerm(event.target.value)}
              placeholder="Search decks"
              style={{
                borderRadius: 999,
                border: "1px solid #d1d5db",
                padding: "10px 16px",
                fontSize: 14,
                minWidth: 220,
                background: "#ffffff",
              }}
            />
            <button
              type="button"
              onClick={reload}
              style={{
                border: "1px solid #d1d5db",
                background: "#ffffff",
                color: "#1f2937",
                borderRadius: 999,
                padding: "10px 16px",
                fontSize: 13,
                fontWeight: 600,
                cursor: "pointer",
              }}
            >
              Refresh
            </button>
          </div>
        </div>

        <div style={{ overflow: "hidden" }}>
          <div style={{ height: "100%", overflowY: "auto" }}>
            {error && (
              <div style={{ marginBottom: 16, color: "#b91c1c", fontSize: 13 }}>{error}</div>
            )}

            {loading ? (
              <div
                style={{
                  display: "grid",
                  gap: 32,
                  gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
                  alignContent: "start",
                  padding: "0 16px 32px",
                  margin: "0 auto",
                  width: "100%",
                  maxWidth: 1080,
                  boxSizing: "border-box",
                }}
              >
                {Array.from({ length: 6 }).map((_, index) => (
                  <LibraryDeckSkeleton key={index} />
                ))}
              </div>
            ) : filteredDecks.length === 0 ? (
              <EmptyState message="No decks match the current filters." />
            ) : (
              <div
                style={{
                  display: "grid",
                  gap: 32,
                  gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
                  alignContent: "start",
                  padding: "0 16px 32px",
                  margin: "0 auto",
                  width: "100%",
                  maxWidth: 1080,
                  boxSizing: "border-box",
                }}
              >
                {filteredDecks.map((deck) => (
                  <LibraryDeckCard key={deck.id} deck={deck} />
                ))}
              </div>
            )}
          </div>
        </div>
      </main>
    </div>
  );
}

function FilterSection({
  title,
  options,
  selected,
  onToggle,
}: {
  title: string;
  options: string[];
  selected: string[];
  onToggle: (value: string) => void;
}) {
  return (
    <section style={{ display: "grid", gap: 10 }}>
      <h3 style={{ margin: 0, fontSize: 15, color: "#111827" }}>{title}</h3>
      <div style={{ display: "grid", gap: 8 }}>
        {options.map((option) => {
          const id = `${title}-${option}`.toLowerCase().replace(/[^a-z0-9]+/g, "-");
          const active = selected.includes(option);
          return (
            <label
              key={option}
              htmlFor={id}
              style={{
                display: "flex",
                alignItems: "center",
                gap: 8,
                fontSize: 13,
                color: active ? "#1d4ed8" : "#334155",
                cursor: "pointer",
              }}
            >
              <input
                id={id}
                type="checkbox"
                checked={active}
                onChange={() => onToggle(option)}
                style={{ width: 16, height: 16, accentColor: "#1d4ed8" }}
              />
              {option}
            </label>
          );
        })}
      </div>
    </section>
  );
}

function LibraryDeckCard({ deck }: { deck: LibraryDeck }) {
  const previewSrc =
    resolveAssetUrl(deck.coverThumbnailUrl)
    ?? resolveAssetUrl(deck.thumbnailUrl)
    ?? resolveAssetUrl(buildSlideImageUrl(deck.id, 1));
  const initials = deck.title
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part.slice(0, 1).toUpperCase())
    .join("") || "DX";
  const updated = formatRelativeDate(deck.updatedAt);
  const summary = deck.summary.length > 160 ? `${deck.summary.slice(0, 157)}…` : deck.summary;

  return (
    <article
      style={{
        display: "grid",
        gap: 12,
        background: "#ffffff",
        borderRadius: 16,
        border: "1px solid #e2e8f0",
        boxShadow: "0 12px 30px rgba(15,23,42,0.08)",
        padding: 16,
        width: "90%",
        maxWidth: 320,
        margin: "0 auto 8px",
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
            style={{ width: "100%", height: "100%", objectFit: "contain", display: "block" }}
          />
        ) : (
          <span style={{ fontSize: 28, fontWeight: 700, color: "#4338ca", letterSpacing: 0.8 }}>{initials}</span>
        )}
      </div>

      <div style={{ display: "grid", gap: 8 }}>
        <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 8 }}>
          <h3 style={{ margin: 0, fontSize: 17, color: "#0f172a" }}>{deck.title}</h3>
          {updated && <span style={{ fontSize: 11, color: "#94a3b8" }}>Updated {updated}</span>}
        </div>

        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          <span
            style={{
              fontSize: 11,
              fontWeight: 600,
              color: "#1d4ed8",
              background: "#e0ecff",
              padding: "4px 8px",
              borderRadius: 999,
              textTransform: "uppercase",
              letterSpacing: 0.5,
            }}
          >
            {deck.industry}
          </span>
          <span
            style={{
              fontSize: 11,
              fontWeight: 600,
              color: "#0f172a",
              background: "#e5e7eb",
              padding: "4px 8px",
              borderRadius: 999,
              textTransform: "uppercase",
              letterSpacing: 0.5,
            }}
          >
            {deck.category}
          </span>
          {typeof deck.slideCount === "number" && Number.isFinite(deck.slideCount) && (
            <span
              style={{
                fontSize: 11,
                fontWeight: 600,
                color: "#1f2937",
                background: "#f3f4f6",
                padding: "4px 8px",
                borderRadius: 999,
                letterSpacing: 0.5,
              }}
            >
              {deck.slideCount} slides
            </span>
          )}
        </div>

        <p style={{ margin: 0, fontSize: 13, color: "#475569", lineHeight: 1.6 }}>{summary}</p>
      </div>
    </article>
  );
}

function LibraryDeckSkeleton() {
  return (
    <article
      style={{
        display: "grid",
        gap: 12,
        background: "#ffffff",
        borderRadius: 16,
        border: "1px solid #e2e8f0",
        padding: 16,
        boxShadow: "0 12px 30px rgba(15,23,42,0.05)",
        width: "90%",
        maxWidth: 320,
        margin: "0 auto 8px",
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
        <div style={{ height: 14, background: "#e2e8f0", borderRadius: 999 }} />
        <div style={{ height: 12, background: "#f1f5f9", borderRadius: 999, width: "60%" }} />
        <div style={{ height: 44, background: "#f1f5f9", borderRadius: 12 }} />
      </div>
    </article>
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
      const { deck, render, deckId } = await uploadDeck(file, instructions);
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
  const [render, setRender] = useState<RenderResult | null>(seed?.render ?? null);
  const [selectedPage, setSelectedPage] = useState<number>(1);
  const [previewEnabled, setPreviewEnabled] = useState(false);

  const deckId = seed?.deckId ?? null;
  const deckPayload = seed && isDeckResponse(seed.deck) ? seed.deck : null;

  const rulesState = useSupabaseRules(deckId);
  const {
    actions: ruleActions,
    loading: actionsLoading,
    error: actionsError,
    reload: reloadRuleActions,
  } = useSupabaseRuleActions(deckId, previewEnabled);

  const totalActions = ruleActions.length;

  const slideActions = useMemo(() => {
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

  async function handleRedactDeck() {
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
    } catch (err: any) {
      setRedactError(err?.message ?? "Failed to create redacted deck");
      setRedactResult(null);
    } finally {
      setRedactBusy(false);
    }
  }
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
    setRedactResult(null);
    setRedactError(null);
    setRedactBusy(false);
  }, [seed]);

  const activeDoc = render?.doc ?? null;
  const viewerKey = "original";

  useEffect(() => {
    if (!activeDoc) return;
    if (selectedPage > activeDoc.numPages) {
      setSelectedPage(activeDoc.numPages);
    }
  }, [activeDoc, selectedPage]);

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

          <div style={{gap: 8, overflowY: "auto", minHeight: 0 }}>
            <ActiveRulesPanel deckId={deckId} rulesState={rulesState} />
          </div>

          <div style={{ display: "grid", gap: 8 }}>
            <UploadDeckButton
              disabled={!deckId || redactBusy}
              busy={redactBusy}
              onClick={handleRedactDeck}
            />
            {redactError && <div style={{ fontSize: 12, color: "#b91c1c" }}>{redactError}</div>}
            {redactResult && (
              <div style={{ fontSize: 12, color: "#047857", display: "grid", gap: 4 }}>
                <span>
                  Saved redacted deck{typeof redactResult.actionsApplied === "number" ? ` • ${redactResult.actionsApplied} change${redactResult.actionsApplied === 1 ? "" : "s"} applied` : ""}.
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
                  busy={previewEnabled && actionsLoading}
                  disabled={!previewAvailable}
                  total={totalActions}
                  current={currentSlideCount}
                />
                {previewEnabled && actionsLoading && (
                  <span style={{ fontSize: 12, color: "#64748b" }}>Loading overlays…</span>
                )}
                {noActionsForSlide && (
                  <span style={{ fontSize: 12, color: "#64748b" }}>No redactions on this slide.</span>
                )}
                {previewEnabled && actionsError && (
                  <span style={{ fontSize: 12, color: "#b91c1c" }}>{actionsError}</span>
                )}
                {previewDisabledMessage && (
                  <span style={{ fontSize: 12, color: "#b91c1c" }}>{previewDisabledMessage}</span>
                )}
              </div>
            </div>
          </section>

          <section style={{ minHeight: 0, position: "relative" }}>
            {activeDoc ? (
              <MainSlideViewer
                key={`${viewerKey}-${previewEnabled ? "preview" : "plain"}`}
                doc={activeDoc}
                pageNum={selectedPage}
                highlights={slideActions}
                previewEnabled={previewEnabled}
                loadingHighlights={actionsLoading}
              />
            ) : (
              <EmptyState message="Upload a .pptx to see a large preview here." />
            )}
          </section>
        </main>

        <aside style={{ padding: 12, borderLeft: "1px solid #eee", overflow: "auto", background: "#fff" }}>
          <h2 style={{ fontSize: 18, margin: "8px 8px 12px" }}>Slide Deck</h2>
          {activeDoc ? (
            <ThumbRail key={viewerKey} doc={activeDoc} selected={selectedPage} onSelect={setSelectedPage} />
          ) : (
            <EmptyState small message="No slides yet." />
          )}
        </aside>
      </div>
    </div>
  );
}

/* ---------------------------- Components ---------------------------- */

type AddRulePanelProps = {
  deckId: string | null;
  deck: Deck | null;
  initialInstructions?: string;
  supabaseConfigured: boolean;
  reloadRules?: () => void;
  reloadRuleActions?: () => void;
  onRuleCreated?: () => void;
};

function AddRulePanel({
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
        const message = typeof responseJson?.detail === "string"
          ? responseJson.detail
          : typeof responseJson?.error === "string"
            ? responseJson.error
            : `Rule creation failed (${response.status})`;
        throw new Error(message);
      }

      reloadRules?.();
      reloadRuleActions?.();
      onRuleCreated?.();

      setShowOverlay(false);
      setInstructions(trimmed);
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

type RulesHookState = {
  rules: RuleRecord[];
  loading: boolean;
  error: string | null;
  reload: () => void;
  configured: boolean;
};

function ActiveRulesPanel({ deckId, rulesState }: { deckId: string | null; rulesState: RulesHookState }) {
  const { rules, loading, error, reload, configured } = rulesState;

  const header = (
    <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 8 }}>
      <h2 style={{ fontSize: 18, margin: 0 }}>Active Rules</h2>
      <button
        type="button"
        onClick={reload}
        disabled={!deckId || !configured || loading}
        style={{
          border: "none",
          background: "transparent",
          color: !deckId || !configured ? "#cbd5f5" : loading ? "#94a3b8" : "#2563eb",
          fontSize: 12,
          cursor: !deckId || !configured || loading ? "default" : "pointer",
          fontWeight: 600,
        }}
      >
        Refresh
      </button>
    </div>
  );

  if (!deckId) {
    return (
      <div style={{ display: "grid", gap: 8 }}>
        {header}
        <div style={{ fontSize: 13, color: "#6b7280" }}>Upload a deck to see saved rules.</div>
      </div>
    );
  }

  if (!configured) {
    return (
      <div style={{ display: "grid", gap: 8 }}>
        {header}
        <div style={{ fontSize: 13, color: "#b91c1c", lineHeight: 1.4 }}>
          Set `VITE_SUPABASE_URL` and `VITE_SUPABASE_ANON_KEY` to load rules.
        </div>
      </div>
    );
  }

  return (
    <div style={{ display: "grid", gap: 8 }}>
      {header}

      {error && <div style={{ fontSize: 13, color: "#b91c1c" }}>{error}</div>}

      {loading && rules.length === 0 && <RuleListSkeleton count={2} />}

      {!loading && !error && rules.length === 0 && (
        <div style={{ fontSize: 13, color: "#6b7280" }}>No rules yet. Generate instructions to add one.</div>
      )}

      {rules.length > 0 && (
        <div style={{ fontSize: 12, color: "#6b7280", marginTop: 2 }}>
          {`${rules.length} active rule${rules.length === 1 ? "" : "s"}`}
        </div>
      )}
      
      {rules.map((rule) => {
        const created = formatTimestamp(rule.created_at);
        return (
          <div key={rule.id} style={ruleCard}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", gap: 8 }}>
              <strong style={{ fontSize: 14 }}>{rule.title || "Untitled rule"}</strong>
              {created && <span style={{ fontSize: 11, color: "#94a3b8" }}>{created}</span>}
            </div>
            <div style={{ fontSize: 13, color: "#475569", marginTop: 4, whiteSpace: "pre-wrap" }}>{rule.user_query}</div>
          </div>
        );
      })}
    </div>
  );
}

function RuleListSkeleton({ count }: { count: number }) {
  return (
    <div style={{ display: "grid", gap: 10 }}>
      {Array.from({ length: count }).map((_, index) => (
        <div key={index} style={{ ...ruleCard, background: "#f8fafc", borderColor: "#e2e8f0" }}>
          <div style={{ height: 12, background: "#e2e8f0", borderRadius: 999, marginBottom: 8 }} />
          <div style={{ height: 10, background: "#e2e8f0", borderRadius: 999 }} />
        </div>
      ))}
    </div>
  );
}

function useSupabaseRules(deckId: string | null) {
  const [rules, setRules] = useState<RuleRecord[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    if (!deckId) {
      setRules([]);
      setLoading(false);
      setError(null);
      return;
    }

    if (!SUPABASE_CONFIGURED) {
      setRules([]);
      setLoading(false);
      setError("Supabase configuration missing.");
      return;
    }

    const controller = new AbortController();
    setLoading(true);
    setError(null);

    (async () => {
      try {
        const params = new URLSearchParams({
          select: "id,deck_id,title,user_query,created_at,updated_at",
          order: "created_at.desc.nullslast",
          deck_id: `eq.${deckId}`,
        });

        const response = await fetch(`${SUPABASE_URL}/rest/v1/${SUPABASE_RULES_TABLE}?${params.toString()}`, {
          method: "GET",
          headers: {
            apikey: SUPABASE_ANON_KEY,
            Authorization: `Bearer ${SUPABASE_ANON_KEY}`,
            Accept: "application/json",
          },
          signal: controller.signal,
        });

        if (!response.ok) {
          const body = await response.text();
          throw new Error(body || `Failed to load rules (${response.status})`);
        }

        const rows = (await response.json()) as RuleRecord[];
        if (!controller.signal.aborted) {
          setRules(Array.isArray(rows) ? rows : []);
        }
      } catch (err) {
        if (controller.signal.aborted) {
          return;
        }
        setRules([]);
        setError(err instanceof Error ? err.message : "Failed to load rules");
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    })();

    return () => controller.abort();
  }, [deckId, revision]);

  const reload = useCallback(() => {
    setRevision((value) => value + 1);
  }, []);

  return { rules, loading, error, reload, configured: SUPABASE_CONFIGURED };
}

function useSupabaseRuleActions(deckId: string | null, enabled: boolean) {
  const [actions, setActions] = useState<RuleActionRecord[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    if (!enabled) {
      setLoading(false);
      setError(null);
      return;
    }

    if (!deckId) {
      setActions([]);
      setLoading(false);
      setError(null);
      return;
    }

    if (!SUPABASE_CONFIGURED) {
      setActions([]);
      setLoading(false);
      setError("Supabase configuration missing.");
      return;
    }

    const controller = new AbortController();
    setLoading(true);
    setError(null);

    (async () => {
      try {
        const params = new URLSearchParams({
          select: "id,rule_id,deck_id,slide_no,element_key,bbox,original_text,new_text,created_at",
          order: "created_at.desc.nullslast",
          deck_id: `eq.${deckId}`,
        });

        const response = await fetch(
          `${SUPABASE_URL}/rest/v1/${SUPABASE_RULE_ACTIONS_TABLE}?${params.toString()}`,
          {
            method: "GET",
            headers: {
              apikey: SUPABASE_ANON_KEY,
              Authorization: `Bearer ${SUPABASE_ANON_KEY}`,
              Accept: "application/json",
            },
            signal: controller.signal,
          },
        );

        if (!response.ok) {
          const body = await response.text();
          throw new Error(body || `Failed to load rule actions (${response.status})`);
        }

        const rows = (await response.json()) as RuleActionRecord[];
        if (!controller.signal.aborted) {
          const normalized = Array.isArray(rows)
            ? rows
                .map((row) => ({
                  ...row,
                  slide_no: Number((row as any).slide_no),
                  bbox: row.bbox ?? null,
                  original_text: row.original_text ?? null,
                  new_text: row.new_text ?? null,
                }))
                .filter((row) => Number.isFinite(row.slide_no))
            : [];
          setActions(normalized);
        }
      } catch (err) {
        if (controller.signal.aborted) {
          return;
        }
        setError(err instanceof Error ? err.message : "Failed to load rule actions");
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    })();

    return () => controller.abort();
  }, [deckId, enabled, revision]);

  const reload = useCallback(() => {
    setRevision((value) => value + 1);
  }, []);

  return { actions, loading, error, reload };
}

function formatTimestamp(iso?: string) {
  if (!iso) {
    return null;
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return null;
  }
  return date.toLocaleString();
}

function UploadDeckButton({
  onClick,
  busy,
  disabled,
}: {
  onClick: () => void;
  busy?: boolean;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      style={{
        appearance: "none",
        border: "1px solid #111827",
        borderRadius: 10,
        padding: "10px 18px",
        background: "#111827",
        color: "#fff",
        fontSize: 14,
        fontWeight: 600,
        cursor: disabled ? "not-allowed" : "pointer",
        transition: "transform 0.15s ease",
        opacity: disabled ? 0.6 : 1,
      }}
      onClick={() => {
        if (disabled) return;
        onClick();
      }}
      disabled={disabled}
    >
      {busy ? "Saving…" : "Upload redacted deck"}
    </button>
  );
}

const ruleCard: React.CSSProperties = {
  background: "#eef4ff",
  borderRadius: 10,
  padding: 10,
  border: "1px solid #d7e4ff",
};

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

async function uploadDeck(
  file: File,
  instructions?: string,
): Promise<{ deck: ExtractResponse; render: RenderResult; deckId?: string }> {
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

  const payload = await response.json();

  if (!response.ok) {
    throw new Error(payload?.error ?? "Upload failed");
  }

  const deck = (payload?.deck ?? payload) as ExtractResponse;
  const deckId = typeof payload?.deckId === "string" ? payload.deckId : undefined;
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
    return {
      deck,
      render: {
        pdfUrl,
        doc,
      },
      deckId,
    };
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

// Big center preview (fits to area; crisp; optional redaction overlays)
function MainSlideViewer({
  doc,
  pageNum,
  highlights = [],
  previewEnabled = false,
  loadingHighlights = false,
}: {
  doc: PDFDocumentProxy;
  pageNum: number;
  highlights?: RuleActionRecord[];
  previewEnabled?: boolean;
  loadingHighlights?: boolean;
}) {
  const wrapRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [box, setBox] = useState({ w: 0, h: 0 });
  const [renderInfo, setRenderInfo] = useState({ cssScale: 1, width: 0, height: 0 });

  // Track available width/height for the viewer
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
      const MAX_CSS_W = 1200;
      const availW = Math.min(box.w - 32, MAX_CSS_W);
      const availH = box.h - 32;
      const cssScale = Math.max(0.1, Math.min(availW / vp.width, availH / vp.height));

      // HiDPI rendering
      const dpr = window.devicePixelRatio || 1;
      const renderScale = cssScale * dpr;
      const renderVp = page.getViewport({ scale: renderScale });

      const canvas = canvasRef.current;
      if (!canvas) return;
      const ctx = canvas.getContext("2d");
      if (!ctx) return;

      const cssWidth = Math.floor(renderVp.width / dpr);
      const cssHeight = Math.floor(renderVp.height / dpr);

      canvas.width = Math.floor(renderVp.width);
      canvas.height = Math.floor(renderVp.height);
      canvas.style.width = `${cssWidth}px`;
      canvas.style.height = `${cssHeight}px`;

      ctx.imageSmoothingQuality = "high";

      setRenderInfo({ cssScale, width: cssWidth, height: cssHeight });

      const task = page.render({ canvasContext: ctx, viewport: renderVp, canvas });
      await task.promise;
      if (cancelled) return;
    })();

    return () => {
      cancelled = true;
    };
  }, [doc, pageNum, box.w, box.h]);

  const overlayRects = useMemo(() => {
    if (!previewEnabled || !highlights.length || renderInfo.width <= 0 || renderInfo.cssScale <= 0) {
      return [] as Array<{ id: string; x: number; y: number; width: number; height: number; action: RuleActionRecord }>;
    }

    const scale = renderInfo.cssScale;
    const maxW = renderInfo.width;
    const maxH = renderInfo.height;
    const toNumber = (value: unknown) => (typeof value === "number" ? value : Number(value ?? 0));

    const result: Array<{ id: string; x: number; y: number; width: number; height: number; action: RuleActionRecord }> = [];
    for (const action of highlights) {
      const bbox = action.bbox ?? {};
      const xEmu = toNumber((bbox as any).x);
      const yEmu = toNumber((bbox as any).y);
      const wEmu = toNumber((bbox as any).w);
      const hEmu = toNumber((bbox as any).h);
      if (wEmu <= 0 || hEmu <= 0) continue;

      const cssX = (xEmu / EMU_PER_POINT) * scale;
      const cssY = (yEmu / EMU_PER_POINT) * scale;
      const cssW = (wEmu / EMU_PER_POINT) * scale;
      const cssH = (hEmu / EMU_PER_POINT) * scale;

      if (cssW <= 1 || cssH <= 1) continue;

      const clampedX = Math.max(0, Math.min(cssX, Math.max(0, maxW - 1)));
      const clampedY = Math.max(0, Math.min(cssY, Math.max(0, maxH - 1)));
      const clampedW = Math.max(2, Math.min(cssW, Math.max(0, maxW - clampedX)));
      const clampedH = Math.max(2, Math.min(cssH, Math.max(0, maxH - clampedY)));

      result.push({ id: action.id, x: clampedX, y: clampedY, width: clampedW, height: clampedH, action });
    }

    return result;
  }, [previewEnabled, highlights, renderInfo.width, renderInfo.height, renderInfo.cssScale]);

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
      <div
        style={{
          position: "relative",
          boxShadow: "0 2px 14px rgba(0,0,0,.08)",
          borderRadius: 8,
          background: "#fff",
          overflow: "hidden",
          width: renderInfo.width ? `${renderInfo.width}px` : "auto",
          height: renderInfo.height ? `${renderInfo.height}px` : "auto",
        }}
      >
        <canvas
          ref={canvasRef}
          style={{
            display: "block",
            width: renderInfo.width ? `${renderInfo.width}px` : "auto",
            height: renderInfo.height ? `${renderInfo.height}px` : "auto",
            borderRadius: 8,
            background: "#fff",
          }}
        />

        {previewEnabled && (
          <div
            style={{
              position: "absolute",
              inset: 0,
              pointerEvents: "none",
            }}
          >
            {loadingHighlights && (
              <div
                style={{
                  position: "absolute",
                  top: 12,
                  right: 12,
                  background: "rgba(15,23,42,0.75)",
                  color: "#fff",
                  fontSize: 11,
                  padding: "4px 8px",
                  borderRadius: 999,
                  letterSpacing: 0.2,
                }}
              >
                Loading…
              </div>
            )}

            {overlayRects.map((rect) => {
              const original = (rect.action.original_text ?? "").trim();
              const replacement = (rect.action.new_text ?? "").trim();
              const labelText = replacement || (original ? "[REDACTED]" : "Updated");
              const snippet = labelText.length > 90 ? `${labelText.slice(0, 87)}…` : labelText;

              return (
                <div
                  key={rect.id}
                  style={{
                    position: "absolute",
                    left: `${rect.x}px`,
                    top: `${rect.y}px`,
                    width: `${rect.width}px`,
                    height: `${rect.height}px`,
                    border: "2px solid rgba(37,99,235,0.85)",
                    borderRadius: 6,
                    background: "rgba(37,99,235,0.16)",
                    boxShadow: "0 12px 30px rgba(37,99,235,0.18)",
                  }}
                >
                  <div
                    style={{
                      position: "absolute",
                      top: 4,
                      left: 4,
                      right: 4,
                      background: "rgba(255,255,255,0.92)",
                      color: "#1d4ed8",
                      fontSize: 11,
                      fontWeight: 600,
                      borderRadius: 4,
                      padding: "2px 4px",
                      lineHeight: 1.25,
                      pointerEvents: "none",
                      maxHeight: "65%",
                      overflow: "hidden",
                    }}
                  >
                    {snippet}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
function PreviewToggleButton({
  enabled,
  onToggle,
  busy,
  disabled,
  total,
  current,
}: {
  enabled: boolean;
  onToggle: () => void;
  busy?: boolean;
  disabled?: boolean;
  total?: number;
  current?: number;
}) {
  const active = enabled && !disabled;
  const hasTotal = typeof total === "number" && total > 0;
  const currentCount = typeof current === "number" ? current : 0;

  let label = "Preview rules";
  if (disabled) {
    label = "Preview rules";
  } else if (busy && !active) {
    label = "Loading…";
  } else if (active) {
    if (busy) {
      label = "Loading…";
    } else if (currentCount > 0) {
      label = `Showing ${currentCount} change${currentCount === 1 ? "" : "s"}`;
    } else {
      label = "Previewing rules";
    }
  } else if (hasTotal) {
    label = `Preview rules (${total})`;
  }

  return (
    <button
      type="button"
      onClick={() => {
        if (disabled) return;
        onToggle();
      }}
      disabled={disabled}
      style={{
        appearance: "none",
        border: `1px solid ${disabled ? "#dbeafe" : active ? "#1d4ed8" : "#cbd5f5"}`,
        borderRadius: 999,
        padding: "6px 16px",
        background: disabled ? "#e2e8f0" : active ? "#1d4ed8" : "#e0ecff",
        color: disabled ? "#94a3b8" : active ? "#fff" : "#1d4ed8",
        fontSize: 13,
        fontWeight: 600,
        cursor: disabled ? "not-allowed" : "pointer",
        transition: "all 0.15s ease",
        boxShadow: active ? "0 8px 18px rgba(29,78,216,0.18)" : "none",
        opacity: busy && !active ? 0.85 : 1,
      }}
    >
      {label}
    </button>
  );
}
