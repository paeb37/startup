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

type ChatMsg = { id: string; role: "user" | "assistant"; content: string };

function HomeView() {
  const [query, setQuery] = useState("");
  const [messages, setMessages] = useState<ChatMsg[]>([
    { id: crypto.randomUUID(), role: "assistant", content: "Ask me about your slides or data." },
  ]);
  const [sending, setSending] = useState(false);

  // Left rail list (unchanged demo data)
  const [chatItems, setChatItems] = useState<ChatItem[]>([
    { title: "Onboarding decks", time: "Yesterday" },
    { title: "Security briefing", time: "2 days ago" },
    { title: "Q4 metrics", time: "Last week" },
  ]);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    const text = query.trim();
    if (!text || sending) return;

    // optimistic user bubble
    const userMsg: ChatMsg = { id: crypto.randomUUID(), role: "user", content: text };
    setMessages((prev) => [...prev, userMsg]);
    setQuery("");
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
    } catch {
      setMessages((prev) => [
        ...prev,
        { id: crypto.randomUUID(), role: "assistant", content: "Network error. Try again." },
      ]);
    } finally {
      setSending(false);
    }
  }

  return (
    <div style={{ height: "100%", display: "grid", gridTemplateColumns: "200px 1fr", minHeight: 0 }}>
      {/* Left rail (Recent) */}
      <aside
        style={{
          borderRight: "1px solid #e5e7eb",
          background: "#f8fafc",
          padding: "24px 20px",
          overflow: "auto",
          color: "#1f2937",
        }}
      >
        <button
          type="button"
          onClick={() =>
            setMessages([{ id: crypto.randomUUID(), role: "assistant", content: "New chat started." }])
          }
          style={{
            width: "100%",
            padding: "12px 14px",
            borderRadius: 10,
            border: "1px solid #d1d5db",
            background: "#ffffff",
            color: "#1f2937",
            fontSize: 14,
            fontWeight: 600,
            display: "flex",
            alignItems: "center",
            gap: 10,
            justifyContent: "center",
            cursor: "pointer",
            marginBottom: 20,
            boxShadow: "0 1px 2px rgba(15,23,42,0.08)",
          }}
        >
          <span
            style={{
              display: "inline-flex",
              alignItems: "center",
              justifyContent: "center",
              width: 22,
              height: 22,
              borderRadius: "50%",
              background: "#2563eb",
              color: "#fff",
              fontSize: 16,
              lineHeight: 1,
            }}
          >
            +
          </span>
          New chat
        </button>

        <h2 style={{ fontSize: 13, textTransform: "uppercase", letterSpacing: 0.8, margin: "0 0 12px", color: "#6b7280" }}>
          Recent
        </h2>
        <div style={{ display: "grid", gap: 6 }}>
          {chatItems.map((item, idx) => (
            <button
              key={idx}
              type="button"
              style={{
                textAlign: "left",
                padding: "10px 12px",
                borderRadius: 8,
                border: "1px solid transparent",
                background: "transparent",
                color: "#1f2937",
                fontSize: 13,
                lineHeight: 1.4,
                cursor: "pointer",
                transition: "background 0.15s ease, border 0.15s ease",
              }}
            >
              <div style={{ fontWeight: 500 }}>{item.title}</div>
              <div style={{ fontSize: 11, color: "#9ca3af", marginTop: 4 }}>{item.time}</div>
            </button>
          ))}
        </div>
      </aside>

      {/* Center: messages + composer (ChatGPT-like) */}
      <main style={{ display: "grid", gridTemplateRows: "1fr auto", minHeight: 0, padding: "0 24px", background: "#fff"}}>
        {/* Message list */}
        <div
          style={{
            overflow: "auto",
            padding: "24px 0",
            display: "flex",
            flexDirection: "column",
            gap: 14,
          }}
        >
          {messages.map((m) =>
            m.role === "assistant" ? (
              <AssistantBlock key={m.id} text={m.content} />
            ) : (
              <UserBubble key={m.id} text={m.content} />
            )
          )}
        </div>


        {/* Composer */}
        <form onSubmit={handleSubmit} style={{ position: "sticky", bottom: 0, background: "transparent", padding: "16px 0" }}>
          <div
            style={{
              display: "flex",
              alignItems: "center",
              gap: 12,
              background: "#fff",
              borderRadius: 999,
              border: "1px solid #d1d5db",
              padding: "14px 18px",
              boxShadow: "0 12px 24px rgba(15,23,42,0.08)",
              maxWidth: 780,
              margin: "0 auto",
            }}
          >
            <span
              style={{
                display: "inline-flex",
                alignItems: "center",
                justifyContent: "center",
                width: 28,
                height: 28,
                borderRadius: "50%",
                background: "#e5e7eb",
                fontWeight: 600,
                color: "#111827",
              }}
            >
              Q
            </span>
            <input
              type="text"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="What do you want to know?"
              style={{
                flex: 1,
                border: "none",
                outline: "none",
                fontSize: 16,
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
                padding: "8px 14px",
                fontWeight: 600,
                cursor: query.trim() && !sending ? "pointer" : "not-allowed",
                opacity: query.trim() && !sending ? 1 : 0.6,
              }}
            >
              Send
            </button>
          </div>
        </form>
      </main>
    </div>
  );
}

