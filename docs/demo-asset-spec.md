# Demo Asset Spec: VOA Lesson 10 Segment Dataset

Prepared 2026-10-28 by Luna. Purpose: the Nov 1 integration day currently assumes a demo-asset dataset that does not exist. This spec defines exactly what to build during asset prep (target: Oct 29 to 30, alongside Director Mode, not Nov 1) so Nov 1 is execution and hardening, not dataset construction.

Status labels used below: VERIFIED means checked against a source or the live repo tonight. TO STAMP means the value must be measured during prep; do not estimate it.

## 1. Source asset

| Field | Value |
|---|---|
| Title | Let's Learn English, Lesson 10: "Come Over to My Place" (VOA Learning English, Level 1) |
| Rights | VOA original production, public domain (not CC0, not CC BY) |
| Credit line (required) | Source: VOA Learning English — learningenglish.voanews.com |
| Disclosure line (required in video and submission) | AI-localized demonstration: dialogue and scene text have been modified. No endorsement implied. |
| Voice rule | Independent licensed synthetic voice only. Never an imitation of the performers. Public-domain footage is not a personality release. |
| Working file (verified in Stage A live run) | `3ec2bbef-f064-4545-bbdd-74f2a695e141_fullhd.mp4`, 1920x1080, 300.1s |
| Integrity | Michael's uploaded copy matched the research-verified SHA-256 (prefix 7082b669). At prep, run a checksum on the working file and record the full value in the dataset notes. Do not copy a full hash from memory. |
| Master segment | 02:35.48 to 02:55.48 (20.00s). Use only the first pass of the lesson; the file repeats material after about 03:22. |
| Sign shot | 02:37.84 to 02:48.24 (10.40 continuous seconds), frontal COFFEE SHOP door sign. Oct 31 visual-localisation target: repaint to CAFETERÍA. |
| Segment caveats | 4 cuts inside the master segment. Two speakers: Ashley on screen at the sign, Anna secondary by telephone. Burned-in English subtitles and a VOA watermark are baked into the picture. |

## 2. Dialogue: what is in the window

Full lesson transcript is published and consistent across multiple mirrors of the VOA lesson script (checked 2026-10-28). The master segment's picture (Ashley at the coffee shop sign, Anna on the phone, sign readable from 02:37.84) matches the second phone call in the script. The candidate line set is therefore that call, in script order:

| # | Speaker | Line |
|---|---|---|
| D1 | Anna | Hi, Ashley. |
| D2 | Ashley | Anna, Which coffee shop? There are three coffee shops. |
| D3 | Anna | Okay, my apartment is across from a big department store. |
| D4 | Ashley | A big department store? Ah, I see it! |
| D5 | Anna | Okay! Bye, Ashley. See you soon! |
| D6 | Ashley | Okay. See you soon. |

CONFIDENCE: high on the set, not yet proven per line. This assignment is inferred from the picture and the script, not from a timed caption track. The stamping pass (section 3) is the verification. Two boundary lines to check while stamping:

- Before D1: the tail of the first call ("Ashley: Okay. See you soon!" then a second "Anna: Hi, Ashley." greeting opens the second call in the script; D1 may be that greeting).
- After D6: the in-person greeting ("Anna: Ashley! Ashley! Ashley! Over here! It's Anna! It's Anna! Hi!") begins once Ashley arrives. If the window's end at 02:55.48 cuts into it, record that as a partial segment; do not silently extend the master segment.

If stamping shows a line does not fall inside 02:35.48 to 02:55.48, drop or add lines to match reality and note the change in the dataset notes. The dataset describes the clip, never the other way round.

## 3. Stamping worksheet (the actual prep work)

For each line, in order, fill in during a listening pass over the extracted clip:

| id | speaker_id | line | start (s, rel. to clip) | end (s) | duration_seconds | frame file | concepts |
|---|---|---|---|---|---|---|---|
| voa10-001 | anna | D1 | TO STAMP | TO STAMP | end minus start | frames/voa10-001.jpg | none |
| voa10-002 | ashley | D2 | TO STAMP | TO STAMP | end minus start | frames/voa10-002.jpg | sign set, section 5 |
| voa10-003 | anna | D3 | TO STAMP | TO STAMP | end minus start | frames/voa10-003.jpg | none |
| voa10-004 | ashley | D4 | TO STAMP | TO STAMP | end minus start | frames/voa10-004.jpg | sign set if sign visible |
| voa10-005 | anna | D5 | TO STAMP | TO STAMP | end minus start | frames/voa10-005.jpg | none |
| voa10-006 | ashley | D6 | TO STAMP | TO STAMP | end minus start | frames/voa10-006.jpg | none |

