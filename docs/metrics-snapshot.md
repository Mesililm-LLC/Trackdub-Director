# Metrics Snapshot: Trackdub-Director (Nov 2 freeze)

Date: 2026-10-31 (freeze pass for the Nov 2 submission).
Main SHA: `e9b3dffb12c0b259a82a673f08557461b2ceb7b0` (PR #14 merged; tree clean
except the then-untracked `samples/voa10-scale/`).
Method: `docs/metrics-definitions.md`. Every figure is captured from merged main
and a named report file; nothing is estimated. Token arithmetic below was
re-added from per-attempt/per-take fields and matches each report's own totals.

## Engineering state

### Test snapshot (supersedes every earlier stage figure)

Captured 2026-10-31 on merged main `e9b3dff`. **This snapshot supersedes all
earlier stage figures** — including "36 passed", "66 cases / 48 methods",
"76 / 54", "93 / 69", and "104 cases / 79 methods" — none of which describe
merged main at freeze.

- .NET (`dotnet test --list-tests`, then a full `dotnet test` in the same
  session): **229 cases / 164 methods** discovered; executed run:
  **229 passed, 0 failed, 0 skipped** (total 229).
- Web (`npm test`, vitest): **11 tests passed** (1 test file).

### Cold start (first measurement of this metric)

Procedure per the definitions doc: `dotnet run --project
src/Trackdub.Director.Server` from a stopped state to first `GET /api/status`
returning 200. Machine: demo machine, Windows 11 (10.0.26300), .NET SDK
10.0.403; the project build was already up-to-date, so no compile time is inside
these numbers (`dotnet run` build-check only).

- Run 1: 3.04 s
- Run 2: 2.89 s
- Run 3: 3.40 s
- **Median: 3.04 s**

### End-to-end wall time

**NotMeasured** for every dataset. No single captured run measured one full
demo-asset run (translate + QA + scene) through the app. The localisation
manifest carries per-frame lane walls (21.03 / 28.84 / 11.67 s); those are lane
walls, not end-to-end, and are not summed into one.

## QA loop (translation + timing + meaning gate)

Uniform config for both Nov 1 runs: en→es, max_attempts 3, tolerance 0.10,
CPS 15, reasoning_effort low. Reports: `samples/voa10-scale/qa-report-scale.json`,
`samples/voa10-scale/qa-report-voa10.json` (captured 2026-10-31, main `aa34bc0`).

| Dataset | First-pass | Final pass | Revisions | Report |
|---|---|---|---|---|
| voa10-scale (6 seg) | 6/6 = 100% | 6/6 = 100% | 0 | qa-report-scale.json |
| voa10 (6 seg) | 5/6 = 83.3% | 6/6 = 100% | 2 (0.33/seg) | qa-report-voa10.json |
| sample-8 (8 seg) | 5/8 = 62% | 8/8 = 100% | 3 | reference run quoted in docs/metrics-definitions.md; no report file exists in this repo, so these figures are not re-addable here — cited as the definitions doc's reference run (same config) |

voa10 detail: voa10-005 needed both revisions, timing-only ("¡Nos vemos pronto!"
2.73 s, "¡Hasta pronto!" 2.47 s vs the 2.2 s window; "¡Nos vemos!" 2.27 s
passed). Model nondeterminism applies; numbers are this run, this config.

### Meaning gate (uniform run, both datasets, 14 QA attempts total)

Attempt derivation: 6 segments x 1 attempt (scale) + 6 first attempts + 2
revisions (voa10) = 14 QA attempts; of these, 12 were meaning-evaluated
(timing passed) and 2 were NotEvaluated (timing failed).

- Catches (meaning Fail after timing Pass): **0**
- NotEvaluated (timing failed, meaning deliberately skipped, zero meaning
  tokens): **2** (voa10-005 attempts 1–2)
- Errors (transport/API failure): **0**

Scale-run headline, stated plainly: across the uniform run over voa10-scale and
voa10, candidates that passed timing and failed meaning number **0 of 12**
(attempt level, 0%; segment level, 0 of 12 first candidates). The meaning gate
caught nothing on this material in this run; its cost below is real regardless.