function UserBubble({ text }: { text: string }) {
  return (
    <div style={{ display: "flex", justifyContent: "flex-end", padding: "0 8px" }}>
      <div
        style={{
          display: "inline-block",
          maxWidth: "min(680px, 70vw)",
          background: "#f3f4f6",            // light gray
          color: "#111827",
          border: "1px solid #e5e7eb",
          padding: "10px 14px",
          borderRadius: "16px 16px 4px 16px",
          lineHeight: 1.45,
          whiteSpace: "pre-wrap",
          wordBreak: "break-word",
        }}
      >
        {text}
      </div>
    </div>
  );
}

function AssistantBlock({ text }: { text: string }) {
  return (
    <div style={{ padding: "0 8px" }}>
      <div
        style={{
          width: "100%",
          maxWidth: 820,             // readable width like ChatGPT
          margin: "0 auto",          // centered on the page
          background: "transparent", // ← no card/bubble
          color: "#111827",
          border: "none",
          padding: 0,                // ← no extra box padding
          boxShadow: "none",
          lineHeight: 1.65,
          whiteSpace: "pre-wrap",
          wordBreak: "break-word",
          fontSize: 16,
        }}
      >
        {text}
      </div>
    </div>
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
      const { deck, render } = await uploadDeck(file);
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
          {busy ? "Uploading…" : "Upload & open builder"}
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
      const { deck, render } = await uploadDeck(file);
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
        {busy ? "Uploading…" : "Upload & Render"}
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
        (UI for “if … then …” rules goes here)
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

async function uploadDeck(file: File): Promise<{ deck: ExtractResponse; render: RenderResult }> {
  const fd = new FormData();
  fd.append("file", file);

  const [jsonRes, pdfBlob] = await Promise.all([
    fetch("/api/extract", {
      method: "POST",
      body: fd,
      headers: { Accept: "application/json" },
    }).then(async (r) => {
      try {
        return await r.json();
      } catch {
        return { error: "Failed to parse extract response" } as ExtractResponse;
      }
    }),
    fetch("/api/render", {
      method: "POST",
      body: fd,
      headers: { Accept: "application/pdf" },
    }).then(async (r) => {
      if (!r.ok) {
        const message = await r.text().catch(() => "");
        throw new Error(message || "PDF render failed");
      }
      return r.blob();
    }),
  ]);

  const deck = jsonRes as ExtractResponse;
  const pdfUrl = URL.createObjectURL(pdfBlob);

  try {
    const buf = await pdfBlob.arrayBuffer();
    const doc = await getDocument({ data: buf }).promise;
    return { deck, render: { pdfUrl, doc } };
  } catch (err) {
    URL.revokeObjectURL(pdfUrl);
    throw err;
  }
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

      const renderTask = page.render({ canvasContext: ctx, viewport: renderViewport });
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
        <div style={{ height: 160, display: "grid", placeItems: "center", color: "#9ca3af" }}>Loading…</div>
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

      const task = page.render({ canvasContext: ctx, viewport: renderVp });
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
