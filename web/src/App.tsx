import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  fetchStatus,
  postAudition,
  postDirectorAdaptation,
  postQa,
  postSceneContext,
  postTranslate,
  type DirectorAdaptationBatch,
  type DirectorAdaptationInput,
  type DirectorAdaptationSegmentResult,
  type DirectorMeaningResult,
  type TranslationProvenance,
  type VisionProvenance,
  type VisionUsage,
  type AuditionReport,
  type AuditionSegmentReport,
  type AuditionTake,
  type DirectorContext,
  type QaAttempt,
  type QaReport,
  type QaSegmentReport,
  type QaTotals,
  type SceneContextBatch,
  type SceneRegion,
  type SceneSegmentResult,
  type SegmentInput,
  type StatusResponse,
  type TranslateBatch,
} from "./api";
import sampleSegments from "./sampleSegments.json";

const DEFAULT_FROM = "en";
const DEFAULT_TO = "es";

type BusyState = "translate" | "qa" | "scene" | "director" | "audition" | null;

function pct(n: number): string {
  return `${Math.round(n * 100)}%`;
}

function fmtSec(n: number): string {
  return `${n.toFixed(2)}s`;
}

function verdictClass(verdict: string): string {
  return `verdict verdict-${verdict.toLowerCase()}`;
}

