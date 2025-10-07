import { useEffect, useState } from "react";

import { configurePdfWorker } from "./config/pdf";
import type { AppView, BuilderSeed } from "./types/app";
import { TopNav } from "./components/navigation/TopNav";
import { HomeView } from "./views/Home/HomeView";
import { LibraryView } from "./views/Library/LibraryView";
import { UploadView } from "./views/Upload/UploadView";
import { RuleBuilder } from "./views/Builder/RuleBuilder";

configurePdfWorker();

export default function App() {
  const [view, setView] = useState<AppView>("home");
  const [builderSeed, setBuilderSeed] = useState<BuilderSeed | null>(null);

  // Lock screen to avoid "flowy" behavior
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
          <UploadView
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
