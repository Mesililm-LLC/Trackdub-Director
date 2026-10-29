import { render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it, vi, beforeEach } from "vitest";
import App from "./App";

// Locate a segment card by its id so gate pills can be asserted per attempt.
function getCard(segId: string): HTMLElement {
  const id = screen.getByText(segId);
  const card = id.closest("article");
  if (!card) throw new Error(`No card found for ${segId}`);
  return card as HTMLElement;
}

// Mock sampleSegments so the test controls what the UI sees before any API call
vi.mock("./sampleSegments.json", () => ({
  default: [
    { id: "seg-001", text: "Hello", duration_seconds: 1.0 },
    { id: "seg-002", text: "Action!", duration_seconds: 0.6 },
  ],
}));

const statusOk = {
  hasKey: true,
  model: "muse-spark-1.1",
  baseUrl: "https://api.meta.ai/v1",
  reasoningEffort: "low",
};

const qaMock = {
  source_language: "en",
  target_language: "es",
  qa_options: {
    max_attempts: 3,
    tolerance_fraction: 0.1,
    target_cps: 15,
    source_cps: 15,
    reasoning_effort: "low",
  },
  segments: [
    {
      id: "seg-001",
      text: "Hello",
      window_seconds: 1.0,
      first_attempt: {
        attempt_number: 1,
        kind: "Initial",
        translation: "Hola",
        estimated_seconds: 0.27,
        window_seconds: 1.0,
        max_chars: 16,
        verdict: "Pass",
        prompt_tokens: 10,
        completion_tokens: 5,
        total_tokens: 15,
        model_id: "muse-spark-1.1",
        timing_verdict: "Pass",
        meaning_verdict: "Pass",
        meaning_issues: [],
        meaning_explanation: "Meaning preserved.",
        meaning_model_id: "muse-spark-1.1",
        meaning_prompt_template_version: "meaning-v1",
        meaning_timestamp_utc: "2026-10-28T00:00:00Z",
        meaning_prompt_tokens: 7,
        meaning_completion_tokens: 4,
        meaning_total_tokens: 11,
        meaning_endpoint_host: "api.meta.ai",
      },
      revisions: [],
      final_verdict: "Pass",
      final_translation: "Hola",
      token_usage: { prompt_tokens: 17, completion_tokens: 9, total_tokens: 26 },
    },
    {
      id: "seg-002",
      text: "Action!",
      window_seconds: 0.6,
      first_attempt: {
        attempt_number: 1,
        kind: "Initial",
        translation: "Accion larguisima que no cabe",
        estimated_seconds: 1.8,
        window_seconds: 0.6,
        max_chars: 9,
        verdict: "Fail",
        prompt_tokens: 10,
        completion_tokens: 8,
        total_tokens: 18,
        model_id: "muse-spark-1.1",
        timing_verdict: "Fail",
        meaning_verdict: "NotEvaluated",
        meaning_issues: null,
        meaning_explanation: null,
        meaning_model_id: null,
        meaning_prompt_template_version: null,
        meaning_timestamp_utc: null,
        meaning_prompt_tokens: 0,
        meaning_completion_tokens: 0,
        meaning_total_tokens: 0,
        meaning_endpoint_host: null,
      },
      revisions: [
        {
          attempt_number: 2,
          kind: "Revision",
          translation: "Accion!",
          estimated_seconds: 0.47,
          window_seconds: 0.6,
          max_chars: 9,
          verdict: "Pass",
          prompt_tokens: 12,
          completion_tokens: 6,
          total_tokens: 18,
          model_id: "muse-spark-1.1",
          timing_verdict: "Pass",
          meaning_verdict: "Pass",
          meaning_issues: [],
          meaning_explanation: "Idiomatic but faithful.",
          meaning_model_id: "muse-spark-1.1",
          meaning_prompt_template_version: "meaning-v1",
          meaning_timestamp_utc: "2026-10-28T00:00:00Z",
          meaning_prompt_tokens: 7,
          meaning_completion_tokens: 4,
          meaning_total_tokens: 11,
          meaning_endpoint_host: "api.meta.ai",
        },
      ],
      final_verdict: "Pass",
      final_translation: "Accion!",
      token_usage: { prompt_tokens: 29, completion_tokens: 18, total_tokens: 47 },
    },
  ],
  totals: {
    total_segments: 2,
    first_pass_count: 1,
    final_pass_count: 2,
    total_revisions: 1,
    first_pass_rate: 0.5,
    final_pass_rate: 1.0,
    total_tokens: 73,
    prompt_tokens: 46,
    completion_tokens: 27,
  },
};

