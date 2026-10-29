export type SegmentInput = {
  id: string;
  text: string;
  duration_seconds?: number | null;
  frame_image_data_url?: string | null;
  concepts?: string[] | null;
  speaker_id?: string | null;
  speaker_label?: string | null;
  frame_id?: string | null;
  timecode_seconds?: number | null;
  frame_sha256?: string | null;
  vision_enrichment?: boolean | null;
};

export type StatusResponse = {
  hasKey: boolean;
  model: string;
  baseUrl: string;
  reasoningEffort: string;
};

export type TranslationProvenance = {
  model_id: string;
  endpoint_host: string;
  timestamp_utc: string;
  prompt_template_version: string;
};

export type TranslationUsage = {
  prompt_tokens: number;
  completion_tokens: number;
  total_tokens: number;
};

export type TranslatedSegment = {
  id: string;
  text: string;
  translation: string;
  provenance: TranslationProvenance;
  usage: TranslationUsage;
};

export type TranslateBatch = {
  source_language: string;
  target_language: string;
  segments: TranslatedSegment[];
  usage_total: TranslationUsage;
};

export type QaAttempt = {
  attempt_number: number;
  kind: string; // Initial | Revision
  translation: string;
  estimated_seconds: number;
  window_seconds: number;
  max_chars: number;
  verdict: string; // Pass | Fail (combined)
  prompt_tokens: number;
  completion_tokens: number;
  total_tokens: number;
  model_id: string;
  timing_verdict: string; // Pass | Fail
  meaning_verdict: string; // Pass | Fail | Error
  meaning_issues: string[] | null;
  meaning_explanation: string | null;
  meaning_model_id: string | null;
  meaning_prompt_template_version: string | null;
  meaning_timestamp_utc: string | null;
  meaning_prompt_tokens: number;
  meaning_completion_tokens: number;
  meaning_total_tokens: number;
  meaning_endpoint_host: string | null;
};

export type QaSegmentReport = {
  id: string;
  text: string;
  window_seconds: number;
  first_attempt: QaAttempt;
  revisions: QaAttempt[];
  final_verdict: string;
  final_translation: string;
  token_usage: TranslationUsage;
};

export type QaTotals = {
  total_segments: number;
  first_pass_count: number;
  final_pass_count: number;
  total_revisions: number;
  first_pass_rate: number;
  final_pass_rate: number;
  total_tokens: number;
  prompt_tokens: number;
  completion_tokens: number;
};

export type QaReport = {
  source_language: string;
  target_language: string;
  qa_options: {
    max_attempts: number;
    tolerance_fraction: number;
    target_cps: number;
    source_cps: number;
    reasoning_effort: string | null;
  };
  segments: QaSegmentReport[];
  totals: QaTotals;
};

// Null context fields are omitted by the server (WhenWritingNull), so every
// field below is optional: absence means unknown, never a default.
export type SceneRegion = {
  concept: string;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  frame_width: number;
  frame_height: number;
  mask_x?: number;
  mask_y?: number;
  mask_data?: string;
};

export type DirectorContext = {
  speaker_id?: string;
  speaker_label?: string;
  emotion_tone_label?: string;
  on_screen_text?: string[];
  scene_summary?: string;
  regions?: SceneRegion[];
  speaker_description?: string;
  burned_in_text?: string[];
};

export type VisionProvenance = {
  requested_model_id: string;
  model_id: string;
  endpoint_host: string;
  timestamp_utc: string;
  prompt_template_version: string;
};

export type VisionUsage = {
  prompt_tokens: number;
  completion_tokens: number;
  total_tokens: number;
  reasoning_tokens?: number;
  cached_tokens?: number;
};

export type SceneSegmentResult = {
  id: string;
  status: string; // Parsed | Empty | Failed
  context?: DirectorContext;
  error?: string;
  provenance: TranslationProvenance;
  usage: TranslationUsage;
  images_processed: number;
  raw_content?: string;
  frame_id?: string;
  timecode_seconds?: number;
  frame_sha256?: string;
  vision_provenance?: VisionProvenance;
  vision_usage?: VisionUsage;
  vision_raw_content?: string;
};

export type SceneContextBatch = {
  segments: SceneSegmentResult[];
  usage_total: TranslationUsage;
  images_processed_total: number;
  vision_usage_total?: VisionUsage;
};

export async function postSceneContext(segments: SegmentInput[]): Promise<SceneContextBatch> {
  const sceneSegments = segments.map((s) => ({
    id: s.id,
    text: s.text,
    frame_image_data_url: s.frame_image_data_url ?? null,
    concepts: s.concepts ?? null,
    speaker_id: s.speaker_id ?? null,
    speaker_label: s.speaker_label ?? null,
    frame_id: s.frame_id ?? null,
    timecode_seconds: s.timecode_seconds ?? null,
    frame_sha256: s.frame_sha256 ?? null,
    vision_enrichment: s.vision_enrichment ?? null,
  }));
  const r = await fetch("/api/scene-context", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ segments: sceneSegments }),
  });
  if (!r.ok) {
    const bodyText = await r.text().catch(() => "");
    throw new Error(`POST /api/scene-context ${r.status}: ${bodyText.slice(0, 600)}`);
  }
  return (await r.json()) as SceneContextBatch;
}

export async function fetchStatus(): Promise<StatusResponse> {
  const r = await fetch("/api/status");
  if (!r.ok) throw new Error(`GET /api/status ${r.status}`);
  return (await r.json()) as StatusResponse;
}

export async function fetchModels(): Promise<{ id: string }[]> {
  const r = await fetch("/api/models");
  if (!r.ok) {
    const body = await r.text().catch(() => "");
    throw new Error(`GET /api/models ${r.status}: ${body.slice(0, 300)}`);
  }
  return (await r.json()) as { id: string }[];
}

export async function postTranslate(
  segments: SegmentInput[],
  from = "en",
  to = "es",
): Promise<TranslateBatch> {
  const r = await fetch("/api/translate", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ segments, sourceLanguage: from, targetLanguage: to }),
  });
  if (!r.ok) {
    const body = await r.text().catch(() => "");
    throw new Error(`POST /api/translate ${r.status}: ${body.slice(0, 600)}`);
  }
  return (await r.json()) as TranslateBatch;
}

export async function postQa(
  segments: SegmentInput[],
  from = "en",
  to = "es",
  options?: { maxAttempts?: number; toleranceFraction?: number; targetCps?: number },
): Promise<QaReport> {
  const body: Record<string, unknown> = { segments, sourceLanguage: from, targetLanguage: to };
  if (options) {
    const opts: Record<string, unknown> = {};
    if (options.maxAttempts != null) opts.maxAttempts = options.maxAttempts;
    if (options.toleranceFraction != null) opts.toleranceFraction = options.toleranceFraction;
    if (options.targetCps != null) opts.targetCharactersPerSecond = options.targetCps;
    if (Object.keys(opts).length > 0) body.options = opts;
  }
  const r = await fetch("/api/qa", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!r.ok) {
    const bodyText = await r.text().catch(() => "");
    throw new Error(`POST /api/qa ${r.status}: ${bodyText.slice(0, 600)}`);
  }
  return (await r.json()) as QaReport;
}
