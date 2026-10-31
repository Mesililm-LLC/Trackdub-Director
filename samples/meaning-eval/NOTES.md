# Meaning-gate evaluation data (2026-10-31)

Two measurements of the meaning gate, both on the source lines of the voa10,
voa10-scale, and sample-8 datasets. Results and limits are recorded in
`docs/metrics-snapshot.md`.

## Pressure run: `pressure-segments.json` → `qa-report-pressure-70.json`

All 20 lines of `samples/voa10/segments.json`, `samples/voa10-scale/segments.json`
and `samples/segments.json`, in that order, with each `duration_seconds`
multiplied by 0.7 and rounded to two decimals. Nothing else changed. The
tighter windows stand in for a faster original performance, so the timing loop
has to compress harder; this is a deliberate stress condition, not the clips'
real timing.

```bash
dotnet run --project src/Trackdub.Director.Cli -- qa \
  --input samples/meaning-eval/pressure-segments.json \
  --output samples/meaning-eval/qa-report-pressure-70.json \
  --max-attempts 3 --tolerance 0.10 --cps 15
```

## Seeded eval: `cases.json` → `meaning-eval-report.json`

40 labeled cases written for this eval: 16 clean Spanish translations and 24
with one planted defect each (`error_type` names it, `note` says what changed).
Clean cases deliberately include idiomatic and register changes the gate must
allow. `seeded-14` is a real compression from the pressure run. `seeded-15` is
that run's failed `Gracias todos` attempt, edited to `Gracias a todos.`.

```bash
dotnet run --project src/Trackdub.Director.Cli -- meaning-eval \
  --input samples/meaning-eval/cases.json \
  --output samples/meaning-eval/meaning-eval-report.json
```

Both commands need `META_API_KEY` in the environment and make live calls;
model output varies between runs.
