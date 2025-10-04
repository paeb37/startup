import { getDocument } from "pdfjs-dist";
import type { ExtractResponse } from "../types/deck";
import type { RenderResult } from "../types/app";

export async function uploadDeck(
  file: File,
  instructions?: string,
  industry?: string,
  deckType?: string,
): Promise<{ deck: ExtractResponse; render: RenderResult; deckId?: string }> {
  const form = new FormData();
  form.append("file", file);
  if (instructions && instructions.trim()) {
    form.append("instructions", instructions.trim());
  }
  if (industry && industry.trim()) {
    form.append("industry", industry.trim());
  }
  if (deckType && deckType.trim()) {
    form.append("deckType", deckType.trim());
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
