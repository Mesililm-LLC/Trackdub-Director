using System.Text.Json;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class MeaningGateTests
{
    private static readonly MetaClient.TokenUsage Usage = new(10, 5, 15);

    [Fact]
    public void Parse_pass_no_issues_stays_pass()
    {
        string json = JsonSerializer.Serialize(new { verdict = "pass", issues = Array.Empty<string>(), explanation = "Same intent." });
        MeaningCheckResult r = MeaningGate.ParseAndNormalize(json, "muse-spark-1.1", Usage, "api.meta.ai");
        Assert.Equal(MeaningVerdict.Pass, r.Verdict);
        Assert.Empty(r.Issues);
        Assert.Equal(MeaningGate.PromptTemplateVersion, r.PromptTemplateVersion);
        Assert.False(string.IsNullOrWhiteSpace(r.TimestampUtc));
    }

    [Fact]
    public void Parse_fail_preserved()
    {
        string json = JsonSerializer.Serialize(new { verdict = "fail", issues = new[] { "dropped negation" }, explanation = "Negation lost." });
        MeaningCheckResult r = MeaningGate.ParseAndNormalize(json, "muse-spark-1.1", null, "api.meta.ai");
        Assert.Equal(MeaningVerdict.Fail, r.Verdict);
        Assert.Single(r.Issues);
    }

    [Fact]
    public void Normalization_pass_with_issues_becomes_fail()
    {
        string json = JsonSerializer.Serialize(new { verdict = "pass", issues = new[] { "dropped subject" }, explanation = "Claims pass but lists issues" });
        MeaningCheckResult r = MeaningGate.ParseAndNormalize(json, "muse-spark-1.1", null, "api.meta.ai");
        Assert.Equal(MeaningVerdict.Fail, r.Verdict);
        Assert.Single(r.Issues);
    }

    [Fact]
    public void Parse_unparseable_fails_closed()
    {
        MeaningCheckResult r = MeaningGate.ParseAndNormalize("not json at all", "muse-spark-1.1", null, "api.meta.ai");
        Assert.Equal(MeaningVerdict.Fail, r.Verdict);
        Assert.Contains(r.Issues, i => i.Contains("unparseable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_uppercase_verdict_normalized()
    {
        string json = JsonSerializer.Serialize(new { verdict = "PASS", issues = Array.Empty<string>(), explanation = "ok" });
        MeaningCheckResult r = MeaningGate.ParseAndNormalize(json, "muse-spark-1.1", null, "api.meta.ai");
        Assert.Equal(MeaningVerdict.Pass, r.Verdict);
    }

    [Fact]
    public void Parse_wrapped_in_prose_extracted()
    {
        string inner = JsonSerializer.Serialize(new { verdict = "pass", issues = Array.Empty<string>(), explanation = "ok" });
        string raw = $"Here is the judgment: {inner} Thanks!";
        MeaningCheckResult r = MeaningGate.ParseAndNormalize(raw, "muse-spark-1.1", null, "api.meta.ai");
        Assert.Equal(MeaningVerdict.Pass, r.Verdict);
    }

    [Fact]
    public void ForError_creates_error_verdict()
    {
        MeaningCheckResult r = MeaningGate.ForError("API 502 bad gateway", "muse-spark-1.1", null, "api.meta.ai");
        Assert.Equal(MeaningVerdict.Error, r.Verdict);
        Assert.Contains("502", r.Explanation, StringComparison.Ordinal);
        Assert.Equal(MeaningGate.PromptTemplateVersion, r.PromptTemplateVersion);
    }

    [Fact]
    public void System_prompt_contains_material_criteria_and_allows_idiom()
    {
        string prompt = MeaningGate.BuildSystemPrompt();
        Assert.Contains("subject", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("negation", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("modality", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idiom", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MeaningGate.MeaningMarker, prompt, StringComparison.Ordinal);
    }
}
