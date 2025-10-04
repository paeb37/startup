import { GlobalWorkerOptions } from "pdfjs-dist";
import workerSrc from "pdfjs-dist/build/pdf.worker.min.mjs?url";

let configured = false;

export function configurePdfWorker() {
  if (configured) return;
  GlobalWorkerOptions.workerSrc = workerSrc;
  configured = true;
}
