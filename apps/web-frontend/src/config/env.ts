const env = import.meta.env as Record<string, string | undefined>;

const read = (value: string | undefined, fallback = "") => (value ?? fallback).trim();

export const STORAGE_PUBLIC_BASE = read(env.VITE_STORAGE_PUBLIC_BASE);
export const SLIDE_IMAGE_BASE = read(env.VITE_DECK_API_BASE);
export const SUPABASE_URL = read(env.VITE_SUPABASE_URL);
export const SUPABASE_ANON_KEY = read(env.VITE_SUPABASE_ANON_KEY);
export const SUPABASE_RULES_TABLE = read(env.VITE_SUPABASE_RULES_TABLE, "rules");
export const SUPABASE_RULE_ACTIONS_TABLE = read(env.VITE_SUPABASE_RULE_ACTIONS_TABLE, "rule_actions");

const semanticUrlRaw = read(env.VITE_SEMANTIC_URL, "http://localhost:8000");
export const SEMANTIC_URL = (semanticUrlRaw || "http://localhost:8000").replace(/\/$/, "");

export const SUPABASE_CONFIGURED = Boolean(
  SUPABASE_URL &&
  SUPABASE_ANON_KEY &&
  SUPABASE_RULES_TABLE &&
  SUPABASE_RULE_ACTIONS_TABLE,
);

export const EMU_PER_POINT = 12700; // 1 point = 1/72", 1 inch = 914400 EMU
