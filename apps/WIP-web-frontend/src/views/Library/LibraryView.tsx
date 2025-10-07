import { useCallback, useMemo, useState } from "react";

import {
  LIBRARY_INDUSTRIES,
  LIBRARY_TYPES,
  buildSlideImageUrl,
  formatRelativeDate,
  resolveAssetUrl,
} from "../../services/decks";
import type { LibraryDeck } from "../../services/decks";
import { useLibraryDecks } from "../../hooks/useDecks";
import { EmptyState } from "../../components/common/EmptyState";

export function LibraryView() {
  const { decks, loading, error, reload } = useLibraryDecks();
  const [searchTerm, setSearchTerm] = useState("");
  const [selectedIndustries, setSelectedIndustries] = useState<string[]>([]);
  const [selectedTypes, setSelectedTypes] = useState<string[]>([]);

  const filteredDecks = useMemo(() => {
    const term = searchTerm.trim().toLowerCase();
    return decks.filter((deck) => {
      const industryMatch = selectedIndustries.length === 0 || selectedIndustries.includes(deck.industry);
      const typeMatch = selectedTypes.length === 0 || selectedTypes.includes(deck.deckType);
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

        <div
          style={{
            position: "relative",
            overflowY: "auto",
            background: "#ffffff",
            borderRadius: 20,
            border: "1px solid #e5e7eb",
          }}
        >
          {error && (
            <div style={{ position: "absolute", top: 16, right: 20, fontSize: 12, color: "#b91c1c" }}>{error}</div>
          )}

          {loading && decks.length === 0 ? (
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
  const [downloading, setDownloading] = useState(false);
  const [downloadError, setDownloadError] = useState<string | null>(null);
  const initials = deck.title
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part.slice(0, 1).toUpperCase())
    .join("") || "DX";
  const updated = formatRelativeDate(deck.updatedAt);
  const summary = deck.summary.length > 160 ? `${deck.summary.slice(0, 157)}…` : deck.summary;
  const downloadVariant = deck.finalized ? "redacted" : "original";

  const handleDownload = useCallback(async () => {
    if (downloading) return;
    setDownloading(true);
    setDownloadError(null);

    try {
      const response = await fetch(`/api/decks/${deck.id}/download?variant=${downloadVariant}`);
      if (!response.ok) {
        const message = await response.text().catch(() => "");
        throw new Error(message || `Download failed (${response.status})`);
      }

      const blob = await response.blob();
      const disposition = response.headers.get("content-disposition") ?? "";
      const fileNameMatch = /filename\*=UTF-8''([^;]+)|filename="?([^";]+)"?/i.exec(disposition);
      const encodedName = fileNameMatch?.[1] ?? fileNameMatch?.[2] ?? `${deck.title || "deck"}.pptx`;
      const decodedName = decodeURIComponent(encodedName);

      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = decodedName;
      anchor.rel = "noopener";
      document.body.appendChild(anchor);
      anchor.click();
      document.body.removeChild(anchor);
      URL.revokeObjectURL(url);
    } catch (err) {
      const message = err instanceof Error ? err.message : "Download failed";
      setDownloadError(message);
    } finally {
      setDownloading(false);
    }
  }, [deck.id, deck.title, downloadVariant, downloading]);

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
            {deck.deckType}
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

        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          <button
            type="button"
            onClick={handleDownload}
            disabled={downloading}
            style={{
              border: "1px solid #2563eb",
              background: downloading ? "#e0ecff" : "#2563eb",
              color: downloading ? "#1d4ed8" : "#fff",
              borderRadius: 999,
              padding: "8px 14px",
              fontSize: 13,
              fontWeight: 600,
              cursor: downloading ? "wait" : "pointer",
              transition: "background 0.2s ease",
            }}
          >
            {downloading ? "Preparing…" : "Download deck"}
          </button>
        </div>
        {downloadError && <div style={{ fontSize: 12, color: "#b91c1c" }}>{downloadError}</div>}
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
        border: "1px solid #e5e7eb",
        padding: 16,
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
        <div style={{ height: 14, background: "#e5e7eb", borderRadius: 999 }} />
        <div style={{ height: 12, background: "#eef2f7", borderRadius: 999 }} />
        <div style={{ height: 12, background: "#f8fafc", borderRadius: 999, width: "50%" }} />
      </div>
    </article>
  );
}