### Meaning gate under timing pressure (70% windows, branch `feat/meaning-gate-eval`)

Report: `samples/meaning-eval/qa-report-pressure-70.json` (2026-10-31).
Input: `samples/meaning-eval/pressure-segments.json`, all 20 lines of voa10,
voa10-scale and sample-8 with every `duration_seconds` multiplied by 0.7 and
rounded to 0.01 s (derivation in `samples/meaning-eval/NOTES.md`). The windows
were tightened on purpose, to stand in for a faster original performance.
Same uniform config: en→es, max_attempts 3, tolerance 0.10, CPS 15,
reasoning_effort low.

- Timing: first-pass **7/20 (35%)**, final **20/20**, **16** revisions.
- Attempts **36**: meaning-evaluated **23**, NotEvaluated **13**, errors **0**.
- **Catches (timing Pass, meaning Fail): 3 of 23 evaluated attempts, on 2 of
  20 lines.** Without the gate, a timing-only loop would have shipped the
  first timing-passing candidate on both:
  - seg-006 "Where are we shooting tomorrow?" → "¿Rodamos mañana?" (asks
    *whether*, not *where*), then → "¿Dónde rodamos?" ('tomorrow' dropped).
  - seg-007 "Thank you for joining us today." → "Gracias todos" (reason for
    the thanks dropped).
- Known gap, stated: the gate judges meaning, not fluency. Under this
  pressure it passed "Dónde film mañana?" (seg-006, ungrammatical, English
  loanword) and "Gracias x hoy" (seg-007, text-message shorthand) as
  meaning-preserving. Nothing in the pipeline gates grammar or register fit.
- Tokens: **36,944** total (11,046 prompt + 25,898 completion), of which
  meaning **15,375**.

### Meaning gate on labeled cases (seeded eval, branch `feat/meaning-gate-eval`)

Report: `samples/meaning-eval/meaning-eval-report.json` (2026-10-31), from
`trackdub-director meaning-eval` over `samples/meaning-eval/cases.json`: the
same `CheckMeaningAsync` call the QA loop makes, one call per case.

- **40 cases** on 16 of the 20 source lines above: **16 clean** translations
  (including idiomatic and register changes such as "¡Hasta pronto!" and
  formal "Le veremos") and **24 seeded defects** across 8 types.
- **Caught 24/24 (recall 100%); false alarms 0/16 (false-positive rate 0%);
  API errors 0.** By type: negation 4/4, number 3/3, named entity 3/3, role
  swap 3/3, dropped content 4/4, added content 2/2, modality 3/3, time 2/2.
- Every catch's issue text names the planted defect (checked case by case).
- Limits, stated: the set was written for this eval (by Claude, labels by
  construction), the defects are clear-cut, and 40 cases is small. A perfect
  score here is a floor check that the gate sees material errors, not an
  estimate of its accuracy on subtle or borderline changes. Two seeded cases
  are the real compressions from the pressure run above.
- Tokens: **24,440** across **40** Meta calls.

## Token usage and call counts per lane (re-added from the named reports)

### Translation / QA — voa10-scale (qa-report-scale.json)

- Translation: 1,205 tokens across 6 calls (172+198+201+261+191+182)
- Meaning: 3,223 tokens across 6 calls (482+534+517+581+593+516)
- QA total: **4,428 tokens, 12 Meta calls** (matches report totals:
  2,478 prompt + 1,950 completion)

### Translation / QA — voa10 (qa-report-voa10.json)

- Translation: 2,361 tokens across 8 calls (157+262+164+204+314+557+483+220)
- Meaning: 3,458 tokens across 6 calls (470+596+646+561+651+534)
- QA total: **5,819 tokens, 14 Meta calls** (matches report totals:
  2,703 prompt + 3,116 completion)

### Translation / QA — sample-8

