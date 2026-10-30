# VOA10 demo dataset notes (asset prep, 2026-10-30)

Branch: `main` (B2 PR #5 merge `e670924` present; prep built on top, no code touched).
Scope: data work only. No translation, QA, or director runs; no TTS; no repaint; no commit.

## 1. Source asset and provenance

- Title: Let's Learn English, Lesson 10: "Come Over to My Place" (VOA Learning English, Level 1)
- Rights: VOA original production, public domain (not CC0, not CC BY)
- Credit line (required): Source: VOA Learning English — learningenglish.voanews.com
- Disclosure (required in video and submission): AI-localized demonstration: dialogue and scene text have been modified. No endorsement implied.
- Voice rule: independent licensed synthetic voice only. Never an imitation of the performers.
- Lesson reference (full source file absent, provenance by reference):
  `http://learningenglish.voanews.com/a/lets-learn-english-lesson-10/3285228.html`
  (mirrors also cite `http://learningenglish.voanews.com/p/5644.html`; title verified on both).

### Media on disk (`samples/voa10/media/`, Michael's filenames, DO NOT RENAME)

| File (as on disk) | Probe | Role |
|---|---|---|
| `coffee-shop.mp4` | 20.00s, 1920x1080, h264 + AAC 44.1 kHz stereo, 9,032,443 bytes, SHA-256 `e27ca6cd5c428c246bb4d01a05a9c409a514bc9aa99d61d7cc0863a3593c96e2` (matches known state) | Master 20.00s demo segment (clip of source 02:35.48–02:55.48) |
| `coffee-shop-audio.wav` | 20.02s (20.015625), pcm_s16le 16 kHz mono, 640,578 bytes, SHA-256 `ed9dab805dc26e55b3ef156495aaf3fd07ba95ec3fdce80584b10211721c1b0df` (matches known state) | Stamping probe input (hash-verified; used for the 1 live call) |
| `first-phone-call.mp4` | 13.20s, 1920x1080, h264 + AAC 44.1 kHz stereo, 8,309,499 bytes, SHA-256 `3158dbcc963ac0b481baf9a01a621ab66f6bafddc8b9594ad55d96ac9ef9f22e` | Adjacent material, out of voa10 v1 scope (not the master segment) |
| `first-phone-call-audio.wav` | 13.21s, pcm_s16le 16 kHz mono, 422,868 bytes, SHA-256 `fde1705444e37de8caef3021148a029bd05367823343667cce9babc52bbec5b` | As above (companion audio) |
| `meeting-scene.mp4` | 23.12s, 1920x1080, h264 + AAC 44.1 kHz stereo, 15,921,663 bytes, SHA-256 `10d1d403acc0cd528e923c00a50765a5715936a89ab454133c3551e15bc599` | Adjacent material, out of voa10 v1 scope |
| `meeting-scene-audio.wav` | 23.13s, pcm_s16le 16 kHz mono, 740,144 bytes, SHA-256 `16628d8726dc6af42480740989843b62a818aae0bfd4ef0f1eb4fcd1ea5eede2` | As above (companion audio) |
| `student-driver-flashback.mp4` | 88.93s, 1280x720, h264 + AAC 48 kHz stereo, 15,513,650 bytes, SHA-256 `393a4c4483d2e70f7e4e425b6a0725619fbb736e9a0c1d5c4d659184` | Adjacent material, out of voa10 v1 scope |
| `student-driver-flashback-audio.wav` | 88.94s, pcm_s16le 16 kHz mono, 2,846,244 bytes, SHA-256 `3e37af5fddb3ed603bc9ace2b45ec52b04d270a102f20ac1c9be52` | As above (companion audio) |

Missing by name: the full ~300s source lesson (`3ec2bbef-..._fullhd.mp4` per spec) is
absent — proceeded by reference (URL above). No file matching a `nosubs` variant is
present either — flagged as an Oct 31 dependency (needed for the clean-plate path);
does not block this dataset.

Listening-copy note: no second 44.1 kHz stereo WAV for the coffee-shop segment was
found on disk — all four WAVs probe as 16 kHz mono. The 44.1 kHz stereo listening
copy is therefore treated as absent (the mp4's embedded AAC 44.1 kHz stereo track
exists, but no separate stereo WAV). The 16 kHz file's hash matched, so the probe
went ahead on `coffee-shop-audio.wav`. Filename deviation: on disk the name is
hyphenated (`coffee-shop-audio.wav`), not `coffee shop audio.wav` with spaces.

### Media ignore proof

`.gitignore` line 16 is now `/samples/voa10/media/` (trailing slash: ignored as a
folder). `git status --short` shows only `M .gitignore` and the pre-existing staged
`A IDEA.md` — no media file tracked or staged. `git check-ignore -v` confirms both
`samples/voa10/media/coffee-shop.mp4` and `coffee-shop-audio.wav` are ignored by
that line; `git ls-files samples/voa10/media/` is empty.

## 2. Frame measurements (from `coffee-shop.mp4`, the cleaned clip)

- Hero: `samples/voa10/frames/hero-0242.jpg`, extracted at clip-relative 6.52s
  (source 02:42.00 = clip start 155.48 + 6.52), 1920x1080 JPEG, 211,450 bytes,
  SHA-256 `2f3e4cbba9adcb4d2afdd15eca1cfeb102ff8bd0b19e849e97ae05b764d64dd`.
  This dataset hero supersedes the spec's source-frame hash (the spec's 116,769-byte
  source frame predates the cleaned clip; different bytes, same instant).
