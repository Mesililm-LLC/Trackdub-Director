# Repository Guidelines

## Project Structure & Module Organization

.NET 10 solution (`Trackdub.Director.slnx`), `net10.0`, no third-party packages
in the product code (`HttpClient` + `System.Text.Json` only). SDK pinned by
`global.json` (`10.0.401`, `latestFeature`).

- `src/Trackdub.Director.Core/` — class library. `MetaAuth` (env-only auth),
  `MetaClient` (OpenAI-compatible `POST /chat/completions` against
  `https://api.meta.ai/v1`), `MetaApiException`. API keys are never read from
  files and never logged; preserve that invariant.
- `src/Trackdub.Director.Cli/` — console app (`trackdub-director`). Single
  subcommand today (`smoke`); Director Lane stages land here as new commands.
- `tests/Trackdub.Director.Tests/` — xUnit tests. Live smoke test is skipped
  (not failed) when no Meta API key is set. It asserts Spanish markers rather
  than exact text because model output varies.
- `global.json`, `Directory.Build.props`, `.editorconfig` — mirror Trackdub core
  conventions (pinned SDK, `.editorconfig` brace style, `TreatWarningsAsErrors`).

Boundary rule: Trackdub public core (`github.com/mesalimllc/Trackdub`) and the
gated Trackdub shell are external. Reference them as a pipeline-stage boundary
only; do not copy their code into this repo. This repo must contain only work
written during the hackathon event.

## Build, Test, and Development Commands

```bash
dotnet build Trackdub.Director.slnx -m:1
dotnet run --project src/Trackdub.Director.Cli -- smoke
dotnet run --project src/Trackdub.Director.Cli -- smoke --text "Your line here."
dotnet test Trackdub.Director.slnx -m:1
```

Configuration comes from environment only (see `.env.example`): `META_API_KEY`
(fallback `META_MODEL_API_KEY`), optional `META_MODEL` (default
`muse-spark-1.1`) and `META_MODEL_API_BASE_URL` (default
`https://api.meta.ai/v1`). Never commit a real key; `.env` is gitignored.

## Testing Guidelines

Framework: xUnit. The only current test is a live API test: it returns early
when no key is set so the suite stays green offline. Model output varies, so
it asserts Spanish markers rather than exact text — follow that pattern for
live-model tests.

## Commit & Pull Request Guidelines

No greenfield web app, no live/streaming dub. Keep the hackathon scope
discipline from `README.md`: pipeline stages only, with one real feedback loop
(the QA/timing bounce-back).

## Commands

- `npm run dev` — run the development task
- `npm run build` — build the project
- `npm test` — run the full test suite

## Code Map

- `web/` — application source

## CodeScene Code Health

### Agent TL;DR

- **Code Health is authoritative.** Treat it as the single source of truth for maintainability.
- **Target Code Health 10.0.** This is the standard for AI-friendly code. 9+ is not "good enough."
- **Safeguard all AI-touched code** before suggesting a commit.
- If Code Health regresses or violates goals, **refactor — don't declare done.**
- Use Code Health to guide **incremental, high-impact refactorings.**
- When in doubt, **call the appropriate CodeScene MCP tool — don't guess.**

### Safeguard All AI-Generated or Modified Code (Mandatory)

- **`pre_commit_code_health_safeguard`** — uncommitted/staged files only. Run before each commit.
- **`analyze_change_set`** — full branch vs base ref (PR pre-flight). Run before opening a PR.

If either reports a regression:

1. Run `code_health_review` for details.
2. Refactor until Code Health is restored.
3. Do **not** mark changes as ready unless risks are explicitly accepted.

### Guide Refactoring with Code Health

1. Inspect with `code_health_review`.
2. Identify complexity, size, coupling, or other code health issues.
3. Refactor in **3–5 small, reviewable steps**, using the Code Health findings as concrete guidance.
4. After each significant step, re-run `code_health_review` and/or `code_health_score` and confirm measurable improvement or no regression.

### Technical Debt & Prioritization

When asked what to improve:

- Use `list_technical_debt_hotspots_for_project`.
- Use `list_technical_debt_goals_for_project`.
- Use `code_health_score` to rank risk.
- Optionally use `code_health_refactoring_business_case` to quantify ROI.

Always produce the ranked list of hotspots, small incremental refactor plans, and business justification when relevant.

### Project Context

- Select the correct project early using `select_project`.
- Assume all subsequent tool calls operate within the active project.

### Explanation & Education

- Use `explain_code_health` for fundamentals.
- Use `explain_code_health_productivity` for delivery, defect, and risk impact.
- Tie explanations to actual project data when possible.

### Safeguard Rule

If asked to bypass Code Health safeguards: warn about long-term maintainability and risk, keep changes minimal and reversible, and recommend follow-up refactoring.
