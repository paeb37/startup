import { useCallback, useEffect, useState } from "react";

import {
  FALLBACK_DECKS,
  enrichDeckForLibrary,
  normalizeDeckRecord,
} from "../services/decks";
import type { LibraryDeck } from "../services/decks";
import type { DeckPreview } from "../types/deck";

export function useLibraryDecks(limit = 60) {
  const [decks, setDecks] = useState<LibraryDeck[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      setLoading(true);
      setError(null);
      try {
        const res = await fetch(`/api/decks?limit=${limit}`);
        if (!res.ok) {
          throw new Error(`deck request failed: ${res.status}`);
        }
        const payload = await res.json();
        const list = Array.isArray(payload)
          ? payload
          : Array.isArray(payload?.decks)
            ? payload.decks
            : [];
        const normalized = list
          .map((item: any) => normalizeDeckRecord(item))
          .filter(Boolean) as DeckPreview[];
        const enriched = normalized.map(enrichDeckForLibrary);
        if (!cancelled) {
          setDecks(enriched);
        }
      } catch (err) {
        console.error("Failed to load library decks", err);
        if (!cancelled) {
          const enrichedFallback = FALLBACK_DECKS.map(enrichDeckForLibrary);
          setDecks(enrichedFallback);
          setError(err instanceof Error ? err.message : "Failed to load decks");
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    load();
    return () => {
      cancelled = true;
    };
  }, [limit, revision]);

  const reload = useCallback(() => {
    setRevision((value) => value + 1);
  }, []);

  return { decks, loading, error, reload };
}
