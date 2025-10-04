import {
  STORAGE_PUBLIC_BASE,
  SLIDE_IMAGE_BASE,
} from "../config/env";
import type { DeckPreview, SlideReference } from "../types/deck";

export const FALLBACK_DECKS: DeckPreview[] = [
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

export type LibraryDeck = DeckPreview & {
  industry: string;
  deckType: string;
  summary: string;
};

export const LIBRARY_INDUSTRIES = [
  "Technology",
  "Healthcare",
  "Finance",
  "Education",
  "Retail",
  "Manufacturing",
];

export const LIBRARY_TYPES = [
  "Overview",
  "Pitch",
  "Report",
  "Training",
  "Strategy",
];

export function toPublicUrl(path?: string) {
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

  return `${base}/${clean}`;
}

export function resolveAssetUrl(raw?: string) {
  if (!raw) return undefined;
  if (raw.startsWith("data:")) return raw;
  if (/^https?:\/\//i.test(raw)) return raw;
  return toPublicUrl(raw);
}

export function buildSlideImageUrl(deckId?: string, slideNumber?: number, provided?: string) {
  if (provided && provided.startsWith("data:")) return provided;
  if (provided && /^https?:\/\//i.test(provided)) return provided;
  if (!deckId || !slideNumber) return provided;

  const base = SLIDE_IMAGE_BASE || "";
  const origin = base ? base.replace(/\/$/, "") : "";
  return `${origin}/api/decks/${deckId}/slides/${slideNumber}`;
}

export function enrichDeckForLibrary(deck: DeckPreview, index: number): LibraryDeck {
  const rawIndustry = typeof deck.industry === "string" ? deck.industry.trim() : "";
  const rawType = typeof deck.deckType === "string" ? deck.deckType.trim() : "";
  const industry = rawIndustry.length > 0 ? rawIndustry : LIBRARY_INDUSTRIES[index % LIBRARY_INDUSTRIES.length];
  const deckType = rawType.length > 0 ? rawType : LIBRARY_TYPES[index % LIBRARY_TYPES.length];
  const summary = deck.description?.trim() || "No summary available yet.";

  return {
    ...deck,
    industry,
    deckType,
    summary,
  };
}

export function formatRelativeDate(iso?: string) {
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

export function normalizeDeckRecord(raw: any): DeckPreview | null {
  if (!raw || typeof raw !== "object") return null;

  const deckId = typeof raw.id === "string" ? raw.id : typeof raw.deck_id === "string" ? raw.deck_id : undefined;
  const description = typeof raw.description === "string" ? raw.description : raw.summary;
  const slideCount = typeof raw.slideCount === "number" ? raw.slideCount : typeof raw.slide_count === "number" ? raw.slide_count : undefined;
  const updated = raw.updated_at ?? raw.updatedAt ?? raw.modified;
  const pptxCandidate = raw.pptxPath ?? raw.pptx_path;
  const pdfCandidate = raw.pdfPath ?? raw.pdf_url ?? raw.pdfUrl;
  const redactedPptxCandidate = raw.redactedPptxPath ?? raw.redacted_pptx_path;
  const redactedPdfCandidate = raw.redactedPdfPath ?? raw.redacted_pdf_path;
  const redactedJsonCandidate = raw.redactedJsonPath ?? raw.redacted_json_path;
  const thumbnailCandidate = raw.coverThumbnailUrl ?? raw.cover_thumbnail_url ?? raw.thumbnailUrl ?? raw.thumbnail_url;

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
    industry: typeof raw.industry === "string" ? raw.industry : undefined,
    deckType: typeof raw.deck_type === "string" ? raw.deck_type : typeof raw.deckType === "string" ? raw.deckType : undefined,
    slideCount,
    finalized,
  };
}

export function inferDeckNameFromPath(path: string): string | undefined {
  if (!path) return undefined;
  const segments = path.split("/").filter(Boolean);
  if (segments.length === 0) return undefined;
  const last = segments[segments.length - 1];
  const dot = last.lastIndexOf(".");
  const stem = dot >= 0 ? last.slice(0, dot) : last;
  return stem.trim() || undefined;
}

export function normalizeSlideReference(raw: any): SlideReference | null {
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

  return {
    slideId,
    deckId,
    deckName,
    slideNumber,
    similarity,
    thumbnailUrl,
  };
}

export function normalizeSlideReferences(raw: any): SlideReference[] | undefined {
  if (!Array.isArray(raw)) return undefined;
  const list: SlideReference[] = [];
  for (const item of raw) {
    const normalized = normalizeSlideReference(item);
    if (normalized) list.push(normalized);
  }
  return list.length > 0 ? list : undefined;
}
