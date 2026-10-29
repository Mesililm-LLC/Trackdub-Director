# Metrics Definitions: Trackdub-Director

Prepared 2026-10-28 by Luna, for the Nov 2 metrics pass. Every metric below names its source field in a report the app already emits (field names verified against the repo, branch feat/director-mode, 2026-10-28). If a number cannot be produced from one of these fields, it is not a metric; it goes in the excluded list at the bottom, with its reason.

## Counting and reporting rules (apply to every metric)

1. Test counts are `dotnet test` discovered cases from `--list-tests` output, with method counts reported alongside (example format: "76 cases / 54 methods"). The "36 passed" figure from earlier reports matches neither cases nor methods on main (recount: main is 66 cases / 48 methods) and must never be quoted again, including in the submission.
2. Metrics capture runs after any QA pass or verification run, not gated on commit or push (Michael's rule, 2026-10-28).
3. Every captured run records: date, commit SHA, dataset id (sample-8 or voa10), QA config (max_attempts, tolerance, CPS, reasoning_effort), and the report file names the numbers came from. QA numbers are only comparable under a stated config.
4. A metric that was not measured is reported as "not measured", with the reason. A skipped check never reads as a pass.
5. Dollar costs are not claimed anywhere. No verified per-token price exists for the Meta Model API credits this build runs on, so token counts are the cost metric. (Partner-endpoint pricing seen for Muse Spark Open on Modal is a different route and does not price this one.)

## The metrics

### QA loop (source: QA report JSON, QaTotals and per-segment reports)

| Metric | Definition | Notes |
|---|---|---|
| First-pass rate | segments whose FirstAttempt.Verdict is Pass, divided by total segments | Already computed into the report totals; quote the report's count, do not recount by hand. Reference run (Day 3 sample, config: max_attempts 3, tolerance 10%, CPS 15, reasoning low): 5/8 = 62%. |
| Final pass rate | segments whose FinalVerdict is Pass, divided by total segments | Same reference run: 8/8 = 100%. Model nondeterminism is real (an earlier run was 50% to 100%); always name the run and config beside this number. |
| Revision burden | total revisions (sum of per-segment Revisions) and revisions per segment | Reference run: 3 revisions. This is the cost side of the pass-rate gain; never quote final pass rate without it. |
| Meaning-gate catches | attempts where the meaning verdict is Fail, counted across all attempts | The exhibit case: "Necesitamos una toma más" shortened to "Una toma más" passes timing, fails meaning (dropped subject/modality). Count from MeaningCheckResult.verdict per attempt. |
| NotEvaluated count | attempts where the meaning verdict is NotEvaluated | Meaning is deliberately skipped when timing fails. Reported separately, never folded into pass or fail. Zero meaning tokens must accompany these attempts. |
| Meaning Error count | attempts where the meaning verdict is Error | Transport/API failure. Counted as combined Fail in the report; listed here so infrastructure noise is visible instead of masquerading as translation quality. |

### Token usage (source: usage objects in each lane's output)

| Metric | Definition | Notes |
|---|---|---|
| Translation tokens | usage_total (prompt_tokens, completion_tokens, total_tokens) in the translation batch output | Per-segment usage also exists; report the batch total and the per-segment max if one line dominates. |
| QA tokens | token totals in the QA report (translation + revisions + meaning checks) | Reference: meaning-gate live accounting, corrected 2026-10-28: before meaning gate 4,770 tokens, after 9,332 (4,810 translation + 4,522 meaning), delta 4,562, plus a 134-token probe, grand total 14,236 for that exercise. Quote token arithmetic only after re-adding the components. |
| Meaning tokens | sum of MeaningCheckResult.usage across attempts | The price of the meaning gate, stated separately so the gate's cost is as visible as its catches. |
| Scene tokens and images | scene usage totals plus images_processed count | sam-3.1 has so far reported 0 tokens on every call with images_processed: 1 per call. Report both numbers; if a future call reports nonzero tokens, the new figure supersedes, do not average them away. |
| Hidden reasoning share | reasoning_tokens divided by completion_tokens, from raw response usage details | Probe finding: about 85% of completion tokens were hidden reasoning tokens before reasoning_effort=low roughly halved completion tokens per line. Context for every token figure above; cite as a probe result, not a per-run guarantee. |

### Scene context (source: scene-context report)

| Metric | Definition | Notes |
|---|---|---|
| Parse rate | segments with status Parsed, divided by segments queried | Mechanical success only. Current live evidence: 1/1. |
| Target accuracy (human-verified) | returned regions confirmed by crop inspection to be the intended target, divided by regions returned | Scored against the human sign box recorded in the demo-asset spec. Current live evidence: 0/1 (the single "coffee shop sign" region was the window reflection). Reported beside parse rate, never merged into it. This is the honest headline of the scene lane so far. |
| Empty / Failed counts | segments with status Empty or Failed | Empty splits into "queried and missed" (regions: []) and "not queried" (regions: null, no frame/concepts). Keep the split; they mean different things. |

### Engineering state (source: test runner, git)

| Metric | Definition | Notes |
|---|---|---|
| Test cases / methods | `--list-tests` discovered cases, and method count, for .NET; vitest count for web | Snapshot at Nov 2 freeze from merged main only, not from feature branches. Reference points: main 66 cases / 48 methods and 2 web tests after PR #2; 76 / 54 and 4 web tests if PR #3 (Stage A) is merged. |
| Cold start | wall time from `dotnet run --project src/Trackdub.Director.Server` start to first GET /api/status returning 200 | Procedure defined here because it has never been measured. Nov 2: run 3 times on the demo machine, report the median and the machine. Do not backfill or estimate. |
| End-to-end wall time | wall time of one full demo-asset run (translate, QA, scene) through the app | Measured on the Nov 1 integration run and repeated Nov 2. Name the dataset; the 8-line sample and the VOA dataset are not interchangeable. |

## Explicitly excluded (do not put these in the submission)

| Claimed metric | Why it is excluded |
|---|---|
| Lip-error delta | There is no lip-sync stage in this app. The metric cannot exist. It appeared in early plan drafts and dies here. |
| Cost per dubbed minute (dollars) | No verified per-token price for the route actually used (see rule 5). A token-based version is allowed: total tokens per finished segment, from the fields above. |
| SAM mask accuracy | mask_data is stored verbatim and never decoded in Stage A. There is nothing to score. |
| User, traffic, or revenue metrics | None exist. Trackdub-Director is a hackathon build with no live users. |
| GPU latency / throughput figures | Those belong to Trackdub's AWS POC, a different project that has not run. Nothing from it may be quoted for this entry. |

## Capture template (one per measured run)

```
date:
commit:
dataset: (sample-8 | voa10)
config: max_attempts=, tolerance=, cps=, reasoning_effort=
reports: (file names)
first_pass: n/N   final_pass: n/N   revisions:
meaning: catches=, not_evaluated=, errors=
tokens: translation=, qa=, meaning=, scene= (images_processed=)
scene: parsed=, target_verified=, empty=, failed=
wall_time_e2e:   cold_start (if measured):
notes: (nondeterminism, anomalies, anything that would change how the numbers read)
```