function errorMessage(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

function orDash(value: string | null | undefined): string {
  return value ?? "—";
}

function listOrDash(values: string[] | null | undefined): string {
  return values?.length ? values.join(" | ") : "—";
}

function parseSegments(text: string): SegmentInput[] {
  const parsed = JSON.parse(text) as unknown;
  if (!Array.isArray(parsed)) {
    throw new Error("JSON must be an array of { id, text, duration_seconds? }");
  }

  const segments = parsed as SegmentInput[];
  segments.forEach((segment, index) => {
    if (!segment?.id || typeof segment.text !== "string") {
      throw new Error(`Segment at index ${index} must have { id, text }`);
    }
  });
  return segments;
}

interface LineCardProps {
  director: DirectorAdaptationSegmentResult | undefined;
  audition: AuditionSegmentReport | undefined;
  seg: SegmentInput;
  q: QaSegmentReport | undefined;
  translated: string | null;
  scene: SceneSegmentResult | undefined;
  isExpanded: boolean;
  onToggle: () => void;
}

interface LineView {
  displayTranslation: string;
  windowSec: number | null;
  verdict: string | null;
  firstVerdict: string | null;
  shownAttempt: QaAttempt | null;
  estimated: number | null;
  attemptCount: number;
}

function deriveShownAttempt(q: QaSegmentReport | undefined): QaAttempt | null {
  if (!q) {
    return null;
  }

  return q.revisions.length > 0 ? q.revisions[q.revisions.length - 1] : q.first_attempt;
}

function deriveAttemptCount(q: QaSegmentReport | undefined, translated: string | null): number {
  if (q) {
    return 1 + q.revisions.length;
  }

  return translated ? 1 : 0;
}

function deriveLineView(seg: SegmentInput, q: QaSegmentReport | undefined, translated: string | null): LineView {
  // Gate pills and the estimate must describe the translation shown (the final attempt),
  // not the initial one; otherwise a successful bounce-back displays failing gates.
  const shownAttempt = deriveShownAttempt(q);
  return {
    displayTranslation: q?.final_translation ?? translated ?? "—",
    windowSec: q?.window_seconds ?? seg.duration_seconds ?? null,
    verdict: q?.final_verdict ?? null,
    firstVerdict: q?.first_attempt.verdict ?? null,
    shownAttempt,
    estimated: shownAttempt?.estimated_seconds ?? null,
    attemptCount: deriveAttemptCount(q, translated),
  };
}

function TimingPill({ windowSec, estimated }: { windowSec: number | null; estimated: number | null }) {
  return (
    <span className="meta-pill" title="Timing window vs estimated duration">
      {windowSec != null ? `window ${fmtSec(windowSec)}` : "window —"}
      {estimated != null ? ` · est ${fmtSec(estimated)}` : ""}
    </span>
  );
}

function MainMeaningPill({ attempt }: { attempt: QaAttempt }) {
  if (attempt.meaning_verdict === "NotEvaluated") {
    return (
      <span className="verdict verdict-notevaluated" title={`Not checked (timing failed, attempt #${attempt.attempt_number})`}>
        Meaning: Not checked
      </span>
    );
  }

  return (
    <span className={verdictClass(attempt.meaning_verdict)} title={`Meaning gate (attempt #${attempt.attempt_number})`}>
      Meaning: {attempt.meaning_verdict}
    </span>
  );
}

function GatePills({ attempt, finalVerdict }: { attempt: QaAttempt; finalVerdict: string | null }) {
  return (
    <>
      <span className={verdictClass(attempt.timing_verdict)} title={`Timing gate (attempt #${attempt.attempt_number})`}>
        Timing: {attempt.timing_verdict}
      </span>
      <MainMeaningPill attempt={attempt} />
      <span className={verdictClass(finalVerdict ?? "idle")} title="Combined verdict">
        Final: {finalVerdict}
      </span>
    </>
  );
}

function StatusPills({ q, shownAttempt, verdict, firstVerdict, translated }: {
  q: QaSegmentReport | undefined;
  shownAttempt: QaAttempt | null;
  verdict: string | null;
  firstVerdict: string | null;
  translated: string | null;
}) {
  if (q && shownAttempt) {
    return <GatePills attempt={shownAttempt} finalVerdict={verdict} />;
  }

  if (verdict) {
    return <span className={verdictClass(verdict)}>{verdict}</span>;
  }

  if (firstVerdict) {
    return <span className={verdictClass(firstVerdict)}>{firstVerdict}</span>;
  }

  if (translated) {
    return <span className="verdict verdict-pass">Translated</span>;
  }

  return <span className="verdict verdict-idle">Idle</span>;
}

function SceneStatusPill({ scene }: { scene: SceneSegmentResult }) {
  const statusClass = scene.status === "Parsed" ? "verdict-pass" : scene.status === "Failed" ? "verdict-fail" : "verdict-idle";
  return (
    <span
      className={`verdict ${statusClass}`}
      title={`Scene context (${scene.provenance.model_id}, ${scene.provenance.prompt_template_version})`}
    >
      Scene: {scene.status}
    </span>
  );
}

function RowMeaningPill({ attempt }: { attempt: QaAttempt }) {
  if (attempt.meaning_verdict === "NotEvaluated") {
    return (
      <span className="verdict verdict-notevaluated" title="Not checked (timing failed)">
        Not checked
      </span>
    );
  }

  return (
    <span className={verdictClass(attempt.meaning_verdict)} title={attempt.meaning_explanation ?? ""}>
      {attempt.meaning_verdict}
    </span>
  );
}

function AttemptRow({ attempt }: { attempt: QaAttempt }) {
  return (
    <div className="attempt-row">
      <span>#{attempt.attempt_number}</span>
      <span>{attempt.kind}</span>
      <span className="attempt-text">{attempt.translation}</span>
      <span>
        {fmtSec(attempt.estimated_seconds)} / {fmtSec(attempt.window_seconds)} (max {attempt.max_chars}c)
      </span>
      <span className={verdictClass(attempt.timing_verdict)}>{attempt.timing_verdict}</span>
      <RowMeaningPill attempt={attempt} />
      <span className={verdictClass(attempt.verdict)}>{attempt.verdict}</span>
      <span>
        {attempt.total_tokens}
        {attempt.meaning_total_tokens > 0 ? ` +${attempt.meaning_total_tokens}m` : ""}
      </span>
    </div>
  );
}

function MeaningIssueRow({ attempt }: { attempt: QaAttempt }) {
  const hasIssues = attempt.meaning_issues != null && attempt.meaning_issues.length > 0;
  if (!hasIssues && !attempt.meaning_explanation) {
    return null;
  }

  return (
    <div className="meaning-issues">
      <strong>#{attempt.attempt_number} {hasIssues ? "meaning issues:" : "meaning:"}</strong>{" "}
      {hasIssues ? attempt.meaning_issues!.join("; ") : attempt.meaning_explanation}
      {hasIssues && attempt.meaning_explanation ? ` — ${attempt.meaning_explanation}` : ""}
    </div>
  );
}

function AttemptsFooter({ q, shownAttempt }: { q: QaSegmentReport; shownAttempt: QaAttempt | null }) {
  return (
    <>
      <div className="token-usage">
        Total tokens for line: {q.token_usage.total_tokens} (prompt {q.token_usage.prompt_tokens} + completion {q.token_usage.completion_tokens})
      </div>
      <div className="token-usage">
        Meaning gate (attempt #{shownAttempt?.attempt_number}): model {shownAttempt?.meaning_model_id ?? "—"} · template {shownAttempt?.meaning_prompt_template_version ?? "—"} ·{" "}
        {shownAttempt?.meaning_timestamp_utc ?? "—"}
      </div>
    </>
  );
}

function AttemptsPanel({ q, shownAttempt }: { q: QaSegmentReport; shownAttempt: QaAttempt | null }) {
  const attempts = [q.first_attempt, ...q.revisions];
  return (
    <div className="attempts-panel">
      <div className="attempt-row attempt-row-header">
        <span>#</span>
        <span>Kind</span>
        <span>Translation</span>
        <span>Est / window</span>
        <span>Timing</span>
        <span>Meaning</span>
        <span>Verdict</span>
        <span>Tokens</span>
      </div>
      {attempts.map((a) => (
        <AttemptRow key={`${q.id}-${a.attempt_number}`} attempt={a} />
      ))}
      {attempts.map((a) => (
        <MeaningIssueRow key={`${q.id}-${a.attempt_number}-issue`} attempt={a} />
      ))}
      <AttemptsFooter q={q} shownAttempt={shownAttempt} />
    </div>
  );
}

function RegionRow({ region, index }: { region: SceneRegion; index: number }) {
  return (
    <div key={`region-${index}`} className="token-usage">
      Region: {region.concept} ({region.x1},{region.y1})-({region.x2},{region.y2}) in {region.frame_width}x{region.frame_height}
      {region.mask_data ? " · mask present" : ""}
    </div>
  );
}

function SceneHeader({ context }: { context: DirectorContext | undefined }) {
  const fields: Partial<DirectorContext> = context ?? {};
  return (
    <>
      <div className="token-usage">
        Speaker: {orDash(fields.speaker_label ?? fields.speaker_id)} · Emotion/tone: {orDash(fields.emotion_tone_label)}
      </div>
      <div className="token-usage">Speaker description: {orDash(fields.speaker_description)}</div>
      <div className="token-usage">On-screen text: {listOrDash(fields.on_screen_text)}</div>
      <div className="token-usage">Burned-in text: {listOrDash(fields.burned_in_text)}</div>
      <div className="token-usage">Scene summary: {orDash(fields.scene_summary)}</div>
    </>
  );
}

function SceneRegions({ context }: { context: DirectorContext | undefined }) {
  const regions = context?.regions;
  if (!regions) {
    return null;
  }

  if (regions.length === 0) {
    return <div className="token-usage">Regions: none found</div>;
  }

  return (
    <>
      {regions.map((r, i) => (
        <RegionRow key={`region-${i}`} region={r} index={i} />
      ))}
    </>
  );
}

function SceneDetails({ scene }: { scene: SceneSegmentResult }) {
  return (
    <>
      <SceneHeader context={scene.context} />
      <SceneRegions context={scene.context} />
    </>
  );
}

function hasFrameInfo(scene: SceneSegmentResult): boolean {
  return Boolean(scene.frame_id || scene.timecode_seconds != null || scene.frame_sha256);
}

function VisionGateLine({ provenance, usage }: { provenance: VisionProvenance; usage: VisionUsage | undefined }) {
  const reasoning = usage?.reasoning_tokens;
  return (
    <div className="token-usage">
      Vision gate: requested {provenance.requested_model_id} · returned {provenance.model_id} · template {provenance.prompt_template_version} · {provenance.timestamp_utc} · tokens{" "}
      {usage?.total_tokens ?? "—"}
      {reasoning != null ? ` (reasoning ${reasoning})` : ""}
    </div>
  );
}

function FrameLine({ scene }: { scene: SceneSegmentResult }) {
  return (
    <div className="token-usage" title={scene.frame_sha256 ?? ""}>
      Frame: {orDash(scene.frame_id)} · {scene.timecode_seconds != null ? `${scene.timecode_seconds}s` : "—"} · sha256{" "}
      {scene.frame_sha256 ? `${scene.frame_sha256.slice(0, 12)}…` : "—"}
    </div>
  );
}

function SceneVisionFooter({ scene }: { scene: SceneSegmentResult }) {
  const provenance = scene.vision_provenance;
  const hasFrame = hasFrameInfo(scene);
  if (!provenance && !hasFrame) {
    return null;
  }

  return (
    <>
      {provenance && <VisionGateLine provenance={provenance} usage={scene.vision_usage} />}
      {hasFrame && <FrameLine scene={scene} />}
    </>
  );
}

function ScenePanel({ seg, scene }: { seg: SegmentInput; scene: SceneSegmentResult }) {
  return (
    <div className="attempts-panel" aria-label={`Scene context for ${seg.id}`}>
      {scene.status === "Failed" ? (
        <div className="meaning-issues">
          <strong>Scene failed:</strong> {scene.error ?? "unknown error"}
        </div>
      ) : (
        <SceneDetails scene={scene} />
      )}
      <div className="token-usage">
        Scene gate: model {scene.provenance.model_id} · template {scene.provenance.prompt_template_version} · {scene.provenance.timestamp_utc} · tokens{" "}
        {scene.usage.total_tokens} · images {scene.images_processed}
      </div>
      <SceneVisionFooter scene={scene} />
    </div>
  );
}

function DirectorStatusPill({ director }: { director: DirectorAdaptationSegmentResult }) {
  const statusClass = director.status === "Passed" ? "verdict-pass" : "verdict-fail";
  return (
    <span className={`verdict ${statusClass}`} title="Director adaptation (Passed only when timing passes and meaning is Pass)">
      Director: {director.status}
    </span>
  );
}

function DirectorMeaningPill({ director }: { director: DirectorAdaptationSegmentResult }) {
  const meaning = director.meaning;
  if (!meaning) {
    return null;
  }

  if (meaning.verdict === "NotEvaluated") {
    return (
      <span className="verdict verdict-notevaluated" title="Not checked (timing failed)">
        Meaning: Not checked
      </span>
    );
  }

  return (
    <span className={verdictClass(meaning.verdict)} title={meaning.explanation}>
      Meaning: {meaning.verdict}
    </span>
  );
}

function DirectorGatePills({ director }: { director: DirectorAdaptationSegmentResult }) {
  return (
    <div className="line-meta">
      {director.timing_verdict && (
        <span className={verdictClass(director.timing_verdict)} title="Timing gate (adapted line)">
          Timing: {director.timing_verdict}
        </span>
      )}
      <DirectorMeaningPill director={director} />
      <DirectorStatusPill director={director} />
      {director.estimated_seconds != null && director.window_seconds != null && (
        <span className="meta-pill" title="Adapted estimate vs window">
          est {fmtSec(director.estimated_seconds)} / window {fmtSec(director.window_seconds)}
        </span>
      )}
    </div>
  );
}

function DirectorSideBySide({ director }: { director: DirectorAdaptationSegmentResult }) {
  return (
    <div className="director-grid">
      <div>
        <div className="director-label">Original translation</div>
        <div className="line-translation">{director.original_translation ?? "—"}</div>
      </div>
      <div>
        <div className="director-label">Adapted · {director.target_register ?? "—"}</div>
        <div className="line-translation">{director.adapted_translation ?? "—"}</div>
      </div>
    </div>
  );
}

function isQuietMeaning(meaning: DirectorMeaningResult): boolean {
  return meaning.issues.length === 0 && meaning.verdict === "Pass";
}

function DirectorMeaningIssues({ director }: { director: DirectorAdaptationSegmentResult }) {
  const meaning = director.meaning;
  if (!meaning || isQuietMeaning(meaning)) {
    return null;
  }

  const hasIssues = meaning.issues.length > 0;
  return (
    <div className="meaning-issues">
      <strong>meaning{hasIssues ? " issues:" : ":"}</strong>{" "}
      {hasIssues ? meaning.issues.join("; ") : meaning.explanation}
      {hasIssues && meaning.explanation ? ` — ${meaning.explanation}` : ""}
    </div>
  );
}

function DirectorErrorLine({ error }: { error: string }) {
  return (
    <div className="meaning-issues">
      <strong>Adaptation failed:</strong> {error}
    </div>
  );
}

function DirectorChangeLine({ changed }: { changed: boolean }) {
  return <div className="token-usage">Adaptation: {changed ? "changed" : "no change"}</div>;
}

function ContextFieldsLine({ fields }: { fields: string[] }) {
  return <div className="token-usage">Context fields used: {fields.length ? fields.join(", ") : "none"}</div>;
}

function DirectorGateLine({ director, provenance }: { director: DirectorAdaptationSegmentResult; provenance: TranslationProvenance }) {
  const reasoning = director.usage?.reasoning_tokens;
  return (
    <div className="token-usage">
      Director gate: model {provenance.model_id} · template {provenance.prompt_template_version} · {provenance.timestamp_utc} · tokens{" "}
      {director.usage?.total_tokens ?? "—"}
      {reasoning != null ? ` (reasoning ${reasoning})` : ""}
      {director.meaning ? ` · meaning tokens ${director.meaning.usage.total_tokens}` : ""}
    </div>
  );
}

function DirectorPanel({ seg, director }: { seg: SegmentInput; director: DirectorAdaptationSegmentResult }) {
  return (
    <div className="attempts-panel" aria-label={`Director adaptation for ${seg.id}`}>
      <DirectorSideBySide director={director} />
      <DirectorGatePills director={director} />
      {director.error && <DirectorErrorLine error={director.error} />}
      <DirectorMeaningIssues director={director} />
      {director.changed != null && <DirectorChangeLine changed={director.changed} />}
      <ContextFieldsLine fields={director.context_fields_used} />
      {director.provenance && <DirectorGateLine director={director} provenance={director.provenance} />}
    </div>
  );
}

function AuditionTakeRow({ take }: { take: AuditionTake }) {
  return (
    <div className="attempt-row">
      <span>#{take.take_number}</span>
      <span>{take.kind}</span>
      <span className="attempt-text">{take.text}</span>
      <span>
        {take.measured_seconds != null ? `${fmtSec(take.measured_seconds)} / ${fmtSec(take.window_seconds)}` : `— / ${fmtSec(take.window_seconds)}`}
      </span>
      <span className={take.timing_verdict === "Pass" ? verdictClass("Pass") : take.timing_verdict === "Fail" ? verdictClass("Fail") : "verdict verdict-idle"}>
        {take.timing_verdict}
      </span>
      <span className={verdictClass(take.meaning_verdict)} title={take.meaning_explanation ?? ""}>
        {take.meaning_verdict}
      </span>
      <span>{take.is_winner ? "Winner" : ""}</span>
      <span>{take.elevenlabs_characters > 0 ? `${take.elevenlabs_characters}c` : "—"}</span>
    </div>
  );
}

function AuditionPanel({ seg, audition }: { seg: SegmentInput; audition: AuditionSegmentReport }) {
  return (
    <div className="attempts-panel" aria-label={`Audition for ${seg.id}`}>
      <div className="token-usage">
        Audition: {audition.cast ? `Cast — take #${audition.winner_take_number} (${audition.winner_file_name ?? "—"})` : "Uncast"} · Speaker:{" "}
        {audition.speaker_id ?? "—"} · Voice: {audition.cast_voice ? `${audition.cast_voice.voice_name} (${audition.cast_voice.voice_id})` : "—"}
      </div>
      <div className="attempt-row attempt-row-header">
        <span>#</span>
        <span>Kind</span>
        <span>Take</span>
        <span>Measured / window</span>
        <span>Timing</span>
        <span>Meaning</span>
        <span>Winner</span>
        <span>EL chars</span>
      </div>
      {audition.takes.map((t) => (
        <AuditionTakeRow key={`${audition.id}-${t.take_number}`} take={t} />
      ))}
      {audition.takes
        .filter((t) => t.synthesis_note)
        .map((t) => (
          <div key={`${audition.id}-${t.take_number}-note`} className="token-usage">
            #{t.take_number}: {t.synthesis_note}
          </div>
        ))}
    </div>
  );
}

function LineMeta({ q, scene, view, translated }: {
  q: QaSegmentReport | undefined;
  scene: SceneSegmentResult | undefined;
  view: LineView;
  translated: string | null;
}) {
  return (
    <div className="line-meta">
      <TimingPill windowSec={view.windowSec} estimated={view.estimated} />
      <StatusPills q={q} shownAttempt={view.shownAttempt} verdict={view.verdict} firstVerdict={view.firstVerdict} translated={translated} />
      <span className="attempts">Attempts: {view.attemptCount || "—"}</span>
      {scene && <SceneStatusPill scene={scene} />}
      {q && q.revisions.length > 0 && <span className="revisions-count">Revisions: {q.revisions.length}</span>}
    </div>
  );
}

function AttemptToggle({ isExpanded, onToggle }: { isExpanded: boolean; onToggle: () => void }) {
  return (
    <button type="button" className="expand-btn" onClick={onToggle} aria-expanded={isExpanded}>
      {isExpanded ? "Hide attempts" : "Show attempts"}
    </button>
  );
}

function LineCard({ seg, q, translated, scene, director, audition, isExpanded, onToggle }: LineCardProps) {
  const view = deriveLineView(seg, q, translated);

  return (
    <article key={seg.id} className="line-card" role="listitem">
      <div className="line-main">
        <div className="line-id">{seg.id}</div>
        <div className="line-source">{seg.text}</div>
        <div className="line-translation" title={view.displayTranslation}>
          {view.displayTranslation}
        </div>
        <LineMeta q={q} scene={scene} view={view} translated={translated} />
      </div>
      {q && <AttemptToggle isExpanded={isExpanded} onToggle={onToggle} />}
      {q && isExpanded && <AttemptsPanel q={q} shownAttempt={view.shownAttempt} />}
      {scene && <ScenePanel seg={seg} scene={scene} />}
      {director && director.status !== "NotRequested" && <DirectorPanel seg={seg} director={director} />}
      {audition && <AuditionPanel seg={seg} audition={audition} />}
    </article>
  );
}

function Brand() {
  return (
    <div className="brand">
      <h1 className="brand-title">Trackdub Director</h1>
      <p className="brand-sub">Meta AI Developer Hackathon — Director Lane</p>
    </div>
  );
}

function HeaderStatus({ status }: { status: StatusResponse | null }) {
  return (
    <div className="header-meta">
      <div className="pill" title={status?.baseUrl ?? ""}>
        <span className="pill-label">Model</span>
        <span className="pill-value">{status?.model ?? "…"}</span>
      </div>
      <div className={`pill ${status?.hasKey ? "pill-ok" : "pill-warn"}`}>
        <span className="pill-label">Key</span>
        <span className="pill-value">{status ? (status.hasKey ? "configured" : "not set") : "…"}</span>
      </div>
    </div>
  );
}

function QaStats({ totals }: { totals: QaTotals }) {
  return (
    <>
      <div className="qa-stat">
        <span className="qa-stat-label">First-pass</span>
        <span className="qa-stat-value">{pct(totals.first_pass_rate)}</span>
        <span className="qa-stat-detail">
          {totals.first_pass_count}/{totals.total_segments}
        </span>
      </div>
      <div className="qa-stat">
        <span className="qa-stat-label">Final</span>
        <span className="qa-stat-value qa-stat-final">{pct(totals.final_pass_rate)}</span>
        <span className="qa-stat-detail">
          {totals.final_pass_count}/{totals.total_segments}
        </span>
      </div>
      <div className="qa-stat">
        <span className="qa-stat-label">Revisions</span>
        <span className="qa-stat-value">{totals.total_revisions}</span>
      </div>
      <div className="qa-stat">
        <span className="qa-stat-label">Tokens</span>
        <span className="qa-stat-value">{totals.total_tokens.toLocaleString()}</span>
      </div>
    </>
  );
}

function QaSummary({ qa }: { qa: QaReport | null }) {
  return (
    <div className="qa-summary" aria-label="QA summary">
      {qa ? <QaStats totals={qa.totals} /> : <span className="qa-empty">Run QA to see pass rates</span>}
    </div>
  );
}

function KeyWarning() {
  return (
    <div className="banner banner-warn">
      No Meta API key detected. Set <code>META_API_KEY</code> and restart the server to enable Translate and QA. The UI still loads without a key.
    </div>
  );
}

function AppHeader({ status, statusError, error, qa }: {
  status: StatusResponse | null;
  statusError: string | null;
  error: string | null;
  qa: QaReport | null;
}) {
  return (
    <header className="app-header">
      <div className="app-header-inner">
        <Brand />
        <HeaderStatus status={status} />
        <QaSummary qa={qa} />
      </div>
      {statusError && <div className="banner banner-error">{statusError}</div>}
      {error && <div className="banner banner-error">{error}</div>}
      {!status?.hasKey && !statusError && <KeyWarning />}
    </header>
  );
}

interface ToolbarProps {
  segments: SegmentInput[];
  from: string;
  to: string;
  busy: BusyState;
  fileInputRef: React.RefObject<HTMLInputElement | null>;
  onFileChange: (e: React.ChangeEvent<HTMLInputElement>) => void;
  onLoadSample: () => void;
  onTranslate: () => Promise<void>;
  onQa: () => Promise<void>;
  onScene: () => Promise<void>;
  onDirector: () => Promise<void>;
  onAudition: () => Promise<void>;
}

function Toolbar({ segments, from, to, busy, fileInputRef, onFileChange, onLoadSample, onTranslate, onQa, onScene, onDirector, onAudition }: ToolbarProps) {
  const disabled = busy !== null || segments.length === 0;
  return (
    <div className="toolbar">
      <div className="toolbar-left">
        <button type="button" className="btn btn-primary" onClick={() => void onTranslate()} disabled={disabled}>
          {busy === "translate" ? "Translating…" : "Run Translate"}
        </button>
        <button type="button" className="btn btn-primary" onClick={() => void onQa()} disabled={disabled}>
          {busy === "qa" ? "Running QA…" : "Run QA"}
        </button>
        <button type="button" className="btn btn-primary" onClick={() => void onScene()} disabled={disabled}>
          {busy === "scene" ? "Running Scene…" : "Run Scene"}
        </button>
        <button type="button" className="btn btn-primary" onClick={() => void onDirector()} disabled={disabled}>
          {busy === "director" ? "Running Director…" : "Run Director"}
        </button>
        <button type="button" className="btn btn-primary" onClick={() => void onAudition()} disabled={disabled}>
          {busy === "audition" ? "Running Audition…" : "Run Audition"}
        </button>
        <span className="toolbar-hint">
          {segments.length} segments · {from} → {to}
        </span>
      </div>
      <div className="toolbar-right">
        <button type="button" className="btn" onClick={onLoadSample}>
          Load sample
        </button>
        <label className="btn file-btn">
          Load JSON
          <input ref={fileInputRef} type="file" accept=".json,application/json" onChange={onFileChange} hidden />
        </label>
      </div>
    </div>
  );
}

interface LineListProps {
  segments: SegmentInput[];
  qaById: Map<string, QaSegmentReport> | null;
  batchById: Map<string, string> | null;
  sceneById: Map<number, SceneSegmentResult> | null;
  directorById: Map<string, DirectorAdaptationSegmentResult> | null;
  auditionById: Map<string, AuditionSegmentReport> | null;
  expanded: Record<string, boolean>;
  onToggle: (id: string) => void;
}

function LineList({ segments, qaById, batchById, sceneById, directorById, auditionById, expanded, onToggle }: LineListProps) {
  return (
    <div className="lines" role="list">
      {segments.map((seg, idx) => (
        <LineCard
          key={seg.id}
          seg={seg}
          q={qaById?.get(seg.id)}
          translated={batchById?.get(seg.id) ?? null}
          scene={sceneById?.get(idx)}
          director={directorById?.get(seg.id)}
          audition={auditionById?.get(seg.id)}
          isExpanded={expanded[seg.id] ?? false}
          onToggle={() => onToggle(seg.id)}
        />
      ))}
    </div>
  );
}

function QaReportFooter({ qa }: { qa: QaReport }) {
  return (
    <section className="report-footer">
      <h2>QA report</h2>
      <pre className="report-json">{JSON.stringify(qa.totals, null, 2)}</pre>
      <p className="report-note">
        Shape matches <code>samples/qa-report.json</code> from <code>dotnet run --project src/Trackdub.Director.Cli -- qa</code>.
      </p>
    </section>
  );
}

function AppFooter() {
  return (
    <footer className="app-footer">
      <span>Trackdub Director · Core, Server, web frontend, CLI harness</span>
      <span className="footer-sep">·</span>
      <a href="/api/status">/api/status</a>
    </footer>
  );
}

function useStatus() {
  const [status, setStatus] = useState<StatusResponse | null>(null);
  const [statusError, setStatusError] = useState<string | null>(null);

  const refreshStatus = useCallback(async () => {
    try {
      setStatus(await fetchStatus());
      setStatusError(null);
    } catch (e) {
      setStatusError(errorMessage(e));
    }
  }, []);

  useEffect(() => {
    void refreshStatus();
  }, [refreshStatus]);

  return { status, statusError, refreshStatus };
}

function useReports() {
  const [qa, setQa] = useState<QaReport | null>(null);
  const [batch, setBatch] = useState<TranslateBatch | null>(null);
  const [scene, setScene] = useState<SceneContextBatch | null>(null);
  const [director, setDirector] = useState<DirectorAdaptationBatch | null>(null);
  const [audition, setAudition] = useState<AuditionReport | null>(null);

  const reset = useCallback(() => {
    setQa(null);
    setBatch(null);
    setScene(null);
    setDirector(null);
    setAudition(null);
  }, []);
  const sceneById = useMemo(() => (scene ? new Map(scene.segments.map((s, i) => [i, s] as const)) : null), [scene]);
  const qaById = useMemo(() => (qa ? new Map(qa.segments.map((s) => [s.id, s] as const)) : null), [qa]);
  const batchById = useMemo(() => (batch ? new Map(batch.segments.map((s) => [s.id, s.translation] as const)) : null), [batch]);

  const directorById = useMemo(() => (director ? new Map(director.segments.map((s) => [s.id, s] as const)) : null), [director]);
  const auditionById = useMemo(() => (audition ? new Map(audition.segments.map((s) => [s.id, s] as const)) : null), [audition]);

  return { qa, setQa, batch, setBatch, scene, setScene, director, setDirector, audition, setAudition, reset, qaById, sceneById, batchById, directorById, auditionById };
}

function useSegments(onReset: () => void, setError: React.Dispatch<React.SetStateAction<string | null>>) {
  const [segments, setSegments] = useState<SegmentInput[]>(() => sampleSegments as SegmentInput[]);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const fileInputRef = useRef<HTMLInputElement>(null);

  const handleLoadSample = useCallback(() => {
    setSegments(sampleSegments as SegmentInput[]);
    setError(null);
    onReset();
  }, [onReset, setError]);

  const handleFile = useCallback(async (file: File) => {
    setError(null);
    try {
      setSegments(parseSegments(await file.text()));
      onReset();
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [onReset, setError]);

  const onFileChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const f = e.target.files?.[0];
      if (f) void handleFile(f);
      if (fileInputRef.current) fileInputRef.current.value = "";
    },
    [handleFile],
  );

  const toggle = useCallback((id: string) => {
    setExpanded((m) => ({ ...m, [id]: !m[id] }));
  }, []);

  return { segments, expanded, fileInputRef, handleLoadSample, onFileChange, toggle };
}

interface ActionsContext {
  segments: SegmentInput[];
  from: string;
  to: string;
  refreshStatus: () => Promise<void>;
  setBatch: (batch: TranslateBatch) => void;
  setQa: (qa: QaReport) => void;
  setScene: (scene: SceneContextBatch) => void;
  setDirector: (director: DirectorAdaptationBatch) => void;
  setAudition: (audition: AuditionReport) => void;
  qaById: Map<string, QaSegmentReport> | null;
  batchById: Map<string, string> | null;
  sceneById: Map<number, SceneSegmentResult> | null;
  setError: React.Dispatch<React.SetStateAction<string | null>>;
  sceneRequestRef: React.RefObject<number>;
  directorRequestRef: React.RefObject<number>;
  auditionRequestRef: React.RefObject<number>;
}

function useActions({ segments, from, to, refreshStatus, setBatch, setQa, setScene, setDirector, setAudition, qaById, batchById, sceneById, setError, sceneRequestRef, directorRequestRef, auditionRequestRef }: ActionsContext) {
  const [busy, setBusy] = useState<BusyState>(null);

  const run = useCallback(async (kind: Exclude<BusyState, null>, action: () => Promise<void>) => {
    setBusy(kind);
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
      void refreshStatus();
    }
  }, [refreshStatus, setError]);

  const runTranslate = useCallback(
    () => run("translate", async () => setBatch(await postTranslate(segments, from, to))),
    [run, segments, from, to, setBatch],
  );
  const runQa = useCallback(
    () => run("qa", async () => setQa(await postQa(segments, from, to))),
    [run, segments, from, to, setQa],
  );
  const runScene = useCallback(async () => {
    const requestId = ++sceneRequestRef.current;
    await run("scene", async () => {
      const res = await postSceneContext(segments);
      if (requestId === sceneRequestRef.current) setScene(res);
    });
  }, [run, segments, setScene, sceneRequestRef]);

  const runDirector = useCallback(async () => {
    await run("director", async () => {
      // Existing translation: explicit input wins, else the QA final line, else
      // the plain translation. Context: explicit input wins, else the scene
      // lane's context for this segment. Segments without an opt-in are sent
      // as-is and come back NotRequested with no calls made for them.
      const prepared: DirectorAdaptationInput[] = segments.map((seg, idx) => {
        const sceneContext = sceneById?.get(idx)?.context;
        return {
          id: seg.id,
          text: seg.text,
          translation: seg.translation ?? qaById?.get(seg.id)?.final_translation ?? batchById?.get(seg.id) ?? null,
          duration_seconds: seg.duration_seconds ?? null,
          context:
            seg.context ??
            (sceneContext
              ? {
                  speaker_id: sceneContext.speaker_id ?? null,
                  speaker_label: sceneContext.speaker_label ?? null,
                  emotion_tone_label: sceneContext.emotion_tone_label ?? null,
                  on_screen_text: sceneContext.on_screen_text ?? null,
                  scene_summary: sceneContext.scene_summary ?? null,
                  speaker_description: sceneContext.speaker_description ?? null,
                  burned_in_text: sceneContext.burned_in_text ?? null,
                }
              : null),
          director_adaptation: seg.director_adaptation
            ? { target_register: seg.director_adaptation.target_register ?? null }
            : null,
        };
      });
      const requestId = ++directorRequestRef.current;
      const res = await postDirectorAdaptation(prepared, from, to);
      if (requestId === directorRequestRef.current) setDirector(res);
    });
  }, [run, segments, from, to, setDirector, qaById, batchById, sceneById, directorRequestRef]);

  const runAudition = useCallback(async () => {
    const requestId = ++auditionRequestRef.current;
    await run("audition", async () => {
      const res = await postAudition(segments, from, to);
      if (requestId === auditionRequestRef.current) setAudition(res);
    });
  }, [run, segments, from, to, setAudition, auditionRequestRef]);

  return { busy, runTranslate, runQa, runScene, runDirector, runAudition };
}

export default function App() {
  const [error, setError] = useState<string | null>(null);
  const { status, statusError, refreshStatus } = useStatus();
  const reports = useReports();
  const sceneRequestRef = useRef(0);
  const directorRequestRef = useRef(0);
  const auditionRequestRef = useRef(0);
  const onReset = useCallback(() => {
    reports.reset();
    sceneRequestRef.current += 1;
    directorRequestRef.current += 1;
    auditionRequestRef.current += 1;
  }, [reports]);
  const input = useSegments(onReset, setError);
  const actions = useActions({
    segments: input.segments,
    from: DEFAULT_FROM,
    to: DEFAULT_TO,
    refreshStatus,
    setBatch: reports.setBatch,
    setQa: reports.setQa,
    setScene: reports.setScene,
    setDirector: reports.setDirector,
    setAudition: reports.setAudition,
    qaById: reports.qaById,
    batchById: reports.batchById,
    sceneById: reports.sceneById,
    setError,
    sceneRequestRef,
    directorRequestRef,
    auditionRequestRef,
  });

  return (
    <div className="app">
      <AppHeader status={status} statusError={statusError} error={error} qa={reports.qa} />
      <main className="main">
        <Toolbar
          segments={input.segments}
          from={DEFAULT_FROM}
          to={DEFAULT_TO}
          busy={actions.busy}
          fileInputRef={input.fileInputRef}
          onFileChange={input.onFileChange}
          onLoadSample={input.handleLoadSample}
          onTranslate={actions.runTranslate}
          onQa={actions.runQa}
          onScene={actions.runScene}
          onDirector={actions.runDirector}
          onAudition={actions.runAudition}
        />
        <LineList
          segments={input.segments}
          qaById={reports.qaById}
          batchById={reports.batchById}
          sceneById={reports.sceneById}
          directorById={reports.directorById}
          auditionById={reports.auditionById}
          expanded={input.expanded}
          onToggle={input.toggle}
        />
        {reports.qa && <QaReportFooter qa={reports.qa} />}
      </main>
      <AppFooter />
    </div>
  );
}
