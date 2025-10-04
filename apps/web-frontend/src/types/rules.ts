import type { BBox } from "./deck";

export type RuleRecord = {
  id: string;
  deck_id: string;
  title: string;
  user_query: string;
  created_at?: string;
  updated_at?: string;
};

export type RuleActionRecord = {
  id: string;
  rule_id: string;
  deck_id: string;
  slide_no: number;
  element_key: string;
  bbox?: BBox | null;
  original_text?: string | null;
  new_text?: string | null;
  created_at?: string;
};

export type RedactResponsePayload = {
  success?: boolean;
  actionsApplied?: number;
  path?: string;
  fileName?: string;
  downloadUrl?: string;
  generated?: boolean;
};
