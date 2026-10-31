# Trackdub Director

Standalone open-source app built for the Meta AI Developer Hackathon (Oct 26 - Nov 4, 2026). All code in this repo was written during the event. C# Core engine, ASP.NET Core host, React frontend.

## What is pre-existing

- **Trackdub** — the author's prior project ([github.com/mesalimllc/Trackdub](https://github.com/mesalimllc/Trackdub)). It predates this hackathon and is not part of this submission.

## What this app is

A local dubbing workbench: a translation lane with provenance and metering, a deterministic QA/timing loop that bounces failures back to the model for revision, and a web UI to run both and inspect results — all backed by the Meta Model API (`muse-spark-1.1` via `https://api.meta.ai/v1`).

## Status

Day 3 (Oct 26): QA and timing loop on top of the translation lane. `muse-spark-1.1`
via `https://api.meta.ai/v1`. Raw probe found reasoning overhead: each
translation returned `usage.completion_tokens_details.reasoning_tokens` (about
85% of completion tokens) with no reasoning content in the message body. The
API accepts `reasoning_effort=low|medium|high` (tested live); wiring
`reasoning_effort: low` halves completion tokens. Sample batch
`samples/segments.json` carries `duration_seconds` per segment; deterministic
duration estimator (`chars / CPS`) checks each translation against its window,
bouncing failures back to the model with "preserve meaning, fit X seconds, at
most N chars" and re-checking up to 3 attempts. See QA / timing loop below.

Day 4 (Oct 26): Local web app. ASP.NET Core host exposes Core
over HTTP; React + TypeScript frontend. `src/Trackdub.Director.Server` serves
the UI and the API from one process; `web/` is the Vite app.

Director Mode Stage A (scene context, read-only): `SceneContextService`
segments caller-supplied concept phrases in a per-segment frame via
`sam-3.1` on `POST /v1/responses` (live-probed: `/chat/completions` rejects
that model, a generic instruction returns empty text, and a concept phrase
returns `output_text` box/mask tokens such as
`<|box;x1=10;y1=10;x2=29;y2=49;w=64;h=64|><|mask;x=0;y=0;data=...|>`).
Each segment gets a `DirectorContext` (speaker, emotion/tone, on-screen text,
scene summary, regions) in which every field is nullable: SAM evidences only
`regions`, speaker passes through only when caller-known, and the rest stay
null. Malformed output is `Failed` with no defaulted context. Exposed
read-only at `POST /api/scene-context` and as a `Scene:` outcome alongside
Timing/Meaning in the web UI (`Run Scene`).

## Adoption

The verification gates sit behind provider interfaces (translation, TTS,
image), so an existing dubbing pipeline can adopt the timing and meaning
verification layer without adopting the whole app: implement the interface
against your own providers and the QA loop, meaning gate, and revision
bounce-back work unchanged. Voice in the audition lane is ElevenLabs TTS
because the Meta model catalog offers no TTS endpoint; this is disclosed
wherever the audition is described.

## Architecture

```
Trackdub.Director.slnx
src/Trackdub.Director.Core/      Class library: MetaAuth (env-only auth),
                                  MetaClient (POST /chat/completions + GET /models
                                  against https://api.meta.ai/v1), TranslationService,
                                  DurationEstimator, QaService/QaOptions, QaModels.
                                  No third-party packages; HttpClient + System.Text.Json only.
                                  API keys are never read from files and never logged.
src/Trackdub.Director.Server/    ASP.NET Core minimal API referencing Core.
                                  GET /api/status, GET /api/models,
                                  POST /api/translate, POST /api/qa, POST /api/scene-context.
                                  Serves web/dist output from wwwroot in production;
                                  no key required to start; key only gates live endpoints.
src/Trackdub.Director.Cli/       Console app (trackdub-director): smoke / translate / models / qa.
web/                             React + Vite + TypeScript frontend. One screen:
                                  header (app name, key status, model, QA summary),
                                  line list (source, translation, window vs estimate,
                                  verdict, attempt count, expandable attempts),
                                  Run Translate / Run QA, file picker (sample loaded by default).
                                  Dev proxy: Vite forwards /api to :5103.
tests/Trackdub.Director.Tests/   xUnit. Live tests skipped without key; fake-handler
                                  estimator/QA tests; server endpoint tests (WebApplicationFactory).
samples/segments.json            8-line sample with duration_seconds.
```

