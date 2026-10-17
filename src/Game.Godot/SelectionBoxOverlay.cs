using Godot;

namespace DeadGround.Client;

// Draws the translucent selection rectangle during left-button box drag without per-frame allocations.
internal sealed partial class SelectionBoxOverlay : Control
{
    private static readonly Color FillColor = new(0.93f, 0.78f, 0.42f, 0.16f);
    private static readonly Color OutlineColor = new(0.93f, 0.78f, 0.42f, 0.88f);

    private Rect2 _boxRect;

    public Rect2 BoxRect => _boxRect;

    public SelectionBoxOverlay()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Visible = false;
    }

    public void ShowBox(Rect2 rect)
    {
        if (!Visible || _boxRect != rect)
        {
            _boxRect = rect;
            Visible = true;
            QueueRedraw();
        }
    }

    public void HideBox()
    {
        if (Visible)
        {
            Visible = false;
            _boxRect = default;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (!Visible || _boxRect.Size.X <= 0f || _boxRect.Size.Y <= 0f)
            return;

        DrawRect(_boxRect, FillColor, filled: true);
        DrawRect(_boxRect, OutlineColor, filled: false, width: 1.5f);
    }
}