- Frontal COFFEE SHOP door-sign box, human ground truth for Oct 31 accuracy scoring
  (1920x1080 pixels): full door panel approx (415,140)-(705,985); sign graphic +
  COFFEE SHOP text approx (449,145)-(751,534). SAM's verified box (see section 4)
  is (449,145)-(751,534), inside the human panel.
- VOA watermark box (1920x1080 pixels): approx (1380,170)-(1830,230), top-right
  "VOA Learning English" overlay.
- Subtitle band: none applies. The cleaned clip carries no burned-in subtitle band
  (crop recipe: top/bottom blurred anonymization bars only, no subtitle text), so no
  band measurement was taken. The spec's band-measurement step is N/A on this clip.

## 3. Stamping probe (exactly 1 live call)

- Endpoint: `POST https://api.meta.ai/v1/asr/transcribe` (multipart `request` JSON +
  `audio` WAV), Authorization Bearer META_API_KEY.
- Request: `{"mode":"DIARIZATION","model":"muse-voice-transcribe-1.0","audioEncoding":"WAV"}`.
- Returned model: not echoed in the body (only `sessionId`
  `voyager/transcribe/FD801267C65842259A52404BE34902D7`); requested model
  `muse-voice-transcribe-1.0` assumed served (HTTP 200, shape matches cookbook).
- Raw shape: top-level keys `audioDurationMs, sessionId, transcript, turns`.
  `audioDurationMs=20080`. No `model`, no `usage`, no token counts (billed per audio
  hour, not tokens — nothing charged to report). No per-word timestamps
  (`words` absent). Per-turn timestamps: each turn has `startMs/endMs` (+ `speaker`
  A/B, `transcript`, `turnId`). So: per-segment timestamps YES, per-word NO.
- Full transcript: "Hi, Ashley. Hello, Anna. Which coffee shop? There are three
  three coffee shops. Okay, my apartment is across from a big department store.
  A big department store? Ah, I see it. Okay, bye Ashley. Ashley. See you soon.
  Okay, see you soon."
- Known garbles disregarded per prompt: "Hello, Anna." for D2's opening "Anna,";
  duplicated "three three" (canonical single "three"); "bye Ashley. Ashley."
  duplication (canonical "Bye, Ashley."). Speaker map: A=Anna, B=Ashley (strict
  alternation A,B,A,B,A,B matches D1–D6).
- Local silence cross-check (ffmpeg silencedetect -30 dB/0.15s on the probe audio):
  speech at 0.42-2.03, 2.25-3.38, 3.62-4.16, 4.44-5.70, 5.90-14.40, 14.86-16.69,
  17.49-17.80, 18.16-18.83. Deviation from the prompt's anchors: one extra
  0.20s pause at ~5.70-5.90 splits the prompt's 4.43-14.41 block; all other
  boundaries agree within ~0.05s.

### Stamped line set (clip-relative s, 0.1s; absolute = +155.48s)

| id | spk | line | clip start-end | absolute start-end | dur | boundary support |
|---|---|---|---|---|---|---|
| voa10-001 | anna | D1 Hi, Ashley. | 0.1-1.3 (turn 0.06-1.26) | 155.6-156.8 | 1.2 | End inside 0.42-2.03 block; start 0.36s before silence end (turn padding/quiet onset), supported by timestamp only |
| voa10-002 | ashley | D2 Anna, Which coffee shop? ... | 1.7-6.1 (turn 1.66-6.06) | 157.1-161.5 | 4.4 | Crosses pauses at 2.03-2.25, 3.38-3.62, 4.16-4.44; tail 4.44-6.06 inside the long block — D2/D3/D4 boundaries stand on transcription timestamps alone |
| voa10-003 | anna | D3 Okay, my apartment ... | 6.5-10.9 (turn 6.46-10.94) | 162.0-166.4 | 4.4 | Fully inside the long block (incl. the extra 5.70-5.90 pause, which falls mid-turn) — timestamps alone |
| voa10-004 | ashley | D4 A big department store? ... | 11.1-14.7 (turn 11.10-14.70) | 166.6-170.2 | 3.6 | Inside the long block; end 14.70 lands in the 14.40-14.86 pause (trailing) — timestamps alone |
| voa10-005 | anna | D5 Okay! Bye, Ashley. ... | 15.1-17.3 (turn 15.10-17.34) | 170.6-172.8 | 2.2 | Start inside 14.86-16.69 block; end 17.34 extends past the 16.69 silence onset into the pause — respects the 16.69-17.49 pause only partially (see deviation) |
| voa10-006 | ashley | D6 Okay. See you soon. | 17.7-19.6 (turn 17.66-19.58) | 173.2-175.1 | 1.9 | Spans 17.49-17.80 + 18.16-18.83 speech with the 17.80-18.16 pause inside the turn; both closing pauses fall inside stamped lines rather than between them |

