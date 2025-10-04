export type BBox = { x?: number; y?: number; w?: number; h?: number };

export type Paragraph = { level: number; text: string };

export type ElementCommon = {
  key: string;
  id: number;
  name?: string;
  bbox: BBox;
  z: number;
};

export type TextboxElement = ElementCommon & {
  type: "textbox";
  paragraphs: Paragraph[];
};

export type PictureElement = ElementCommon & {
  type: "picture";
  imgPath?: string;
  bytes?: number;
};

export type TableCell = {
  r: number;
  c: number;
  rowSpan: number;
  colSpan: number;
  paragraphs: Paragraph[];
  cellBox?: BBox;
};

export type TableElement = ElementCommon & {
  type: "table";
  rows: number;
  cols: number;
  colWidths?: number[];
  rowHeights?: number[];
  cells: (TableCell | null)[][];
};

export type Element = TextboxElement | PictureElement | TableElement;

export type Slide = { index: number; elements: Element[] };

export type Deck = {
  file: string;
  slideCount: number;
  slideWidthEmu?: number;
  slideHeightEmu?: number;
  slides: Slide[];
};

export type ExtractResponse = Deck | { error: string };

export type DeckPreview = {
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
  industry?: string | null;
  deckType?: string | null;
};

export type SlideReference = {
  slideId?: string;
  deckId?: string;
  deckName?: string;
  slideNumber?: number;
  similarity?: number;
  thumbnailUrl?: string;
};

export function isDeckResponse(value: ExtractResponse | null | undefined): value is Deck {
  if (!value || typeof value !== "object") return false;
  const candidate = value as Deck;
  return Array.isArray(candidate.slides) && typeof candidate.slideCount === "number";
}
