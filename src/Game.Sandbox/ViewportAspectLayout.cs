namespace DeadGround.Sandbox;

/// <summary>
/// Stretch and aspect-cap parameters computed from a window's pixel dimensions.
/// </summary>
public readonly record struct ViewportStretchSpec(
    int ContentScaleWidth,
    int ContentScaleHeight,
    bool UseAspectCapKeep,
    int LogicalVisibleWidth,
    int LogicalVisibleHeight,
    int ActiveWindowPixelWidth,
    int ActiveWindowPixelHeight,
    float CenteredOffsetX);

/// <summary>
/// Pure layout specification for multi-aspect (4:3, 16:10, 16:9, 21:9) expansion and 21:9 cap.
/// Free of any Godot dependencies so unit tests and client code share one rule.
/// </summary>
public static class ViewportAspectLayout
{
    public const int BaseViewportWidth = 1440;
    public const int BaseViewportHeight = 900;
    public const float MaxAspectRatio = 2.39f;

    public static ViewportStretchSpec ComputeStretchSpec(int windowWidth, int windowHeight)
    {
        int safeW = Math.Max(1, windowWidth);
        int safeH = Math.Max(1, windowHeight);
        float baseAspect = (float)BaseViewportWidth / BaseViewportHeight;
        float windowAspect = (float)safeW / safeH;

        if (windowAspect > MaxAspectRatio)
        {
            int cappedContentW = (int)MathF.Round(BaseViewportHeight * MaxAspectRatio);
            int activePixelW = Math.Min(safeW, Math.Max(1, (int)MathF.Round(safeH * MaxAspectRatio)));
            float offsetX = (safeW - activePixelW) * 0.5f;
            int logicalW = (int)MathF.Round(activePixelW * ((float)BaseViewportHeight / safeH));
            return new ViewportStretchSpec(
                ContentScaleWidth: cappedContentW,
                ContentScaleHeight: BaseViewportHeight,
                UseAspectCapKeep: true,
                LogicalVisibleWidth: logicalW,
                LogicalVisibleHeight: BaseViewportHeight,
                ActiveWindowPixelWidth: activePixelW,
                ActiveWindowPixelHeight: safeH,
                CenteredOffsetX: offsetX);
        }

        if (windowAspect >= baseAspect)
        {
            int logicalW = (int)MathF.Round(BaseViewportHeight * windowAspect);
            return new ViewportStretchSpec(
                ContentScaleWidth: BaseViewportWidth,
                ContentScaleHeight: BaseViewportHeight,
                UseAspectCapKeep: false,
                LogicalVisibleWidth: logicalW,
                LogicalVisibleHeight: BaseViewportHeight,
                ActiveWindowPixelWidth: safeW,
                ActiveWindowPixelHeight: safeH,
                CenteredOffsetX: 0f);
        }

        int logicalH = (int)MathF.Round(BaseViewportWidth / windowAspect);
        return new ViewportStretchSpec(
            ContentScaleWidth: BaseViewportWidth,
            ContentScaleHeight: BaseViewportHeight,
            UseAspectCapKeep: false,
            LogicalVisibleWidth: BaseViewportWidth,
            LogicalVisibleHeight: logicalH,
            ActiveWindowPixelWidth: safeW,
            ActiveWindowPixelHeight: safeH,
            CenteredOffsetX: 0f);
    }
}
