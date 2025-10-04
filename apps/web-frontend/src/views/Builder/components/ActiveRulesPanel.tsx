import type { RulesHookState } from "../../../hooks/useRules";
import { formatTimestamp } from "../../../hooks/useRules";

export function ActiveRulesPanel({
  deckId,
  rulesState,
}: {
  deckId: string | null;
  rulesState: RulesHookState;
}) {
  const { rules, loading, error, reload, configured } = rulesState;

  const header = (
    <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 8 }}>
      <h2 style={{ fontSize: 18, margin: 0 }}>Active Rules</h2>
      <button
        type="button"
        onClick={reload}
        disabled={!deckId || !configured || loading}
        style={{
          border: "none",
          background: "transparent",
          color: !deckId || !configured ? "#cbd5f5" : loading ? "#94a3b8" : "#2563eb",
          fontSize: 12,
          cursor: !deckId || !configured || loading ? "default" : "pointer",
          fontWeight: 600,
        }}
      >
        Refresh
      </button>
    </div>
  );

  if (!deckId) {
    return (
      <div style={{ display: "grid", gap: 8 }}>
        {header}
        <div style={{ fontSize: 13, color: "#6b7280" }}>Upload a deck to see saved rules.</div>
      </div>
    );
  }

  if (!configured) {
    return (
      <div style={{ display: "grid", gap: 8 }}>
        {header}
        <div style={{ fontSize: 13, color: "#b91c1c", lineHeight: 1.4 }}>
          Set `VITE_SUPABASE_URL` and `VITE_SUPABASE_ANON_KEY` to load rules.
        </div>
      </div>
    );
  }

  return (
    <div style={{ display: "grid", gap: 8 }}>
      {header}

      {error && <div style={{ fontSize: 13, color: "#b91c1c" }}>{error}</div>}

      {loading && rules.length === 0 && <RuleListSkeleton count={2} />}

      {!loading && !error && rules.length === 0 && (
        <div style={{ fontSize: 13, color: "#6b7280" }}>No rules yet. Generate instructions to add one.</div>
      )}

      {rules.length > 0 && (
        <div style={{ fontSize: 12, color: "#6b7280", marginTop: 2 }}>
          {`${rules.length} active rule${rules.length === 1 ? "" : "s"}`}
        </div>
      )}

      {rules.map((rule) => {
        const created = formatTimestamp(rule.created_at);
        return (
          <div key={rule.id} style={ruleCard}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", gap: 8 }}>
              <strong style={{ fontSize: 14 }}>{rule.title || "Untitled rule"}</strong>
              {created && <span style={{ fontSize: 11, color: "#94a3b8" }}>{created}</span>}
            </div>
            <div style={{ fontSize: 13, color: "#475569", marginTop: 4, whiteSpace: "pre-wrap" }}>{rule.user_query}</div>
          </div>
        );
      })}
    </div>
  );
}

function RuleListSkeleton({ count }: { count: number }) {
  return (
    <div style={{ display: "grid", gap: 10 }}>
      {Array.from({ length: count }).map((_, index) => (
        <div key={index} style={{ ...ruleCard, background: "#f8fafc", borderColor: "#e2e8f0" }}>
          <div style={{ height: 12, background: "#e2e8f0", borderRadius: 999, marginBottom: 8 }} />
          <div style={{ height: 10, background: "#e2e8f0", borderRadius: 999 }} />
        </div>
      ))}
    </div>
  );
}

const ruleCard: React.CSSProperties = {
  background: "#eef4ff",
  borderRadius: 10,
  padding: 10,
  border: "1px solid #d7e4ff",
};