The server reuses Core shapes directly — `TranslationBatchOutput`, `QaReport`,
etc. — so the JSON the UI receives from `POST /api/qa` is the same shape the
CLI writes to `qa-report.json`.

## Setup

Requires .NET 10 SDK (10.0.300+; pinned via `global.json`) and Node 18+ for the frontend.

Set the Meta Model API key in your environment. Never commit a key to this
repo; `.env` is gitignored and `.env.example` holds placeholders only.

PowerShell:

```powershell
$env:META_API_KEY = "<your key>"
```

bash:

```bash
export META_API_KEY="<your key>"
```

Optional overrides: `META_MODEL` (default `muse-spark-1.1`),
`META_MODEL_API_BASE_URL` (default `https://api.meta.ai/v1`),
`META_REASONING_EFFORT` (default `low`; accepted `low|medium|high`).
`META_MODEL_API_KEY` is accepted as a fallback for `META_API_KEY`.

## Running the app

### Single-command mode (prod-like)

A clean clone runs the whole app with one command:

```bash
dotnet run --project src/Trackdub.Director.Server
# open http://localhost:5103
```

The prebuilt web UI is committed under wwwroot so the app runs with a single
dotnet run; rebuild it with `cd web && npm run build` if you modify the web
frontend.

No key is required to start — the UI loads and `GET /api/status` reports
`hasKey: false` until a key is configured. Live endpoints
(`POST /api/translate`, `POST /api/qa`, `GET /api/models`) return 401/502 with
a safe error when no Meta key is set. `ELEVENLABS_API_KEY` is needed only for
live audition runs (`POST /api/audition`); when unset the audition lane fails
closed with zero calls.

### Development mode (Vite + backend)

Two terminals:

```bash
# Terminal 1 — backend on :5103
dotnet run --project src/Trackdub.Director.Server

# Terminal 2 — frontend on :5173 with /api proxy to :5103
cd web && npm install && npm run dev
# open http://localhost:5173
```

Vite config (`web/vite.config.ts`) proxies `http://localhost:5173/api/*` to
`http://localhost:5103`, so the dev UI talks to the same API.

To refresh the prod bundle after frontend changes: `cd web && npm run build`.

## Smoke test

One real Meta Model API call translating a single line from English to
Spanish:

```bash
dotnet run --project src/Trackdub.Director.Cli -- smoke
dotnet run --project src/Trackdub.Director.Cli -- smoke --text "Your line here."
```

Expected shape:

```text
EN: The director checks every line before it reaches the audience.
ES: <Spanish translation returned by the model>
```

## Translation lane

Batch translate a JSON file of `{id, text}` segments:

```bash
dotnet run --project src/Trackdub.Director.Cli -- translate --input samples/segments.json --output translated.json
dotnet run --project src/Trackdub.Director.Cli -- translate --input samples/segments.json --output translated.json --from en --to es
```

Input (`samples/segments.json`):

```json
[
  { "id": "seg-001", "text": "The director checks every line before it reaches the audience.", "duration_seconds": 4.6 }
]
```

`duration_seconds` is optional per segment. It is used by the QA loop (see
below); the plain `translate` command ignores it. When absent, the QA loop
estimates the window from the source line's own spoken duration at the
configured CPS.

Output: each segment gains `translation`, `provenance` (model ID, endpoint host,
UTC timestamp, prompt template version), and `usage` (prompt/completion/total
tokens per line and totaled). See `samples/segments.json` for a runnable 8-line
sample.

List models available to this account:

```bash
dotnet run --project src/Trackdub.Director.Cli -- models
```

## QA / timing loop

The `qa` subcommand runs translate plus a deterministic timing check over an
input file and writes a QA report JSON. Per segment: the timing window (explicit
`duration_seconds` or source-estimated), the first-attempt estimate and verdict,
each revision attempt, the final verdict, and token usage across all attempts.
A line that still fails after max attempts is reported as `Fail`, never
silently accepted.

