import { render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi, beforeEach } from "vitest";
import App from "./App";

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
      },
      revisions: [],
      final_verdict: "Pass",
      final_translation: "Hola",
      token_usage: { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 },
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
        },
      ],
      final_verdict: "Pass",
      final_translation: "Accion!",
      token_usage: { prompt_tokens: 22, completion_tokens: 14, total_tokens: 36 },
    },
  ],
  totals: {
    total_segments: 2,
    first_pass_count: 1,
    final_pass_count: 2,
    total_revisions: 1,
    first_pass_rate: 0.5,
    final_pass_rate: 1.0,
    total_tokens: 51,
    prompt_tokens: 32,
    completion_tokens: 19,
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
    // Expand second line to see attempts
    const expandButtons = screen.getAllByText("Show attempts");
    expect(expandButtons.length).toBeGreaterThan(0);
    expandButtons[1].click();
    expect(await screen.findByText("Accion larguisima que no cabe")).toBeInTheDocument();
    expect(screen.getAllByText("Accion!").length).toBeGreaterThanOrEqual(1);
  });
});
