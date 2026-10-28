import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  fetchStatus,
  postQa,
  postTranslate,
  type QaReport,
  type QaSegmentReport,
  type SegmentInput,
  type StatusResponse,
  type TranslateBatch,
} from "./api";
import sampleSegments from "./sampleSegments.json";

const DEFAULT_FROM = "en";
const DEFAULT_TO = "es";

function pct(n: number): string {
  return `${Math.round(n * 100)}%`;
}

function fmtSec(n: number): string {
  return `${n.toFixed(2)}s`;
}

export default function App() {
  const [status, setStatus] = useState<StatusResponse | null>(null);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [segments, setSegments] = useState<SegmentInput[]>(() => sampleSegments as SegmentInput[]);
  const [from] = useState(DEFAULT_FROM);
  const [to] = useState(DEFAULT_TO);
  const [qa, setQa] = useState<QaReport | null>(null);
  const [batch, setBatch] = useState<TranslateBatch | null>(null);
  const [busy, setBusy] = useState<"translate" | "qa" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const fileInputRef = useRef<HTMLInputElement>(null);

  const refreshStatus = useCallback(async () => {
    try {
      const s = await fetchStatus();
      setStatus(s);
      setStatusError(null);
    } catch (e) {
      setStatusError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    void refreshStatus();
  }, [refreshStatus]);

  const qaById = useMemo(() => {
    if (!qa) return null;
    return new Map(qa.segments.map((s) => [s.id, s] as const));
  }, [qa]);

  const batchById = useMemo(() => {
    if (!batch) return null;
    return new Map(batch.segments.map((s) => [s.id, s.translation] as const));
  }, [batch]);

  const handleLoadSample = useCallback(() => {
    setSegments(sampleSegments as SegmentInput[]);
    setQa(null);
    setBatch(null);
    setError(null);
  }, []);

  const handleFile = useCallback(async (file: File) => {
    setError(null);
    try {
      const text = await file.text();
      const parsed = JSON.parse(text) as unknown;
      if (!Array.isArray(parsed)) throw new Error("JSON must be an array of { id, text, duration_seconds? }");
      const arr = parsed as SegmentInput[];
      for (let i = 0; i < arr.length; i++) {
        if (!arr[i]?.id || typeof arr[i].text !== "string") {
          throw new Error(`Segment at index ${i} must have { id, text }`);
        }
      }
      setSegments(arr);
      setQa(null);
      setBatch(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  const onFileChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const f = e.target.files?.[0];
      if (f) void handleFile(f);
      if (fileInputRef.current) fileInputRef.current.value = "";
    },
    [handleFile],
  );

  const runTranslate = useCallback(async () => {
    setBusy("translate");
    setError(null);
    try {
      const res = await postTranslate(segments, from, to);
      setBatch(res);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
      void refreshStatus();
    }
  }, [segments, from, to, refreshStatus]);

  const runQa = useCallback(async () => {
    setBusy("qa");
    setError(null);
    try {
      const res = await postQa(segments, from, to);
      setQa(res);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
      void refreshStatus();
    }
  }, [segments, from, to, refreshStatus]);

  const toggle = useCallback((id: string) => {
    setExpanded((m) => ({ ...m, [id]: !m[id] }));
  }, []);

  return (
    <div className="app">
      <header className="app-header">
        <div className="app-header-inner">
          <div className="brand">
            <h1 className="brand-title">Trackdub Director</h1>
            <p className="brand-sub">Meta AI Developer Hackathon — Director Lane</p>
          </div>
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
          <div className="qa-summary" aria-label="QA summary">
            {qa ? (
              <>
                <div className="qa-stat">
                  <span className="qa-stat-label">First-pass</span>
                  <span className="qa-stat-value">{pct(qa.totals.first_pass_rate)}</span>
                  <span className="qa-stat-detail">
                    {qa.totals.first_pass_count}/{qa.totals.total_segments}
                  </span>
                </div>
                <div className="qa-stat">
                  <span className="qa-stat-label">Final</span>
                  <span className="qa-stat-value qa-stat-final">{pct(qa.totals.final_pass_rate)}</span>
                  <span className="qa-stat-detail">
                    {qa.totals.final_pass_count}/{qa.totals.total_segments}
                  </span>
                </div>
                <div className="qa-stat">
                  <span className="qa-stat-label">Revisions</span>
                  <span className="qa-stat-value">{qa.totals.total_revisions}</span>
                </div>
                <div className="qa-stat">
                  <span className="qa-stat-label">Tokens</span>
                  <span className="qa-stat-value">{qa.totals.total_tokens.toLocaleString()}</span>
                </div>
              </>
            ) : (
              <span className="qa-empty">Run QA to see pass rates</span>
            )}
          </div>
        </div>
        {statusError && <div className="banner banner-error">{statusError}</div>}
        {error && <div className="banner banner-error">{error}</div>}
        {!status?.hasKey && !statusError && (
          <div className="banner banner-warn">
            No Meta API key detected. Set <code>META_API_KEY</code> and restart the server to enable Translate and QA. The UI still loads without a key.
          </div>
        )}
      </header>

      <main className="main">
        <div className="toolbar">
          <div className="toolbar-left">
            <button type="button" className="btn btn-primary" onClick={() => void runTranslate()} disabled={busy !== null || segments.length === 0}>
              {busy === "translate" ? "Translating…" : "Run Translate"}
            </button>
            <button type="button" className="btn btn-primary" onClick={() => void runQa()} disabled={busy !== null || segments.length === 0}>
              {busy === "qa" ? "Running QA…" : "Run QA"}
            </button>
            <span className="toolbar-hint">
              {segments.length} segments · {from} → {to}
            </span>
          </div>
          <div className="toolbar-right">
            <button type="button" className="btn" onClick={handleLoadSample}>
              Load sample
            </button>
            <label className="btn file-btn">
              Load JSON
              <input ref={fileInputRef} type="file" accept=".json,application/json" onChange={onFileChange} hidden />
            </label>
          </div>
        </div>

        <div className="lines" role="list">
          {segments.map((seg) => {
            const q: QaSegmentReport | undefined = qaById?.get(seg.id);
            const translated = batchById?.get(seg.id) ?? null;
            const displayTranslation = q?.final_translation ?? translated ?? "—";
            const windowSec = q?.window_seconds ?? seg.duration_seconds ?? null;
            const verdict = q?.final_verdict ?? null;
            const firstVerdict = q?.first_attempt.verdict ?? null;
            // Gate pills and the estimate must describe the translation shown (the final attempt),
            // not the initial one; otherwise a successful bounce-back displays failing gates.
            const shownAttempt = q ? (q.revisions.length > 0 ? q.revisions[q.revisions.length - 1] : q.first_attempt) : null;
            const estimated = shownAttempt ? shownAttempt.estimated_seconds : null;
            const attemptCount = q ? 1 + q.revisions.length : translated ? 1 : 0;
            const isExpanded = expanded[seg.id] ?? false;

            return (
              <article key={seg.id} className="line-card" role="listitem">
                <div className="line-main">
                  <div className="line-id">{seg.id}</div>
                  <div className="line-source">{seg.text}</div>
                  <div className="line-translation" title={displayTranslation}>
                    {displayTranslation}
                  </div>
                  <div className="line-meta">
                    <span className="meta-pill" title="Timing window vs estimated duration">
                      {windowSec != null ? `window ${fmtSec(windowSec)}` : "window —"}
                      {estimated != null ? ` · est ${fmtSec(estimated)}` : ""}
                    </span>
                    {q && shownAttempt ? (
                      <>
                        <span className={`verdict verdict-${shownAttempt.timing_verdict.toLowerCase()}`} title={`Timing gate (attempt #${shownAttempt.attempt_number})`}>
                          Timing: {shownAttempt.timing_verdict}
                        </span>
                        {shownAttempt.meaning_verdict === "NotEvaluated" ? (
                          <span className="verdict verdict-notevaluated" title={`Not checked (timing failed, attempt #${shownAttempt.attempt_number})`}>
                            Meaning: Not checked
                          </span>
                        ) : (
                          <span className={`verdict verdict-${shownAttempt.meaning_verdict.toLowerCase()}`} title={`Meaning gate (attempt #${shownAttempt.attempt_number})`}>
                            Meaning: {shownAttempt.meaning_verdict}
                          </span>
                        )}
                        <span className={`verdict verdict-${(verdict ?? "idle").toLowerCase()}`} title="Combined verdict">
                          Final: {verdict}
                        </span>
                      </>
                    ) : verdict ? (
                      <span className={`verdict verdict-${verdict.toLowerCase()}`}>{verdict}</span>
                    ) : firstVerdict ? (
                      <span className={`verdict verdict-${firstVerdict.toLowerCase()}`}>{firstVerdict}</span>
                    ) : translated ? (
                      <span className="verdict verdict-pass">Translated</span>
                    ) : (
                      <span className="verdict verdict-idle">Idle</span>
                    )}
                    <span className="attempts">Attempts: {attemptCount || "—"}</span>
                    {q && q.revisions.length > 0 && <span className="revisions-count">Revisions: {q.revisions.length}</span>}
                  </div>
                </div>
                {q && (
                  <button type="button" className="expand-btn" onClick={() => toggle(seg.id)} aria-expanded={isExpanded}>
                    {isExpanded ? "Hide attempts" : "Show attempts"}
                  </button>
                )}
                {q && isExpanded && (
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
                    {[q.first_attempt, ...q.revisions].map((a) => (
                      <div key={`${seg.id}-${a.attempt_number}`} className="attempt-row">
                        <span>#{a.attempt_number}</span>
                        <span>{a.kind}</span>
                        <span className="attempt-text">{a.translation}</span>
                        <span>
                          {fmtSec(a.estimated_seconds)} / {fmtSec(a.window_seconds)} (max {a.max_chars}c)
                        </span>
                        <span className={`verdict verdict-${a.timing_verdict.toLowerCase()}`}>{a.timing_verdict}</span>
                        {a.meaning_verdict === "NotEvaluated" ? (
                          <span className="verdict verdict-notevaluated" title="Not checked (timing failed)">
                            Not checked
                          </span>
                        ) : (
                          <span className={`verdict verdict-${a.meaning_verdict.toLowerCase()}`} title={a.meaning_explanation ?? ""}>
                            {a.meaning_verdict}
                          </span>
                        )}
                        <span className={`verdict verdict-${a.verdict.toLowerCase()}`}>{a.verdict}</span>
                        <span>
                          {a.total_tokens}
                          {a.meaning_total_tokens > 0 ? ` +${a.meaning_total_tokens}m` : ""}
                        </span>
                      </div>
                    ))}
                    {[q.first_attempt, ...q.revisions].map((a) =>
                      a.meaning_issues && a.meaning_issues.length > 0 ? (
                        <div key={`${seg.id}-${a.attempt_number}-issues`} className="meaning-issues">
                          <strong>#{a.attempt_number} meaning issues:</strong> {a.meaning_issues.join("; ")}
                          {a.meaning_explanation ? ` — ${a.meaning_explanation}` : ""}
                        </div>
                      ) : a.meaning_explanation ? (
                        <div key={`${seg.id}-${a.attempt_number}-expl`} className="meaning-issues">
                          <strong>#{a.attempt_number} meaning:</strong> {a.meaning_explanation}
                        </div>
                      ) : null,
                    )}
                    <div className="token-usage">
                      Total tokens for line: {q.token_usage.total_tokens} (prompt {q.token_usage.prompt_tokens} + completion {q.token_usage.completion_tokens})
                    </div>
                    <div className="token-usage">
                      Meaning gate: model {q.first_attempt.meaning_model_id ?? "—"} · template {q.first_attempt.meaning_prompt_template_version ?? "—"} ·{" "}
                      {q.first_attempt.meaning_timestamp_utc ?? "—"}
                    </div>
                  </div>
                )}
              </article>
            );
          })}
        </div>

        {qa && (
          <section className="report-footer">
            <h2>QA report</h2>
            <pre className="report-json">{JSON.stringify(qa.totals, null, 2)}</pre>
            <p className="report-note">
              Shape matches <code>samples/qa-report.json</code> from <code>dotnet run --project src/Trackdub.Director.Cli -- qa</code>.
            </p>
          </section>
        )}
      </main>

      <footer className="app-footer">
        <span>Trackdub Director · Core, Server, web frontend, CLI harness</span>
        <span className="footer-sep">·</span>
        <a href="/api/status">/api/status</a>
      </footer>
    </div>
  );
}
