export function UploadDeckButton({
  onClick,
  busy,
  disabled,
}: {
  onClick: () => void;
  busy?: boolean;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      style={{
        appearance: "none",
        border: "1px solid #111827",
        borderRadius: 10,
        padding: "10px 18px",
        background: "#111827",
        color: "#fff",
        fontSize: 14,
        fontWeight: 600,
        cursor: disabled ? "not-allowed" : "pointer",
        transition: "transform 0.15s ease",
        opacity: disabled ? 0.6 : 1,
      }}
      onClick={() => {
        if (disabled) return;
        onClick();
      }}
      disabled={disabled}
    >
      {busy ? "Saving…" : "Upload redacted deck"}
    </button>
  );
}
