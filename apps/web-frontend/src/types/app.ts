import type { PDFDocumentProxy } from "pdfjs-dist/types/src/display/api";
import type { ExtractResponse } from "./deck";

export type AppView = "home" | "library" | "builder" | "upload";

export type RenderResult = {
  pdfUrl: string;
  doc: PDFDocumentProxy;
};

export type BuilderSeed = {
  deck: ExtractResponse;
  render: RenderResult;
  instructions: string;
  fileName?: string;
  deckId?: string;
};
