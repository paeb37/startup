import { useEffect, useState } from "react";
import { getDocument } from "pdfjs-dist";
import type { PDFDocumentProxy } from "pdfjs-dist/types/src/display/api";

export function usePdfDocument(src?: string) {
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

export function usePageBitmap(doc: PDFDocumentProxy | null, pageNum: number, cssWidth: number) {
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

      const dpr = window.devicePixelRatio || 1;
      const scale = cssWidth / baseViewport.width;
      const renderViewport = page.getViewport({ scale: scale * dpr });

      const canvas = document.createElement("canvas");
      canvas.width = Math.max(1, Math.floor(renderViewport.width));
      canvas.height = Math.max(1, Math.floor(renderViewport.height));

      const ctx = canvas.getContext("2d");
      if (!ctx) return;
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
