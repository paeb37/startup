import { useEffect, useState } from "react";
import type { PDFDocumentProxy } from "pdfjs-dist/types/src/display/api";

import { usePageBitmap } from "../../../hooks/usePdf";

export type ThumbRailProps = {
  doc: PDFDocumentProxy;
  selected: number;
  onSelect: (page: number) => void;
};

export function ThumbRail({ doc, selected, onSelect }: ThumbRailProps) {
  const [pageCount, setPageCount] = useState(0);

  useEffect(() => {
    setPageCount(doc.numPages);
  }, [doc]);

  const THUMB_W = 200;

  return (
    <div style={{ display: "grid", gap: 10 }}>
      {Array.from({ length: pageCount }, (_, index) => {
        const pageNum = index + 1;
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
