using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Pure-function tests for <see cref="DurationEstimator"/>: no model calls and no environment reads.
/// </summary>
public sealed class DurationEstimatorTests
{
    [Fact]
    public void EstimateSeconds_divides_character_count_by_characters_per_second()
    {
        Assert.Equal(2.0, DurationEstimator.EstimateSeconds("abcdef", 3.0), 6);
    }

    [Fact]
    public void EstimateSeconds_returns_zero_for_empty_text()
    {
        Assert.Equal(0.0, DurationEstimator.EstimateSeconds("", 15.0));
    }

    [Fact]
    public void EstimateSeconds_rejects_null_text()
    {
        Assert.Throws<ArgumentNullException>(() => DurationEstimator.EstimateSeconds(null!, 15.0));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void EstimateSeconds_rejects_non_positive_characters_per_second(double cps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurationEstimator.EstimateSeconds("abc", cps));
    }

    [Fact]
    public void EstimateSeconds_by_language_uses_the_configured_rate()
    {
        var options = new QaOptions { CharactersPerSecondByLanguage = new Dictionary<string, double> { ["es"] = 10.0 } };

        Assert.Equal(3.0, DurationEstimator.EstimateSeconds(new string('a', 30), "es", options), 6);
    }

    [Fact]
    public void EstimateSeconds_by_language_falls_back_to_default_rate_without_options()
    {
        Assert.Equal(2.0, DurationEstimator.EstimateSeconds(new string('a', 30), "en"), 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EstimateSeconds_by_language_rejects_blank_language_code(string languageCode)
    {
        Assert.ThrowsAny<ArgumentException>(() => DurationEstimator.EstimateSeconds("abc", languageCode));
    }

    [Fact]
    public void Fits_accepts_an_estimate_exactly_at_the_window()
    {
        Assert.True(DurationEstimator.Fits(4.0, 4.0, 0.0));
    }

    [Fact]
    public void Fits_rejects_an_estimate_just_over_the_window_with_zero_tolerance()
    {
        Assert.False(DurationEstimator.Fits(4.1, 4.0, 0.0));
    }

    [Fact]
    public void Fits_allows_the_tolerance_margin_above_the_window()
    {
        Assert.True(DurationEstimator.Fits(4.3, 4.0, 0.10));
        Assert.False(DurationEstimator.Fits(4.5, 4.0, 0.10));
    }

    [Fact]
    public void Fits_rejects_negative_window()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurationEstimator.Fits(1.0, -1.0, 0.0));
    }

    [Fact]
    public void Fits_rejects_negative_tolerance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurationEstimator.Fits(1.0, 2.0, -0.1));
    }

    [Fact]
    public void MaxCharsForWindow_rounds_down_to_whole_characters()
    {
        Assert.Equal(37, DurationEstimator.MaxCharsForWindow(2.5, 15.0));
    }

    [Fact]
    public void MaxCharsForWindow_rejects_negative_window()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurationEstimator.MaxCharsForWindow(-1.0, 15.0));
    }

    [Fact]
    public void MaxCharsForWindow_rejects_non_positive_characters_per_second()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurationEstimator.MaxCharsForWindow(2.5, 0.0));
    }
}