Stamping VERIFIED (timestamps exist and are usable) — no worksheet fallback needed.

## 4. Dataset files + SAM concept log

- `samples/voa10/segments.json`: 6 rows `{id, text, duration_seconds}` (durations
  above: 1.2/4.4/4.4/3.6/2.2/1.9).
- `samples/voa10/scene-input.json`: same ids/texts, speaker passthrough
  anna/ashley per spec table, one frame per segment at its stamped midpoint
  (0.7/3.9/8.7/12.9/16.2/18.65s) embedded as `data:image/jpeg;base64` URLs,
  concepts `["shop sign"]` on voa10-002 only, `[]` elsewhere.
- Frames: `hero-0242.jpg` (SHA above) + `voa10-001..006.jpg` (SHA-256:
  001 `c93e69cfccbfb9ae8a6119fc9d595d55b2ee0fe706060a6406b5cdd1547a5237`,
  002 `ccd0a69f93769a4bcdcf27a5491de5e0778d8f05be2250af338ae610acca4f43`,
  003 `3dce75618da5bf12dd3e7718bab0e2e9ef01cdef58e70089caa0239d46d00d6f`,
  004 `af191a6711daaa15d4dfd1ca2ab9332878cdf8710586974f9db97ed68687c570`,
  005 `daf10c6a8e2330ce45b737d2c411ecec7739c2378da95266530f6dac104f7694`,
  006 `369e213b9e07a0fd460cb3143ae491a17d9c5e122007ffc1fa454fcf00483661`).

### SAM log (model sam-3.1, `POST /v1/responses`, 0 tokens every call, 1 image each)

Sign-shot scope for v1: voa10-002 only (worksheet). voa10-004 midpoint frame checked:
department-store exterior, no coffee sign — correctly `[]`. Frames voa10-003 (8.7s)
and voa10-006 (18.65s) DO show the coffee sign but carry `[]` in v1 per the worksheet
(scope limit to bound live calls; v2 candidates — recorded as deviation, not error).
The stated 2.36-12.76s sign window is approximate: the picture cuts back to the
coffee shop for D6.

| # | concept | result | boxes (1920x1080) | crop verdict |
|---|---|---|---|---|
| 1 | coffee shop sign | miss (empty output_text) | — | n/a (no region) |
| 2 | sign on door | miss | — | n/a |
| 3 | door sign | miss | — | n/a |
| 4 | shop sign | 4 regions | (1588,395)-(1639,466); (449,145)-(751,534); (1836,190)-(1919,381); (1546,234)-(1663,285) | box1 reflection "five BREW": NO; box2 frontal COFFEE SHOP cup+text: YES (chosen); box3 neighbor-shop edge ("cro/MUSI"): NO; box4 window reflection (yellowish reversed text): NO |

Chosen: concept `shop sign`, box (449,145)-(751,534), verified YES (crop shows the
cyan cup + COFFEE SHOP; covers the sign graphic/text, i.e. the repaint target, not
the full door panel with store hours). The spec's Stage-A trap (a reflection box for
"coffee shop sign") did not reproduce here — the first three concepts missed outright
and the hit came on the fourth concept, whose reflection boxes were correctly
rejected by crop.

## 5. Deviations from the prompt's known state (with reasons)

1. Media filename uses hyphens (`coffee-shop-audio.wav`); prompt quoted spaces.
   Used the files as they exist; no renames.
2. No separate 44.1 kHz stereo WAV on disk (all WAVs are 16 kHz mono); listening
   copy treated as absent. Probe proceeded on hash match.
3. Full source lesson absent (by reference) and nosubs variant absent (Oct 31
   dependency, non-blocking).
4. Hero SHA differs from the spec's source-frame hash (expected: cleaned clip vs
   source bytes; same 02:42 instant, supersedes it).
5. Extra 0.20s silence at ~5.70-5.90 inside the prompt's 4.43-14.41 block (same
   -30 dB/0.15s detector; D3's turn spans it, stamps unaffected).
6. D5 end (17.34) overshoots the 16.69 pause onset; D6 turn contains the 17.80-18.16
   pause internally — closing pauses fall inside lines, not between them. Stamps
   follow the transcription timestamps (the only separator available).
7. v1 concepts on voa10-002 only: voa10-003/006 frames show the sign but are `[]`
   per the worksheet's v1 scope (live-call bound); flagged as v2 additions.

Live-call tally: 1 transcribe + 4 SAM = 5 total. Zero translation/QA/director/TTS calls.
