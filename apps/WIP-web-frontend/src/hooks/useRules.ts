import { useCallback, useEffect, useState } from "react";

import {
  SUPABASE_URL,
  SUPABASE_ANON_KEY,
  SUPABASE_RULES_TABLE,
  SUPABASE_RULE_ACTIONS_TABLE,
  SUPABASE_CONFIGURED,
} from "../config/env";
import type { RuleActionRecord, RuleRecord } from "../types/rules";

export type RulesHookState = {
  rules: RuleRecord[];
  loading: boolean;
  error: string | null;
  reload: () => void;
  configured: boolean;
};

export function useSupabaseRules(deckId: string | null): RulesHookState {
  const [rules, setRules] = useState<RuleRecord[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    if (!deckId) {
      setRules([]);
      setLoading(false);
      setError(null);
      return;
    }

    if (!SUPABASE_CONFIGURED) {
      setRules([]);
      setLoading(false);
      setError("Supabase configuration missing.");
      return;
    }

    const controller = new AbortController();
    setLoading(true);
    setError(null);

    (async () => {
      try {
        const params = new URLSearchParams({
          select: "id,deck_id,title,user_query,created_at,updated_at",
          order: "created_at.desc.nullslast",
          deck_id: `eq.${deckId}`,
        });

        const response = await fetch(`${SUPABASE_URL}/rest/v1/${SUPABASE_RULES_TABLE}?${params.toString()}`, {
          method: "GET",
          headers: {
            apikey: SUPABASE_ANON_KEY,
            Authorization: `Bearer ${SUPABASE_ANON_KEY}`,
            Accept: "application/json",
          },
          signal: controller.signal,
        });

        if (!response.ok) {
          const body = await response.text();
          throw new Error(body || `Failed to load rules (${response.status})`);
        }

        const rows = (await response.json()) as RuleRecord[];
        if (!controller.signal.aborted) {
          setRules(Array.isArray(rows) ? rows : []);
        }
      } catch (err) {
        if (!controller.signal.aborted) {
          setRules([]);
          setError(err instanceof Error ? err.message : "Failed to load rules");
        }
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    })();

    return () => controller.abort();
  }, [deckId, revision]);

  const reload = useCallback(() => {
    setRevision((value) => value + 1);
  }, []);

  return { rules, loading, error, reload, configured: SUPABASE_CONFIGURED };
}

export function useSupabaseRuleActions(deckId: string | null, enabled: boolean) {
  const [actions, setActions] = useState<RuleActionRecord[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    if (!enabled) {
      setLoading(false);
      setError(null);
      return;
    }

    if (!deckId) {
      setActions([]);
      setLoading(false);
      setError(null);
      return;
    }

    if (!SUPABASE_CONFIGURED) {
      setActions([]);
      setLoading(false);
      setError("Supabase configuration missing.");
      return;
    }

    const controller = new AbortController();
    setLoading(true);
    setError(null);

    (async () => {
      try {
        const params = new URLSearchParams({
          select: "id,rule_id,deck_id,slide_no,element_key,bbox,original_text,new_text,created_at",
          order: "created_at.desc.nullslast",
          deck_id: `eq.${deckId}`,
        });

        const response = await fetch(
          `${SUPABASE_URL}/rest/v1/${SUPABASE_RULE_ACTIONS_TABLE}?${params.toString()}`,
          {
            method: "GET",
            headers: {
              apikey: SUPABASE_ANON_KEY,
              Authorization: `Bearer ${SUPABASE_ANON_KEY}`,
              Accept: "application/json",
            },
            signal: controller.signal,
          },
        );

        if (!response.ok) {
          const body = await response.text();
          throw new Error(body || `Failed to load rule actions (${response.status})`);
        }

        const rows = (await response.json()) as RuleActionRecord[];
        if (!controller.signal.aborted) {
          const normalized = Array.isArray(rows)
            ? rows
                .map((row) => ({
                  ...row,
                  slide_no: Number((row as any).slide_no),
                  bbox: row.bbox ?? null,
                  original_text: row.original_text ?? null,
                  new_text: row.new_text ?? null,
                }))
                .filter((row) => Number.isFinite(row.slide_no))
            : [];
          setActions(normalized);
        }
      } catch (err) {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Failed to load rule actions");
        }
      } finally {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      }
    })();

    return () => controller.abort();
  }, [deckId, enabled, revision]);

  const reload = useCallback(() => {
    setRevision((value) => value + 1);
  }, []);

  return { actions, loading, error, reload };
}

export function formatTimestamp(iso?: string) {
  if (!iso) {
    return null;
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return null;
  }
  return date.toLocaleString();
}