Rules:
- Stamp to 0.1s precision against the master file's absolute timecode, then convert to clip-relative. Keep both in the notes.
- duration_seconds is measured, never estimated. This matters mechanically: in QaService.ResolveWindowSeconds, an explicit duration_seconds is the QA timing window; without it the window falls back to a characters-per-second estimate of the source line.
- Segment ids are stable once assigned. All lanes join on id.
- Frame per segment: extract at the line's midpoint. Naming: frames/<segment id>.jpg.

## 4. Extraction commands (ffmpeg, asset prep only)

```bash
# Master clip (20.00s)
ffmpeg -ss 155.48 -i source.mp4 -t 20.00 -c copy demo-segment.mp4

# Hero frame at 02:42.00 (already verified once: JPEG, 116,769 bytes, used in the Stage A live run)
ffmpeg -ss 162.00 -i source.mp4 -frames:v 1 frames/hero-0242.jpg

# Per-segment frame at a stamped midpoint <T>
ffmpeg -ss <T> -i source.mp4 -frames:v 1 frames/voa10-00N.jpg

# Verification crop of any SAM region (x, y, w, h from the scene report)
ffmpeg -i frames/<frame>.jpg -vf crop=<w>:<h>:<x>:<y> crop-check.jpg
```

## 5. Scene concepts and the known SAM trap

Only sign-shot segments carry concepts in v1 of this dataset. Everything else gets a frame and no concepts (scene lane returns Empty with regions null and makes no live call).

VERIFIED TRAP (Stage A live run, 2026-10-28): on the 02:42 frame, concept "coffee shop sign" returned exactly one region, box (1545,90) to (1662,140) on 1920x1080, and a crop showed it is the reversed sign reflected in the shop window, not the frontal door sign on the left of frame.

Procedure that follows from that:
1. Try concepts in this order per sign segment: "coffee shop sign", "sign on door", "door sign", "shop sign". One live call per concept; log every call.
2. Crop every returned region and eyeball it. Human pick is a dataset field, not an afterthought: record chosen concept, chosen box, and verified: yes/no in the dataset notes.
3. A region is usable for Oct 31 only when verified: yes. Parsed status alone is not usability (see the metrics sheet: parse rate and target accuracy are separate metrics).

## 6. Data shapes (verified against the repo, branch feat/director-mode, 2026-10-28)

Translation / QA input (TranslationSegmentInput, QaSegmentInput): only three fields exist.

```json
{ "id": "voa10-002", "text": "Anna, Which coffee shop? There are three coffee shops.", "duration_seconds": 3.2 }
```

There is NO speaker field in the translation/QA input. Speaker lives only in the scene input (SceneSegmentInput) as caller-known passthrough. Anything that needs speaker per line joins the two files on segment id. Do not add speaker to the translation input without a code change and a decision.

Scene input (SceneSegmentInput):

```json
{
  "id": "voa10-002",
  "text": "Anna, Which coffee shop? There are three coffee shops.",
  "frame_image_data_url": "data:image/jpeg;base64,...",
  "concepts": ["coffee shop sign"],
  "speaker_id": "ashley",
  "speaker_label": "Ashley"
}
```

Scene output context fields (DirectorContext): speaker_id, speaker_label, emotion_tone_label, on_screen_text, scene_summary, regions. In Stage A everything except regions and caller-supplied speaker is an honest null; B1 is the stage that may fill tone/text/summary.

## 7. Measurements to take during prep (cheap now, expensive Nov 3)

- Subtitle band: on the hero frame, record the burned-in subtitle band's y range (top and bottom pixel rows at 1080p) and x extent. The cover/crop/leave decision is a Nov 3 production call; the measurement is prep work and belongs here.
- Watermark: record the VOA watermark's box on the hero frame.
- Sign box (human): record the frontal door sign's box as you see it on the hero frame. This is the ground truth the Oct 31 accuracy metric is scored against.

## 8. Register-flip line: open choice, flagged not decided

None of the six VOA lines carries much register, idiom, or humour; they are learner English by design. A formal/informal flip on them will be close to invisible in the demo. Options, for Michael to pick:
- Flip D5 ("Okay! Bye, Ashley. See you soon!") anyway and accept a subtle delta, keeping everything on one asset.
- Add one register-rich line from the existing 8-line sample set to the demo dataset as a separate flip exhibit (not presented as VOA dialogue).

This spec does not choose. It only prevents Nov 3 from discovering the problem.

## 9. Prep outputs checklist

- [ ] demo-segment.mp4 (20.00s master clip)
- [ ] segments.json in translation/QA input shape, stamped durations
- [ ] scene-input.json in scene input shape, frames embedded as data URLs
- [ ] frames/ directory, one frame per segment plus hero-0242.jpg
- [ ] Dataset notes: full file checksum, stamped absolute and relative timecodes, SAM concept log with crop-verification flags, subtitle band and watermark measurements, human sign box, any line-set changes from section 2 with reasons
