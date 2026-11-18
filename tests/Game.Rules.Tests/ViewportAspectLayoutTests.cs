using DeadGround.Sandbox;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class ViewportAspectLayoutTests
{
    [Theory]
    [InlineData(1440, 900, 1440, 900, false, 0f)]
    [InlineData(1280, 800, 1440, 900, false, 0f)]
    [InlineData(1920, 1080, 1600, 900, false, 0f)]
    [InlineData(3440, 1440, 2150, 900, false, 0f)]
    [InlineData(1440, 1080, 1440, 1080, false, 0f)]
    public void ComputeStretchSpec_ExpandsWithoutBarsUpTo21By9(
        int winW,
        int winH,
        int expectedLogicalW,
        int expectedLogicalH,
        bool expectedKeepCap,
        float expectedOffsetX)
    {
        ViewportStretchSpec spec = ViewportAspectLayout.ComputeStretchSpec(winW, winH);
        Assert.Equal(expectedKeepCap, spec.UseAspectCapKeep);
        Assert.Equal(expectedLogicalW, spec.LogicalVisibleWidth);
        Assert.Equal(expectedLogicalH, spec.LogicalVisibleHeight);
        Assert.Equal(winW, spec.ActiveWindowPixelWidth);
        Assert.Equal(winH, spec.ActiveWindowPixelHeight);
        Assert.Equal(expectedOffsetX, spec.CenteredOffsetX, 2);
    }

    [Fact]
    public void ComputeStretchSpec_Caps32By9WithSymmetricPillarboxBarsAt21By9()
    {
        ViewportStretchSpec spec = ViewportAspectLayout.ComputeStretchSpec(5120, 1440);
        Assert.True(spec.UseAspectCapKeep);
        Assert.Equal((int)MathF.Round(ViewportAspectLayout.BaseViewportHeight * ViewportAspectLayout.MaxAspectRatio), spec.ContentScaleWidth);
        Assert.Equal(ViewportAspectLayout.BaseViewportHeight, spec.ContentScaleHeight);
        Assert.Equal(ViewportAspectLayout.BaseViewportHeight, spec.LogicalVisibleHeight);
        Assert.InRange(spec.ActiveWindowPixelWidth / (float)spec.ActiveWindowPixelHeight, 2.38f, ViewportAspectLayout.MaxAspectRatio + 0.01f);
        Assert.True(spec.CenteredOffsetX > 800f);
        Assert.Equal(5120f, spec.CenteredOffsetX * 2f + spec.ActiveWindowPixelWidth, 1);
    }

    [Theory]
    [InlineData(3440, 1440, 0.8626f, 2)]
    [InlineData(3840, 2160, 0.6667f, 2)]
    [InlineData(6880, 2880, 0.4313f, 2)]
    [InlineData(10240, 4320, 0.3500f, 0)]
    public void AutoRenderScaleAndMsaa_MatchPixelBudgetFormulaAndClamp(
        int width,
        int height,
        float expectedScale,
        int expectedMsaa)
    {
        float scale = DisplaySettings.ResolveRenderScaleFactor(RenderScalePreset.Auto, width, height);
        int msaa = DisplaySettings.ResolveMsaaSamples(AntiAliasingPreset.Auto, width, height, scale);

        Assert.InRange(scale, expectedScale - 0.01f, expectedScale + 0.01f);
        Assert.Equal(expectedMsaa, msaa);
    }
}
