using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Tests for <see cref="QaOptions"/> defaults, per-language rate lookup and environment parsing.
/// Environment variables are process-wide; test classes run one at a time (see xunit.runner.json),
/// and each test restores what it changed.
/// </summary>
public sealed class QaOptionsTests
{
    private static readonly string[] EnvNames =
    [
        "META_REASONING_EFFORT", "QA_MAX_ATTEMPTS", "QA_TOLERANCE",
        "QA_CPS_EN", "QA_CPS_ES", "QA_CPS_FR", "QA_CPS_DE", "QA_CPS_PT",
        "QA_CPS_IT", "QA_CPS_JA", "QA_CPS_KO", "QA_CPS_ZH",
    ];

    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public EnvScope(params (string Name, string? Value)[] overrides)
        {
            foreach (string name in EnvNames)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }

            foreach ((string name, string? value) in overrides)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> entry in _previous)
            {
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            }
        }
    }

    [Fact]
    public void Defaults_match_the_documented_values()
    {
        var options = new QaOptions();

        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(0.10, options.ToleranceFraction);
        Assert.Equal("low", options.ReasoningEffort);
        Assert.Empty(options.CharactersPerSecondByLanguage);
    }

    [Fact]
    public void GetCharactersPerSecond_returns_the_override_for_a_language_ignoring_case_and_whitespace()
    {
        var options = new QaOptions { CharactersPerSecondByLanguage = new Dictionary<string, double> { ["es"] = 12.5 } };

        Assert.Equal(12.5, options.GetCharactersPerSecond(" ES "));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-4.0)]
    public void GetCharactersPerSecond_ignores_non_positive_overrides(double cps)
    {
        var options = new QaOptions { CharactersPerSecondByLanguage = new Dictionary<string, double> { ["es"] = cps } };

        Assert.Equal(DurationEstimator.DefaultCharactersPerSecond, options.GetCharactersPerSecond("es"));
    }

    [Fact]
    public void GetCharactersPerSecond_returns_default_for_an_unconfigured_language()
    {
        Assert.Equal(DurationEstimator.DefaultCharactersPerSecond, new QaOptions().GetCharactersPerSecond("fr"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void GetCharactersPerSecond_rejects_blank_language_code(string languageCode)
    {
        Assert.ThrowsAny<ArgumentException>(() => new QaOptions().GetCharactersPerSecond(languageCode));
    }

    [Fact]
    public void FromEnvironment_uses_defaults_when_nothing_is_set()
    {
        using var env = new EnvScope();

        QaOptions options = QaOptions.FromEnvironment();

        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(0.10, options.ToleranceFraction);
        Assert.Equal("low", options.ReasoningEffort);
        Assert.Empty(options.CharactersPerSecondByLanguage);
    }

    [Fact]
    public void FromEnvironment_reads_valid_overrides()
    {
        using var env = new EnvScope(
            ("META_REASONING_EFFORT", "  High "),
            ("QA_MAX_ATTEMPTS", "5"),
            ("QA_TOLERANCE", "0.25"),
            ("QA_CPS_ES", " 12.5 "),
            ("QA_CPS_EN", "14"));

        QaOptions options = QaOptions.FromEnvironment();

        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(0.25, options.ToleranceFraction);
        Assert.Equal("high", options.ReasoningEffort);
        Assert.Equal(12.5, options.CharactersPerSecondByLanguage["es"]);
        Assert.Equal(14.0, options.CharactersPerSecondByLanguage["en"]);
    }

    [Fact]
    public void FromEnvironment_falls_back_to_low_for_an_unsupported_reasoning_effort()
    {
        using var env = new EnvScope(("META_REASONING_EFFORT", "turbo"));

        Assert.Equal("low", QaOptions.FromEnvironment().ReasoningEffort);
    }

    [Fact]
    public void FromEnvironment_falls_back_to_low_for_a_blank_reasoning_effort()
    {
        using var env = new EnvScope(("META_REASONING_EFFORT", "   "));

        Assert.Equal("low", QaOptions.FromEnvironment().ReasoningEffort);
    }

    [Fact]
    public void FromEnvironment_ignores_invalid_numeric_values()
    {
        using var env = new EnvScope(
            ("QA_MAX_ATTEMPTS", "-1"),
            ("QA_TOLERANCE", "-0.1"),
            ("QA_CPS_ES", "-3"),
            ("QA_CPS_FR", "fast"),
            ("QA_CPS_DE", "0"));

        QaOptions options = QaOptions.FromEnvironment();

        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(0.10, options.ToleranceFraction);
        Assert.Empty(options.CharactersPerSecondByLanguage);
    }

    [Fact]
    public void FromEnvironment_ignores_non_integer_max_attempts()
    {
        using var env = new EnvScope(("QA_MAX_ATTEMPTS", "many"));

        Assert.Equal(3, QaOptions.FromEnvironment().MaxAttempts);
    }
}
