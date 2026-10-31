# Trackdub-Director demo video storyboard (3:00)

Rules carried from the Participant Guide and the asset spec:
- 3 minutes, only work built during the hackathon on screen. No Trackdub
  product UI, no Trackdub-gated shell, ever.
- The opening must separate pre-existing material from event-built work.
  Beat 1 does this on screen, in text.
- No "Llama" wording anywhere. The models are Muse Spark, SAM 3.1,
  Muse Image, Muse Voice Transcribe.
- No catch-rate claim: the uniform scale run found 0 of 12 timing-pass /
  meaning-fail candidates. The demo says so (Beat 8).
- No video-rate sign repaint claim: localisation is frame-level; the one
  insert in Beat 7 is labeled as a frame.
- End card carries the VOA credit and the AI-localized disclosure verbatim.
- Record all UI shots from captured manifests/reports (no live calls during
  recording, so every number on screen is reproducible from the repo).

Total narration: ~380 words at a natural pace. Record voiceover first,
then cut visuals to it.

## Beat 1: Disclosure card (0:00-0:12)

On screen (full-frame text card, no voiceover):

> Built during the Meta AI Developer Hackathon, Oct 26 to Nov 4, 2026.
> Pre-existing material: the VOA Learning English source footage (public
> domain) and ElevenLabs stock voices. The application, its verification
> gates, the test dataset, and every measurement shown were created
> during the event.

## Beat 2: The problem (0:12-0:30)

Visual: the English source clip excerpt, Ashley at the sign
("Anna, Which coffee shop? There are three coffee shops."), then a hard
cut to the app UI.

Voiceover:
"AI dubbing fails quietly. A translated line runs long, or drops the
subject of the sentence, and nothing downstream checks. It just ships.
Studios still pay people to watch dubs and catch this by ear.
Trackdub-Director is that check, as software."

## Beat 3: The two gates (0:30-0:58)

Visual: QA panel over the voa10 report. Highlight per-line verdicts, then
the voa10-005 attempts showing NotEvaluated.

Voiceover:
"Every line is translated by Muse Spark, and then it has to pass two
gates. A timing gate estimates the spoken length against the original
window. A meaning gate judges whether the translation still says what
the original said. Both must pass. If timing fails first, meaning is
recorded as NotEvaluated, and zero tokens are spent on it. A check that
never ran never reads as a pass."

## Beat 4: Measured, not estimated (0:58-1:28)

Visual: audition panel. voa10-005 take 1 FAIL (2.46s vs 2.2s window),
take 2 PASS (2.32s). Then the Oct 30 run's voa10-001 row: single take
1.393s vs 1.32s ceiling, line UNCAST. Caption under it: "A later run
measured the same line at 1.11s and cast it. The gate judges the audio
it is given, every time."

Voiceover:
"Estimates are never trusted with the final decision. Candidate lines
are synthesized, and the real audio is measured. Here, take one measured
2.46 seconds against a 2.2 second window: rejected. Take two measured
2.32, inside tolerance: cast. In an earlier run, the opening line's only
take measured 1.39 against a 1.32 ceiling, and the system refused to
cast it at all. No winner is ever forced."

## Beat 5: Direction from the scene (1:28-1:50)

Visual: scene panel, hero frame with the SAM box on the sign
(449,145)-(751,534), the reflected-sign region visibly not boxed; then
the register adaptation result: "Le veremos en la proxima etapa",
changed: true, both gates Pass.

Voiceover:
"Before translation, the scene is read. SAM 3.1 grounds the coffee shop
sign, and a crop check verified that box is the real sign, not its
reflection in the window. A vision pass reads the scene text and tone.
That context can request a register change, formal or informal, and the
adapted line has to pass both gates again. This one did, and the
manifest records that it changed."

## Beat 6: The sign, localized (1:50-2:12)

Visual: visual localisation before/after viewer, toggling COFFEE SHOP /
CAFETERIA on the hero frame, outside_box_diff: 0 readout visible. Show
the other two validated frames briefly.

Voiceover:
"The sign itself gets localized. Muse Image repaints a crop of the sign
region, and only that region is composited back. Every pixel outside
the box is machine-checked against the source: difference zero, on all
three validated frames. It costs 18 to 36 thousand tokens per frame, so
this is frame-level by design, and the video-rate math is printed in
the repo."

## Beat 7: The dubbed segment (2:12-2:38)

Visual: the full 20-second segment, dubbed (dub-track.wav muxed onto the
cleaned clip). At the sign moment, a 1.5 second insert of the localised
hero frame, captioned: "Sign frame localized (frame-level)".

Voiceover (first 3 seconds only, then the clip plays clean):
"Here is the segment, dubbed."

## Beat 8: Honest numbers and close (2:38-3:00)

Visual: metrics snapshot scrolling (test snapshot, per-lane totals),
then the end card.

Voiceover:
"Honest numbers: 229 automated tests. Across twelve uniform segments,
the meaning gate caught zero silent failures on this material, and the
snapshot in the repo says exactly that. The gates cost about 850 tokens
per segment and leave a verdict for every line. Trackdub-Director: the
dubbing tool that can say no, and show its work."

End card (hold 4 seconds inside the beat):
> Source: VOA Learning English, learningenglish.voanews.com
> AI-localized demonstration: dialogue and scene text have been modified.
> No endorsement implied.
> Voices: ElevenLabs stock synthetic voices. No original performer is
> imitated or cloned.
> Code: github.com/MesilimLLC/Trackdub-Director

## Production notes

- Mux for Beat 7: cleaned nosubs clip + samples/voa10/media/dub-track.wav
  (winning takes at the NOTES.md stamped offsets, already assembled).
  Luna holds the nosubs clip; dub-track.wav lives on Michael's machine.
  Either he uploads dub-track.wav for assembly here, or he runs the mux
  locally: ffmpeg -i coffee-shop-nosubs.mp4 -i dub-track.wav -map 0:v
  -map 1:a -c:v copy -shortest demo-dub.mp4
- UI captures: run the frozen app (main a8eaf06) locally, preload the
  panels from the tracked manifests and reports (audition-manifest.json,
  qa-report-voa10.json, localisation-manifest.json, samples/voa10/NOTES.md
  scene data). No live API calls while recording.
- Capture at 1920x1080. Browser zoom set so verdict text is legible at
  video scale; the numbers that matter (2.46 / 2.32 / 1.39 / diff 0) get
  a highlight box or zoom in edit, not a re-render.
- Music: none. Voiceover plus clip audio only.
- If voiceover runs long in recording, cut Beat 5's last sentence first,
  then Beat 3's NotEvaluated sentence. Beats 1, 4, 7, and 8 do not move.
