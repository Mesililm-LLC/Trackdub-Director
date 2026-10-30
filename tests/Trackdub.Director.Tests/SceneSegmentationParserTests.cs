using Trackdub.Director.Core;
using Xunit;

namespace Trackdub.Director.Tests;

/// <summary>
/// Token-level tests for <see cref="SceneSegmentationParser"/>. Covers mask association
/// inside a box span: every mask in the span must be well formed, not just the first.
/// </summary>
public sealed class SceneSegmentationParserTests
{
    private const string Box = "<|box;x1=10;y1=10;x2=29;y2=49;w=64;h=64|>";

    [Fact]
    public void Concept_miss_is_not_malformed_and_has_no_regions()
    {
        SegmentationParseResult result = SceneSegmentationParser.Parse("   ", "red rectangle");

        Assert.False(result.IsMalformed);
        Assert.Empty(result.Regions);
    }

    [Fact]
    public void Single_box_with_valid_mask_yields_one_region_with_that_mask()
    {
        SegmentationParseResult result = SceneSegmentationParser.Parse(Box + "<|mask;x=0;y=0;data=40,20|>", "red rectangle");

        Assert.False(result.IsMalformed);
        SceneRegion region = Assert.Single(result.Regions);
        Assert.Equal("40,20", region.MaskData);
    }

    [Fact]
    public void Second_mask_inside_box_span_is_validated_and_malformed_one_fails_the_parse()
    {
        // The second mask has no data= field, so it is malformed even though the first mask is fine.
        SegmentationParseResult result = SceneSegmentationParser.Parse(
            Box + "<|mask;x=0;y=0;data=40,20|><|mask;x=1;y=1|>",
            "red rectangle");

        Assert.True(result.IsMalformed);
        Assert.Contains("missing 'data='", result.Error);
    }

    [Fact]
    public void Valid_extra_masks_inside_box_span_do_not_change_the_region()
    {
        SegmentationParseResult result = SceneSegmentationParser.Parse(
            Box + "<|mask;x=0;y=0;data=40,20|><|mask;x=1;y=1;data=7,8|>",
            "red rectangle");

        Assert.False(result.IsMalformed);
        SceneRegion region = Assert.Single(result.Regions);
        Assert.Equal("40,20", region.MaskData);
    }

    [Fact]
    public void Mask_outside_every_box_span_is_validated_and_malformed_one_fails_the_parse()
    {
        // A mask before the first box is not inside any span, so it goes through unassociated validation.
        SegmentationParseResult result = SceneSegmentationParser.Parse(
            "<|mask;x=1;y=1|>" + Box,
            "red rectangle");

        Assert.True(result.IsMalformed);
        Assert.Contains("missing 'data='", result.Error);
    }
}
