using Godot;

namespace DeadGround.Client;

/// <summary>
/// Color palette, dimensions, and drawing styles for the tactical minimap,
/// styled as a neutral black-graphite radar-topographic screen for OLED displays.
/// </summary>
public static class TacticalMinimapTheme
{
    // Palette: Neutral black-graphite radar-topographic screen matching HudTheme panels
    public static readonly Color LandColor = HudTheme.PanelSolid; // Deep neutral obsidian (#0d0d0e)
    public static readonly Color LandContourColor = new(0.130f, 0.130f, 0.135f); // Subtle elevation contour lines
    public static readonly Color LandContourIndexColor = new(0.185f, 0.185f, 0.192f); // Index contour lines
    public static readonly Color WaterColor = new(0.095f, 0.145f, 0.170f); // Muted dark petrol water
    public static readonly Color WaterShorelineColor = new(0.060f, 0.095f, 0.115f); // Shoreline
    public static readonly Color UrbanColor = new(0.165f, 0.165f, 0.170f); // Neutral graphite urban blocks
    public static readonly Color UrbanBorderColor = new(0.070f, 0.070f, 0.073f); // Urban block borders
    public static readonly Color RoadColor = new(0.470f, 0.460f, 0.440f); // Neutral stone-sand road tracks
    public static readonly Color RoadCasingColor = new(0.070f, 0.070f, 0.073f); // Road casing
    public static readonly Color CrossingColor = HudTheme.Accent; // Unified amber crossings
    public static readonly Color CrossingBorderColor = HudTheme.Background; // Crossing border

    // Frame, fields, and optical reticle accents
    public static readonly Color WidgetBackground = HudTheme.Panel;
    public static readonly Color FrameBorder = HudTheme.Border;
    public static readonly Color FieldColor = HudTheme.Background;
    public static readonly Color GridLineColor = new(0.55f, 0.55f, 0.56f, 0.12f);
    public static readonly Color ReticleCrosshairColor = new(HudTheme.Accent, 0.18f);
    public static readonly Color CornerTickColor = HudTheme.Accent;

    // Camera view frustum
    public static readonly Color CameraFrustumOutline = HudTheme.Accent;
    public static readonly Color CameraFrustumFill = new(HudTheme.Accent, 0.09f);

    // Unit and contact markers
    public static readonly Color FriendlyColor = HudTheme.Text;
    public static readonly Color FriendlyBorderColor = HudTheme.Background;
    public static readonly Color FriendlySelectedRing = HudTheme.Accent;
    public static readonly Color EnemyCurrentColor = HudTheme.Danger;
    public static readonly Color EnemyCurrentBorderColor = HudTheme.Background;
    public static readonly Color EnemyLastKnownColor = HudTheme.Danger.Darkened(0.12f);

    // Dimensions
    public const float CrossingMinimumTextureWidth = 7f;
    public const float FriendlyMarkerRadius = 3.5f;
    public const float SelectedRingRadius = 6.0f;
    public const float SelectedRingWidth = 1.8f;
    public const float EnemyDiamondRadius = 4.5f;
    public const float EnemyLastKnownHalfSize = 4.0f;
    public const float EnemyLastKnownLineWidth = 1.6f;
    public const float CameraFrustumLineWidth = 1.8f;
    public const float CornerTickLength = 9.0f;
    public const float HoverHitRadius = 8.0f;
}
