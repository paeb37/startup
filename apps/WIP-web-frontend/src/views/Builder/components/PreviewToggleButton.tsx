export function PreviewToggleButton({
  enabled,
  onToggle,
  busy,
  disabled,
}: {
  enabled: boolean;
  onToggle: () => void;
  busy?: boolean;
  disabled?: boolean;
}) {
  const active = enabled && !disabled;

  let label = "Preview rules";
  if (disabled) {
    label = "Preview rules";
  } else if (busy && !active) {
    label = "Loading…";
  } else if (active) {
    label = busy ? "Loading…" : "Previewing rules";
  }

  return (
    <button
      type="button"
      onClick={() => {
        if (disabled) return;
        onToggle();
      }}
      disabled={disabled}
      style={{
        appearance: "none",
        border: `1px solid ${disabled ? "#dbeafe" : active ? "#1d4ed8" : "#cbd5f5"}`,
        borderRadius: 999,
        padding: "6px 16px",
        background: disabled ? "#e2e8f0" : active ? "#1d4ed8" : "#e0ecff",
        color: disabled ? "#94a3b8" : active ? "#fff" : "#1d4ed8",
        fontSize: 13,
        fontWeight: 600,
        cursor: disabled ? "not-allowed" : "pointer",
        transition: "all 0.15s ease",
        boxShadow: active ? "0 8px 18px rgba(29,78,216,0.18)" : "none",
        opacity: busy && !active ? 0.85 : 1,
      }}
    >
      {label}
    </button>
  );
}