function mockFetchForQa() {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url.includes("/api/status")) {
      return new Response(JSON.stringify(statusOk), { status: 200, headers: { "Content-Type": "application/json" } });
    }
    if (url.includes("/api/qa") && init?.method === "POST") {
      return new Response(JSON.stringify(qaMock), { status: 200, headers: { "Content-Type": "application/json" } });
    }
    if (url.includes("/api/translate")) {
      return new Response(JSON.stringify({ source_language: "en", target_language: "es", segments: [], usage_total: { prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 } }), {
        status: 200,
      });
    }
    return new Response("not found", { status: 404 });
  });
}

describe("App smoke", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", mockFetchForQa());
  });

  it("renders line list from sample and QA summary after Run QA", async () => {
    render(<App />);

    // Lines from sample
    expect(await screen.findByText("seg-001")).toBeInTheDocument();
    expect(screen.getByText("Hello")).toBeInTheDocument();

    // Header shows model from status
    expect(await screen.findByText("muse-spark-1.1")).toBeInTheDocument();

    // Run QA
    const btn = screen.getByRole("button", { name: "Run QA" });
    btn.click();

    await waitFor(() => {
      expect(screen.getByText("Hola")).toBeInTheDocument();
    });

    // QA summary visible
    expect(screen.getByText("First-pass")).toBeInTheDocument();
    // Expand second line to see attempts — now shows Timing and Meaning columns
    const expandButtons = screen.getAllByText("Show attempts");
    expect(expandButtons.length).toBeGreaterThan(0);
    expandButtons[1].click();
    expect(await screen.findByText("Accion larguisima que no cabe")).toBeInTheDocument();
    expect(screen.getAllByText("Accion!").length).toBeGreaterThanOrEqual(1);
    // seg-001 passed first time: its card shows passing gates.
    const seg1 = getCard("seg-001");
    expect(within(seg1).getByText("Timing: Pass")).toBeInTheDocument();
    expect(within(seg1).getByText("Meaning: Pass")).toBeInTheDocument();

    // seg-002 was bounced back and its revision passed: the pill next to the shown translation
    // must describe that final attempt, not the initial one that failed timing.
    const seg2 = getCard("seg-002");
    // Shown both as the card translation and inside the attempt details row.
    expect(within(seg2).getAllByText("Accion!").length).toBeGreaterThanOrEqual(1);
    expect(within(seg2).getByText("Timing: Pass")).toBeInTheDocument();
    expect(within(seg2).getByText("Meaning: Pass")).toBeInTheDocument();
    // The abandoned first attempt is never presented as the card's gate outcome.
    expect(within(seg2).queryByText("Timing: Fail")).not.toBeInTheDocument();
    expect(within(seg2).queryByText("Meaning: Not checked")).not.toBeInTheDocument();
    // The early failing attempt is still visible inside the attempt details.
    expect(within(seg2).getByText("Not checked")).toBeInTheDocument();
    expect(within(seg2).getByText(/Meaning gate \(attempt #2\): model muse-spark-1\.1.*template meaning-v1.*2026-10-28T00:00:00Z/)).toBeInTheDocument();
  });

  it("shows Not checked for a segment whose final attempt failed timing", async () => {
    // A later revision that overruns the window is the displayed attempt, so its skipped
    // meaning check must surface as "Not checked" rather than inheriting the initial Pass.
    const neverFits = {
      ...qaMock,
      segments: [
        {
          ...qaMock.segments[1],
          first_attempt: {
            ...qaMock.segments[1].first_attempt,
            translation: "Hola.",
            timing_verdict: "Pass",
            meaning_verdict: "Pass",
            meaning_explanation: "Meaning preserved.",
            verdict: "Pass",
          },
          revisions: [
            {
              ...qaMock.segments[1].revisions[0],
              translation: "Accion demasiado larga que no cabe",
              timing_verdict: "Fail",
              meaning_verdict: "NotEvaluated",
              meaning_issues: null,
              meaning_explanation: null,
              verdict: "Fail",
            },
          ],
          final_verdict: "Fail",
          final_translation: "Accion demasiado larga que no cabe",
        },
      ],
    };
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL) => {
        const url = typeof input === "string" ? input : input.toString();
        if (url.includes("/api/status")) return new Response(JSON.stringify(statusOk), { status: 200 });
        if (url.includes("/api/qa")) return new Response(JSON.stringify(neverFits), { status: 200 });
        return new Response("not found", { status: 404 });
      }),
    );

    render(<App />);
    screen.getByRole("button", { name: "Run QA" }).click();
    await waitFor(() => expect(screen.getByText("Accion demasiado larga que no cabe")).toBeInTheDocument());

    const card = getCard("seg-002");
    expect(within(card).getByText("Timing: Fail")).toBeInTheDocument();
    expect(within(card).getByText("Meaning: Not checked")).toBeInTheDocument();
    // The initial passing attempt must not be presented as the card's outcome.
    expect(within(card).queryByText("Meaning: Pass")).not.toBeInTheDocument();
  });

  it("shows meaning issues in attempt details when present", async () => {
    const withIssue = {
      ...qaMock,
      segments: [
        {
          ...qaMock.segments[0],
          first_attempt: {
            ...qaMock.segments[0].first_attempt,
            timing_verdict: "Pass",
            meaning_verdict: "Fail",
            meaning_issues: ["dropped modality Necesitamos"],
            meaning_explanation: "Candidate omits obligation.",
            verdict: "Fail",
          },
          final_verdict: "Fail",
          final_translation: "Una toma mas.",
        },
      ],
    };
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL) => {
        const url = typeof input === "string" ? input : input.toString();
        if (url.includes("/api/status")) return new Response(JSON.stringify(statusOk), { status: 200 });
        if (url.includes("/api/qa")) return new Response(JSON.stringify(withIssue), { status: 200 });
        return new Response("not found", { status: 404 });
      }),
    );
    render(<App />);
    const btn = screen.getByRole("button", { name: "Run QA" });
    btn.click();
    await waitFor(() => expect(screen.getByText("Una toma mas.")).toBeInTheDocument());
    const expand = (await screen.findAllByText("Show attempts"))[0];
    expand.click();
    expect(await screen.findByText(/dropped modality/)).toBeInTheDocument();
    expect(screen.getByText(/Candidate omits obligation/)).toBeInTheDocument();
  });

  it("shows read-only scene context after Run Scene", async () => {
    const sceneMock = {
      segments: [
        {
          id: "seg-001",
          status: "Parsed",
          context: {
            speaker_id: "spk-1",
            emotion_tone_label: "cheerful",
            on_screen_text: ["COFFEE SHOP", "STORE HOURS"],
            scene_summary: "A woman stands outside a coffee shop entrance talking on her cell phone while smiling.",
            regions: [{ concept: "red rectangle", x1: 10, y1: 10, x2: 29, y2: 49, frame_width: 64, frame_height: 64, mask_x: 0, mask_y: 0, mask_data: "40,20,abc" }],
            speaker_description: "a smiling woman in a knit beanie and black winter coat talking on a phone",
            burned_in_text: ["VOA Learning English", "Okay. My apartment is across from a big department store."],
          },
          provenance: { model_id: "sam-3.1", endpoint_host: "api.meta.ai", timestamp_utc: "2026-10-29T00:00:00Z", prompt_template_version: "scene-v1" },
          usage: { prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 },
          images_processed: 1,
          frame_id: "hero-0242",
          timecode_seconds: 162,
          frame_sha256: "6c348068c34585f6f8666cff4fa7b03362732e082d72f76adff8023b77523e75",
          vision_provenance: { requested_model_id: "muse-spark-1.1", model_id: "muse-spark-1.1", endpoint_host: "api.meta.ai", timestamp_utc: "2026-10-29T00:00:00Z", prompt_template_version: "scene-vision-v1" },
          vision_usage: { prompt_tokens: 2878, completion_tokens: 756, total_tokens: 3634, reasoning_tokens: 611, cached_tokens: 0 },
        },
        {
          id: "seg-002",
          status: "Empty",
          context: {},
          provenance: { model_id: "sam-3.1", endpoint_host: "api.meta.ai", timestamp_utc: "2026-10-29T00:00:00Z", prompt_template_version: "scene-v1" },
          usage: { prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 },
          images_processed: 0,
        },
      ],
      usage_total: { prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 },
      images_processed_total: 1,
      vision_usage_total: { prompt_tokens: 2878, completion_tokens: 756, total_tokens: 3634, reasoning_tokens: 611, cached_tokens: 0 },
    };
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL) => {
        const url = typeof input === "string" ? input : input.toString();
        if (url.includes("/api/status")) return new Response(JSON.stringify(statusOk), { status: 200 });
        if (url.includes("/api/scene-context")) return new Response(JSON.stringify(sceneMock), { status: 200 });
        return new Response("not found", { status: 404 });
      }),
    );
    render(<App />);
    screen.getByRole("button", { name: "Run Scene" }).click();
    await waitFor(() => expect(screen.getByText("Scene: Parsed")).toBeInTheDocument());

    const seg1 = getCard("seg-001");
    expect(within(seg1).getByText(/Region: red rectangle \(10,10\)-\(29,49\) in 64x64/)).toBeInTheDocument();
    expect(within(seg1).getByText(/Speaker: spk-1/)).toBeInTheDocument();
    // Enriched (B1) fields render read-only, burned-in text distinct from in-scene text.
    expect(within(seg1).getByText(/Emotion\/tone: cheerful/)).toBeInTheDocument();
    expect(within(seg1).getByText(/Speaker description: a smiling woman in a knit beanie/)).toBeInTheDocument();
    expect(within(seg1).getByText(/On-screen text: COFFEE SHOP \| STORE HOURS/)).toBeInTheDocument();
    expect(within(seg1).getByText(/Burned-in text: VOA Learning English/)).toBeInTheDocument();
    expect(within(seg1).getByText(/Vision gate: requested muse-spark-1\.1 · returned muse-spark-1\.1 · template scene-vision-v1/)).toBeInTheDocument();
    expect(within(seg1).getByText(/tokens 3634 \(reasoning 611\)/)).toBeInTheDocument();
    expect(within(seg1).getByText(/Frame: hero-0242 · 162s · sha256 6c348068c345…/)).toBeInTheDocument();
    expect(within(seg1).getByText(/Scene summary: A woman stands outside a coffee shop entrance/)).toBeInTheDocument();
    expect(within(seg1).getByText(/Scene gate: model sam-3\.1 · template scene-v1/)).toBeInTheDocument();

    const seg2 = getCard("seg-002");
    expect(within(seg2).getByText("Scene: Empty")).toBeInTheDocument();
    // Un-evidenced fields still render as unknown, never as invented values.
    expect(within(seg2).getByText(/Scene summary: —/)).toBeInTheDocument();
    expect(within(seg2).getByText(/Burned-in text: —/)).toBeInTheDocument();
  });
});
