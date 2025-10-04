import type { ReactNode } from "react";
import type { AppView } from "../../types/app";

type TopNavProps = {
  activeView: AppView;
  onSelect: (view: AppView) => void;
};

type IconButtonProps = {
  label: string;
  active?: boolean;
  onClick: () => void;
  children: ReactNode;
};

function IconButton({ label, active, onClick, children }: IconButtonProps) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={label}
      aria-label={label}
      style={{
        display: "inline-flex",
        alignItems: "center",
        justifyContent: "center",
        gap: 8,
        border: "1px solid " + (active ? "#2563eb" : "#d1d5db"),
        background: active ? "#ebf2ff" : "#ffffff",
        color: "#111827",
        borderRadius: 999,
        width: 40,
        height: 40,
        cursor: "pointer",
        boxShadow: active ? "0 2px 6px rgba(37,99,235,0.15)" : "0 1px 2px rgba(15,23,42,0.06)",
        transition: "all 0.15s ease",
      }}
      aria-pressed={active}
    >
      <span
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          lineHeight: 0,
        }}
      >
        {children}
      </span>
    </button>
  );
}

function HomeIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#111827" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M3 11l9-7 9 7" />
      <path d="M9 21V11H15V21" />
    </svg>
  );
}

function LibraryIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#111827" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M4 19V5a2 2 0 012-2h2" />
      <path d="M10 3h4" />
      <path d="M18 3h2a2 2 0 012 2v14" />
      <rect x="6" y="7" width="12" height="14" rx="2" />
    </svg>
  );
}

function PlusIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#111827" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 5v14" />
      <path d="M5 12h14" />
    </svg>
  );
}

export function TopNav({ activeView, onSelect }: TopNavProps) {
  return (
    <header
      style={{
        display: "flex",
        alignItems: "center",
        justifyContent: "space-between",
        padding: "10px 20px",
        borderBottom: "1px solid #e5e7eb",
        background: "#ffffffd9",
        backdropFilter: "blur(10px)",
        position: "sticky",
        top: 0,
        zIndex: 10,
      }}
    >
      <nav style={{ display: "flex", alignItems: "center", gap: 12 }}>
        <IconButton label="Home" active={activeView === "home"} onClick={() => onSelect("home")}>
          <HomeIcon />
        </IconButton>
        <IconButton label="Library" active={activeView === "library"} onClick={() => onSelect("library")}
        >
          <LibraryIcon />
        </IconButton>
        <IconButton label="New workspace" active={activeView === "upload"} onClick={() => onSelect("upload")}
        >
          <PlusIcon />
        </IconButton>
      </nav>
      <button
        type="button"
        style={{
          background: "transparent",
          border: "1px solid #d1d5db",
          borderRadius: 999,
          padding: "6px 14px",
          fontSize: 14,
          fontWeight: 500,
          color: "#111827",
          cursor: "pointer",
        }}
      >
        Log out
      </button>
    </header>
  );
}
