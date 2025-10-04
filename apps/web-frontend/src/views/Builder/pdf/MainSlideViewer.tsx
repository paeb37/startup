import { useEffect, useMemo, useRef, useState } from "react";
import type { PDFDocumentProxy, PDFPageProxy } from "pdfjs-dist/types/src/display/api";

import { EMU_PER_POINT } from "../../../config/env";
import type { RuleActionRecord } from "../../../types/rules";

export type MainSlideViewerProps = {
  doc: PDFDocumentProxy;
  pageNum: number;
  highlights?: RuleActionRecord[];
  previewEnabled?: boolean;
  loadingHighlights?: boolean;
  showHighlights?: boolean;
};

export function MainSlideViewer({
  doc,
  pageNum,
  highlights = [],
  previewEnabled = false,
  loadingHighlights = false,
  showHighlights = true,
}: MainSlideViewerProps) {
  const wrapRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [box, setBox] = useState({ w: 0, h: 0 });
  const [renderInfo, setRenderInfo] = useState({
    cssScale: 1,
    width: 0,
    height: 0,
    viewXMin: 0,
    viewYMin: 0,
    viewWidth: 0,
    viewHeight: 0,
    rotation: 0,
  });

  useEffect(() => {
    const el = wrapRef.current;
    if (!el) return;

    const updateBox = () => {
      const rect = el.getBoundingClientRect();
      if (rect.width > 0 && rect.height > 0) {
        setBox({ w: rect.width, h: rect.height });
      }
    };

    const ro = new ResizeObserver((entries) => {
      const r = entries[0].contentRect;
      if (r.width > 0 && r.height > 0) {
        setBox({ w: r.width, h: r.height });
      }
    });

    ro.observe(el);

    const timeouts = [
      setTimeout(updateBox, 0),
      setTimeout(updateBox, 10),
      setTimeout(updateBox, 50),
      setTimeout(updateBox, 100),
    ];

    return () => {
      ro.disconnect();
      timeouts.forEach(clearTimeout);
    };
  }, []);

  useEffect(() => {
    let cancelled = false;

    (async () => {
      if (!doc || !pageNum) return;
      if (box.w <= 0 || box.h <= 0) {
        const el = wrapRef.current;
        if (el) {
          const rect = el.getBoundingClientRect();
          if (rect.width > 0 && rect.height > 0) {
            setBox({ w: rect.width, h: rect.height });
          } else {
            setTimeout(() => {
              if (!cancelled) {
                const elCurrent = wrapRef.current;
                if (elCurrent) {
                  const fallbackRect = elCurrent.getBoundingClientRect();
                  if (fallbackRect.width > 0 && fallbackRect.height > 0) {
                    setBox({ w: fallbackRect.width, h: fallbackRect.height });
                  }
                }
              }
            }, 100);
            return;
          }
        } else {
          return;
        }
      }

      const page: PDFPageProxy = await doc.getPage(pageNum);
      const baseViewport = page.getViewport({ scale: 1 });
      const dpr = window.devicePixelRatio || 1;

      const MAX_CSS_W = 1200;
      const MIN_AVAIL_W = 400;
      const targetWidth = Math.max(MIN_AVAIL_W, Math.min(box.w - 32, MAX_CSS_W));

      const scale = targetWidth / baseViewport.width;
      const renderViewport = page.getViewport({ scale: scale * dpr });

      const tempCanvas = document.createElement("canvas");
      tempCanvas.width = Math.max(1, Math.floor(renderViewport.width));
      tempCanvas.height = Math.max(1, Math.floor(renderViewport.height));

      const tempCtx = tempCanvas.getContext("2d");
      if (!tempCtx) return;
      tempCtx.imageSmoothingQuality = "high";

      const renderTask = page.render({ canvasContext: tempCtx, viewport: renderViewport, canvas: tempCanvas });
      await renderTask.promise;

      if (cancelled) return;

      const canvas = canvasRef.current;
      if (!canvas) return;
      const ctx = canvas.getContext("2d");
      if (!ctx) return;

      canvas.width = Math.max(1, Math.floor(renderViewport.width));
      canvas.height = Math.max(1, Math.floor(renderViewport.height));
      canvas.style.width = `${targetWidth}px`;
      canvas.style.height = `${Math.floor(baseViewport.height * scale)}px`;

      ctx.imageSmoothingQuality = "high";
      ctx.drawImage(tempCanvas, 0, 0);

      setRenderInfo({
        cssScale: scale,
        width: targetWidth,
        height: Math.floor(baseViewport.height * scale),
        viewXMin: 0,
        viewYMin: 0,
        viewWidth: baseViewport.width,
        viewHeight: baseViewport.height,
        rotation: 0,
      });
    })();

    return () => {
      cancelled = true;
    };
  }, [doc, pageNum, box.w, box.h]);

  const overlayRects = useMemo(() => {
    if (
      !previewEnabled ||
      !showHighlights ||
      !highlights.length ||
      renderInfo.width <= 0 ||
      renderInfo.cssScale <= 0 ||
      renderInfo.viewWidth === undefined ||
      renderInfo.viewHeight === undefined
    ) {
      return [] as Array<{ id: string; x: number; y: number; width: number; height: number; action: RuleActionRecord }>;
    }

    const scale = renderInfo.cssScale;
    const maxW = renderInfo.width;
    const maxH = renderInfo.height;
    const offsetX = (renderInfo.viewXMin ?? 0) * scale;
    const offsetY = (renderInfo.viewYMin ?? 0) * scale;
    const toNumber = (value: unknown) => (typeof value === "number" ? value : Number(value ?? 0));

    const result: Array<{ id: string; x: number; y: number; width: number; height: number; action: RuleActionRecord }> = [];
    for (const action of highlights) {
      const bbox = action.bbox ?? {};
      const xEmu = toNumber((bbox as any).x);
      const yEmu = toNumber((bbox as any).y);
      const wEmu = toNumber((bbox as any).w);
      const hEmu = toNumber((bbox as any).h);
      if (wEmu <= 0 || hEmu <= 0) continue;

      const cssX = (xEmu / EMU_PER_POINT) * scale - offsetX;
      const cssY = (yEmu / EMU_PER_POINT) * scale - offsetY;
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
  }, [previewEnabled, showHighlights, highlights, renderInfo.width, renderInfo.height, renderInfo.cssScale]);

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

        {previewEnabled && showHighlights && (
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