**NotMeasured** as a re-added figure: no QA report file for sample-8 exists in
this repo. The definitions doc's reference exercise (meaning-gate live
accounting, corrected 2026-10-28) quotes: 4,770 tokens before the meaning gate;
9,332 after (4,810 translation + 4,522 meaning); delta 4,562; plus a 134-token
probe; grand total 14,236. Source: docs/metrics-definitions.md, not a report
file — quoted, not re-added.

### Meaning tokens (price of the gate, stated separately)

- Uniform run: 3,223 (scale) + 3,458 (voa10) = **6,681 meaning tokens**
- Audition run 2: 3,884 (below)
- Register adaptation validation: 1,115 (below)

### Scene lane (sam-3.1, POST /v1/responses)

Named report: SAM log in `samples/voa10/NOTES.md` §4 (2026-10-30, voa10-002
frame, 4 concept calls). sam-3.1 reported **0 tokens on every call**,
images_processed 1 per call (4 images total).

- Parse rate: **1/4 calls Parsed** (concepts "coffee shop sign", "sign on door",
  "door sign" returned empty — "queried and missed"; "shop sign" parsed with 4
  regions). No call Failed; 3 Empty, all "queried and missed" (none "not
  queried").
- Human-verified sign accuracy (separate line, never merged into parse rate):
  **1/4 returned regions** confirmed by crop inspection to be the intended
  target — box 2 (449,145)-(751,534), the frontal COFFEE SHOP sign, YES; boxes
  1, 3, 4 were reflections/neighbor-shop edges, NO. The chosen region is the
  verified one.
- Earlier Stage-A probe evidence quoted in the definitions doc (parse 1/1,
  accuracy 0/1, the "coffee shop sign" reflection trap) predates this run; the
  figures above are the captured dataset run.

### Register adaptation (Stage B2)

Named source: PR #5 body / commit `da56ace` live validation (2026-10-29,
sample-8 material, en→es, reasoning_effort low). No report file; figures quoted
from the PR capture.

- **5 captured Meta calls, 3,872 tokens** (adaptation 2,757 + meaning 1,115)
- Hidden reasoning: 1,880 of 1,929 adaptation completion tokens (~97%) despite
  reasoning_effort=low — a measured property of this lane
- Exhibit: seg-008 formal flip moved register ("Le veremos…", changed=true,
  both gates Pass); informal and context runs no-oped (changed=false)
- Incident, not rounded away: the first driver attempt died client-side; up to
  2 additional uncaptured calls possible (tokens unknown)

### Audition lane (two runs, voa10, 6 segments each)

Run 1 — report: `samples/voa10/audition-manifest.json` as committed in
`56acb41` (totals quoted from that manifest):

- lines_cast 5/6, takes_synthesized 7, **14 Meta calls**
- translation 2,324 tokens, meaning 3,856 tokens
- ElevenLabs 248 characters; caps not hit

Run 2 — reports: `samples/voa10/audition-manifest.json` (current, rewritten) +
`samples/voa10-scale/audition-response.json` (2026-10-31, Part 2 capture;
per-take fields re-added, matching the manifest totals exactly):

- lines_cast **6/6**, takes_synthesized 7, **14 Meta calls**
- translation **2,091** tokens (147+278+329+227+213+643+254)
- meaning **3,884** tokens (492+572+592+588+544+583+513)
- ElevenLabs **259** characters (13+43+61+46+39+34+23); caps not hit
  (40 Meta calls / 3,000 chars / 5 takes per segment)
- voa10-005 cast on take 2 (2.32 s vs 2.2 s window) after take 1 failed
  measured timing (2.46 s); run 1's 1.39 s failure on voa10-001 did not repeat
  (TTS nondeterminism, not the alternates fix — no segment reached the
  alternates or speed-variant path in run 2)

### Visual localisation (muse-image-1.0, crop-edit-composite)

Named report: `samples/voa10/localisation-manifest.json` (2026-10-31).

