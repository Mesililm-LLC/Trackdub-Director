# Trackdub Live - Director Lane

Hackathon build for the Meta AI Developer Hackathon (Oct 26 - Nov 4, 2026).
This is a fresh, standalone public repository created for the event. All code
in this repo is written during the hackathon build period.

## What is pre-existing

- **Trackdub public core** - the existing Trackdub product and its public
  codebase ([github.com/mesalimllc/Trackdub](https://github.com/mesalimllc/Trackdub)).
  It predates this hackathon, is **not** part of this submission, and is not
  copied into this repo. This project references it as a dependency / pipeline
  stage boundary only. The core is .NET 10 and this repo mirrors its solution
  layout, CLI structure, and naming conventions.
- The gated Trackdub shell is used as the demo stage only. It is not part of
  this repo or the submission.

## What will be built this week (in this repo)

The Director Lane: Meta-model stages that sit alongside the existing
Trackdub pipeline.

- Meta Model API wiring + CLI entry point (today)
- Translation lane behind the existing consent gate, with provenance and
  metering preserved
- QA / timing rewrite loop (bounce-back revisions with a pass-rate harness)
- Director mode: scene context driving register adaptation
- Audition casting: multiple takes per line, scored and auto-picked
- Muse Image visual localisation of on-screen text

No greenfield web app, no live/streaming dub. Pipeline stages with one real
feedback loop (the QA/timing bounce-back).

## Status

Day 1 (Oct 26): .NET 10 solution scaffold, Meta Model API auth, EN->ES smoke
test. `muse-spark-1.1` via `https://api.meta.ai/v1`.

## Setup

Requires .NET 10 SDK (10.0.300+; pinned via `global.json`).

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
`META_MODEL_API_BASE_URL` (default `https://api.meta.ai/v1`).
`META_MODEL_API_KEY` is accepted as a fallback for `META_API_KEY`.

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

Tests:

```bash
dotnet test
```

The live smoke test is skipped (not failed) when no API key is present.

## Layout

```text
Trackdub.Director.slnx
src/Trackdub.Director.Core/    Class library: Meta Model API client (HttpClient + System.Text.Json)
src/Trackdub.Director.Cli/     Console app: smoke subcommand (more lanes added later)
tests/Trackdub.Director.Tests/ xUnit tests (live smoke test, skipped without META_API_KEY)
.env.example                   Placeholder config - no real keys
global.json                    Pins .NET 10 SDK
```
