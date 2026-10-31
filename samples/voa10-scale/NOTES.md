# VOA10 scale set notes (Nov 1 hardening, Part 1 scale run)

Branch: `main` @ `aa34bc0` (visual localisation PR #13 merged). Data work only, no code changes.
Scope: transcribe + align + translate/QA only. No scene, audition, or localisation runs on this set.

## 1. Sources

- Audio: `samples/voa10/media/first-phone-call-audio.wav` (13.21s) and
  `samples/voa10/media/meeting-scene-audio.wav` (23.13s). Both probed with ffprobe as
  pcm_s16le 16 kHz mono before use — no ffmpeg re-extraction was needed.
- Script: published Lesson 10 "Come Over to My Place" conversation, same source as the
  voa10 line set: `http://learningenglish.voanews.com/a/lets-learn-english-lesson-10/3285228.html`
  (fetched 2026-10-31).
- Transcription: exactly 2 live calls, `POST https://api.meta.ai/v1/asr/transcribe`,
  `{"mode":"DIARIZATION","model":"muse-voice-transcribe-1.0","audioEncoding":"WAV"}` —
  same mode as asset prep. Raw responses kept: `fpc-transcribe-raw.json`
  (sessionId `voyager/transcribe/692540F37FAA44B79A37CD028A9E292D`, audioDurationMs 13280)
  and `meet-transcribe-raw.json` (sessionId `voyager/transcribe/09EC3768D71747CCB94651918861F8A0`,
  audioDurationMs 23200). Per-turn `startMs/endMs` + speaker labels; no per-word stamps.

## 2. Alignment (clip-relative seconds; duration = turn endMs - startMs, 0.1s)

### first-phone-call (script lines 2-4 of the published conversation)

| id | spk | script line | turn stamps (s) | dur | notes |
|---|---|---|---|---|---|
| fpc-001 | Anna (A) | Hi Ashley! | 1.74-2.94 | 1.2 | exact match |
| fpc-002 | Ashley (B) | Hi Anna! I'm coming to your apartment. Where is your apartment? | 3.02-7.26 | 4.2 | exact match |
| fpc-003 | Anna (labeled C) | My apartment is near the Columbia Heights Metro. | 7.66-12.78 | 5.1 | transcript appends a garbled echo " Metro."; canonical script text kept, duration covers the full turn including the echo |

Speaker map: A=Anna, B=Ashley; the diarizer labeled Anna's third line as a new speaker C.

### meeting-scene (script lines 16-18)

| id | spk | script line | turn stamps (s) | dur | notes |
|---|---|---|---|---|---|
| meet-001 | Anna (A) | Ashley! Ashley! Ashley! Over here! It's Anna! It's Anna! Hi! | 1.02-16.46 | 15.4 | transcript merges: leading "Okay." (not in the script line) and garbles "Santa! Donna! Oh!" for "It's Anna! It's Anna! Hi!" — one script line, kept whole |
| meet-002 | Anna (A) | I love having my friends over. Come on! | 17.34-21.50 | 4.2 | exact match |
| meet-003 | Ashley (B) | Great! | 21.50-22.70 | 1.2 | transcript hears "Hey."; canonical script text kept |

Splits/merges vs the script, stated plainly: the transcript never splits a script line;
meet-001 merges the script line with an unscripted leading "Okay."; fpc-003 carries a
duplicated-word echo. No script line was split across turns, and no two script lines
were merged into one segment.

## 3. Live-call tally (Part 1)

2 transcribe calls. Translation + QA calls are counted from the two QA report files
(scale + voa10, uniform config: en->es, max_attempts 3, tolerance 0.10, cps 15,
reasoning_effort low) and reported with the metrics. Part 1 cap: 60 Meta calls total.

Data files in `samples/voa10-scale/` are intentionally left uncommitted for now.

## 4. Metrics capture (per docs/metrics-definitions.md)

```
date: 2026-10-31
commit: aa34bc0 (main)
dataset: voa10-scale (6) + voa10 (6), one uniform config
config: max_attempts=3, tolerance=0.10, cps=15, reasoning_effort=low
reports: qa-report-scale.json, qa-report-voa10.json
scale:  first_pass: 6/6   final_pass: 6/6   revisions: 0
voa10:  first_pass: 5/6   final_pass: 6/6   revisions: 2
meaning: catches=0, not_evaluated=2 (voa10-005 attempts 1-2, timing failed), errors=0
tokens: scale qa=4428 (translation 1205 + meaning 3223); voa10 qa=5819 (translation 2361 + meaning 3458)
scene: not run (out of scope for the scale set; not part of this part)
```

Requested gates (raw counts):

(a) Attempt level — candidates that passed timing and failed meaning, as a share of all
candidates that passed timing: scale 0/6 = 0%; voa10 0/6 = 0%; combined 0/12 = 0%.
(b) Segment level — segments whose first candidate passed timing but failed meaning:
scale 0/6; voa10 0/6; combined 0/12 = 0%.

Meta call tally for Part 1: 2 transcribe + 12 scale QA (6 translate + 6 meaning) +
14 voa10 QA (8 translate attempts + 6 meaning) = 28 of 60 cap. Cap not hit.

Note: voa10-005 needed 2 revisions, both timing-only ("¡Nos vemos pronto!" 2.73s and
"¡Hasta pronto!" 2.47s vs the 2.2s window; "¡Nos vemos!" 2.27s passed). Model
nondeterminism applies; numbers are this run, this config.

## 5. Part 2 capture — live audition validation (branch feat/audition-alternates)

```
date: 2026-10-31
commit: feat/audition-alternates working tree (candidate-pool fix, uncommitted)
dataset: voa10 (6 segments), one audition run via POST /api/audition
config: QaOptions.FromEnvironment (max_attempts=3, tolerance=0.10, cps=15, reasoning_effort=low)
reports: samples/voa10/audition-manifest.json (rewritten), audition-response.json (this folder)
lines_cast: 6/6   takes_synthesized: 7   meta_calls: 14   elevenlabs_characters: 259
caps: meta_cap_hit=false, elevenlabs_cap_hit=false (caps: 40 Meta calls, 3000 chars, 5 takes/segment)
tokens: translation=2091, meaning=3884 (re-added from per-take fields; both match the manifest totals exactly, as does elevenlabs_characters=259)
```

Per line, plainly:
- voa10-001 "Hi, Ashley." — CAST, take 1 (Initial), measured 1.11s vs 1.2s window. Last
  run's 1.39s failure did not repeat; the line cast from its QA candidate, so neither
  the alternate-text route nor the speed variant was needed. TTS nondeterminism, not
  the fix, is what cast it this run.
- voa10-002 — CAST, take 1 (Initial), 3.16s / 4.4s.
- voa10-003 — CAST, take 1 (Initial), 3.95s / 4.4s.
- voa10-004 — CAST, take 1 (Initial), 2.93s / 3.6s.
- voa10-005 — CAST, take 2 (Revision), 2.32s / 2.2s; take 1 failed measured timing (2.46s).
- voa10-006 — CAST, take 1 (Initial), 1.49s / 1.9s.

No segment reached the alternates or speed-variant path in this run (every line had a
winning QA candidate), so the new code paths are validated by the unit tests, not by
this live run. Meta calls: 14 of 40. ElevenLabs: 259 of 3000 characters.