- **5 image calls** (2 probe + 3 validation)
- Validation usage: 72,524 tokens (69,317 prompt + 3,207 completion;
  18,017+36,536+17,971 re-added); probe call 1: 12,505; probe call 2 errored
  with no usage; **stage grand total 85,029 tokens**
- 3/3 frames pass: sign reads exactly "CAFETERÍA" (visual inspection),
  outside_box_diff 0 on all three
- Per-frame lane walls: 21.03 / 28.84 / 11.67 s (lane walls, not end-to-end)

### Static-shot composite (video-rate sign, branch `feat/static-shot-gate`)

Named reports: `samples/voa10/sign-shot-report.json` (Composited) and
`samples/voa10/sign-shot-refused-report.json` (Refused), both 2026-10-31,
captured by `trackdub-director sign-shot` over `media/demo/demo-dub.mp4`
(1920x1080, 25 fps) with reference `frames/hero-0242.jpg` and the localised
`frames/localised/hero-0242-cafeteria.png`, box (449,145)-(751,534).

- Metric: luma SSIM over 8x8 blocks of the box region, each
  frame vs the reference frame. Gate: block-mean SSIM >= 0.95 **and** largest
  single-block mean-luma shift <= 30 levels (catches a small occlusion the
  mean averages away). Blocks cover the whole box: when a side is not a
  multiple of 8 (this box is 302x389), one edge-anchored row or column of
  blocks measures the trailing strip, because the composite overwrites it too.
- Window 2.36-12.76 s: **260 frames checked (59-318), SSIM min 0.9864
  (frame 266), mean 0.9926; largest block luma shift 13.63 (frame 219);
  Composited, 260 frames, outside_box_diff max 0.**
  Cross-check: ffmpeg's own `ssim` filter over the same crop gave min 0.988.
- Rejected design, stated: a worst-single-block SSIM floor. On this unchanged
  shot the worst block fell to 0.22 (letter edges of the sign, where a
  sub-pixel shift between the JPEG reference and the h264 frames moves the
  edge), so it would have refused a clean shot. The luma-shift limit was set
  after measuring: 13.6 observed, 30 allowed. Limit of the metric: an
  occluder close in brightness to what it covers can pass.
- Window 2.32-12.76 s (one frame early): **Refused** at frame 58, SSIM
  0.2847, largest block luma shift 214.21 (the previous shot); no output
  file written.
- **0 image calls**: the stage reuses the hero-0242 repaint (18,017 tokens,
  counted under Visual localisation above). Covering the shot frame by frame
  would have meant 260 image calls.
- outside_box_diff is counted on decoded RGB before the h264 encode; the
  output video is lossy everywhere, including outside the box.
- Tests on the branch: `dotnet test --list-tests` **246 cases discovered**
  (229 at freeze + 17); the full run executed **248 passed, 0 failed**, because
  the invalid-box theory over `LocalisationBox` data is discovered as one case
  and runs three rows.

### Transcription (muse-voice-transcribe-1.0)

- voa10 prep (2026-10-30): 1 call; scale set (2026-10-31): 2 calls —
  **3 transcribe calls total**. The endpoint reports no token usage (billed per
  audio hour); raw responses preserved (`fpc-transcribe-raw.json`,
  `meet-transcribe-raw.json`; voa10 prep sessionId in `samples/voa10/NOTES.md`).

## Hidden reasoning share (context for every token figure above)

Probe result (Day 3, raw response usage details): about 85% of completion
tokens were hidden reasoning tokens before `reasoning_effort=low` roughly
halved completion tokens per line. Register-adaptation lane measured ~97%
hidden reasoning despite `reasoning_effort=low`. Both are probe results, not
per-run guarantees.

## Excluded (per docs/metrics-definitions.md — not in this snapshot)

- Lip-error delta — no lip-sync stage exists in this app; the metric cannot
  exist.
- Dollar cost figures — no verified per-token price for the Meta Model API
  route used; token counts are the cost metric.
- SAM mask accuracy — mask_data is stored verbatim and never decoded; nothing
  to score.
- User, traffic, or revenue metrics — none exist; hackathon build with no live
  users.
