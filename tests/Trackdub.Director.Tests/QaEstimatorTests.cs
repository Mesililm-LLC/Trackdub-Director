using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class QaEstimatorTests
{
    [Fact]
    public void EstimateSeconds_is_length_over_cps()
    {
        Assert.Equal(2.0, DurationEstimator.EstimateSeconds("Hello, world!", 6.5), precision: 6);
        Assert.Equal(0, DurationEstimator.EstimateSeconds(string.Empty, 15));
        Assert.Equal(10.0 / 15.0, DurationEstimator.EstimateSeconds(new string('x', 10), 15), precision: 6);
    }

    [Fact]
    public void Fits_respects_tolerance()
    {
        // window 2.0s, est 2.2s: without tolerance fails, with 10% (allowed 2.2s) passes
        Assert.False(DurationEstimator.Fits(2.2, 2.0, 0));
        Assert.True(DurationEstimator.Fits(2.2, 2.0, 0.10));

        // exact boundary
        Assert.True(DurationEstimator.Fits(2.0, 2.0, 0));
        Assert.False(DurationEstimator.Fits(2.001, 2.0, 0));

        // large tolerance
        Assert.True(DurationEstimator.Fits(5, 2.0, 2.0));
    }

    [Fact]
    public void MaxCharsForWindow_floors()
    {
        Assert.Equal(33, DurationEstimator.MaxCharsForWindow(2.2, 15));
        Assert.Equal(36, DurationEstimator.MaxCharsForWindow(2.2 * 1.10, 15));
        Assert.Equal(0, DurationEstimator.MaxCharsForWindow(0, 15));
    }

    [Fact]
    public void QaOptions_per_language_cps()
    {
        var opts = new QaOptions
        {
            CharactersPerSecondByLanguage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["es"] = 14,
                ["en"] = 16,
            },
        };
        Assert.Equal(14, opts.GetCharactersPerSecond("es"));
        Assert.Equal(16, opts.GetCharactersPerSecond("EN"));
        Assert.Equal(DurationEstimator.DefaultCharactersPerSecond, opts.GetCharactersPerSecond("fr"));
    }

    [Fact]
    public void ResolveWindow_uses_duration_when_present_otherwise_source_estimate()
    {
        var opts = new QaOptions();
        var withWindow = new QaSegmentInput("a", "Hello world", DurationSeconds: 2.5);
        Assert.Equal(2.5, QaService.ResolveWindowSeconds(withWindow, opts, "en"));

        var withoutWindow = new QaSegmentInput("b", "Hello", DurationSeconds: null);
        double expected = DurationEstimator.EstimateSeconds("Hello", opts.GetCharactersPerSecond("en"));
        Assert.Equal(expected, QaService.ResolveWindowSeconds(withoutWindow, opts, "en"), precision: 9);
    }
}