Configurable: `--max-attempts` (default 3), `--tolerance` (fraction, default
`0.10` = 10%), `--cps` (characters per second for the target language, default
`15`). Env overrides `QA_MAX_ATTEMPTS`, `QA_TOLERANCE`, `QA_CPS_<LANG>`,
`META_REASONING_EFFORT`. The duration estimator is `seconds = chars / CPS` with
per-language CPS.

```bash
dotnet run --project src/Trackdub.Director.Cli -- qa --input samples/segments.json --output qa-report.json
dotnet run --project src/Trackdub.Director.Cli -- qa --input samples/segments.json --output qa-report.json --max-attempts 3 --tolerance 0.10 --cps 15
```

Report shape (`qa-report.json`):

```json
{
  "source_language": "en",
  "target_language": "es",
  "qa_options": { "max_attempts": 3, "tolerance_fraction": 0.1, "target_cps": 15, "source_cps": 15, "reasoning_effort": "low" },
  "segments": [
    {
      "id": "seg-001",
      "window_seconds": 4.6,
      "first_attempt": { "translation": "...", "estimated_seconds": 4.07, "verdict": "Pass", "prompt_tokens": 52, "completion_tokens": 96 },
      "revisions": [],
      "final_verdict": "Pass",
      "final_translation": "...",
      "token_usage": { "prompt_tokens": 52, "completion_tokens": 96, "total_tokens": 148 }
    }
  ],
  "totals": { "total_segments": 8, "first_pass_count": 4, "final_pass_count": 8, "total_revisions": 4, "first_pass_rate": 0.5, "final_pass_rate": 1.0, "total_tokens": 4398 }
}
```

How to read the pass-rate report: the console prints the same summary as the
`totals` block. `First-pass` is the share of segments that fit their window on
the initial translation. `Final` is the share after bounce-back revisions.
`Revisions` is the total number of revision calls. Tokens are summed across
all attempts. A non-zero gap between first-pass and final shows the loop
working; a final pass rate below 100% means at least one line was reported as
`Fail` after exhausting attempts.

## Server API

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/status` | none | `{ hasKey, model, baseUrl, reasoningEffort }` — never the key |
| GET | `/api/models` | key | Account models (proxies `GET /models`) |
| POST | `/api/translate` | key | `{ segments, sourceLanguage?, targetLanguage? }` → `TranslationBatchOutput` |
| POST | `/api/qa` | key | `{ segments, sourceLanguage?, targetLanguage?, options? }` → `QaReport` (same JSON as CLI report) |

`POST` bodies accept `segments` as `[{ id, text, duration_seconds? }]` plus
optional `sourceLanguage`/`source_language`/`from` and
`targetLanguage`/`target_language`/`to` (default `en` → `es`). `POST /api/qa`
also accepts `options: { maxAttempts, toleranceFraction, targetCharactersPerSecond, reasoningEffort }`.

## Tests

```bash
dotnet test
cd web && npm test
```

Live .NET tests (smoke, translation batch, QA) run only when a Meta API key is
present; otherwise they return early (skipped, not failed) so the suite stays
green offline. Deterministic tests use a fake HTTP handler: estimator unit
tests, a line that passes first try, a line that fails then passes on revision,
and a line that never fits and is reported failed after max attempts. Server
endpoint tests use `WebApplicationFactory` with the same fake handler.

Frontend: one Vitest smoke test rendering the line list from a mocked API response.

## Layout

```text
Trackdub.Director.slnx
src/Trackdub.Director.Core/    Class library: Meta Model API client, translation, duration estimator, QA loop
src/Trackdub.Director.Server/  ASP.NET Core host exposing Core over HTTP + serving the built frontend from wwwroot
src/Trackdub.Director.Cli/     Console app: smoke / translate / models / qa subcommands
web/                           React + Vite + TypeScript frontend (one screen)
tests/Trackdub.Director.Tests/ xUnit tests + server endpoint tests
samples/segments.json           8-line sample input with duration_seconds for the qa command
.env.example                   Placeholder config - no real keys
global.json                    Pins .NET 10 SDK
```

## Honest history

- Day 1-3: Core, translation lane, QA/timing loop, CLI harness — pushed to main.
- Day 4: Local web app — `Trackdub.Director.Server` (thin HTTP wrapper over Core) and `web/` (single-screen Vite app; proxies `/api` in dev, builds into `wwwroot` for single-command runs). No Tauri/Avalonia, no Core behavior changes.
