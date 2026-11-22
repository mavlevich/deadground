using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

internal sealed partial class UnitRoleSymbol : Control
{
    private static readonly Color BackingFill = new(0.043f, 0.043f, 0.046f, 0.72f);

    private ContactClassification _classification;
    private bool _enemy;
    private bool _lastKnown;
    private bool _selected;

    public UnitRoleSymbol()
    {
        CustomMinimumSize = new Vector2(18, 12);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void Update(ContactClassification classification, bool enemy, bool lastKnown, bool selected = false)
    {
        if (_classification == classification && _enemy == enemy && _lastKnown == lastKnown && _selected == selected) return;
        _classification = classification;
        _enemy = enemy;
        _lastKnown = lastKnown;
        _selected = selected;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Color color = _enemy
            ? (_lastKnown ? HudTheme.Danger.Darkened(0.18f) : HudTheme.Danger)
            : (_selected ? HudTheme.Accent : HudTheme.Text);

        if (_enemy)
        {
            Vector2 top = new(9f, 0.5f);
            Vector2 right = new(16f, 6f);
            Vector2 bottom = new(9f, 11.5f);
            Vector2 left = new(2f, 6f);

            // Subtle dark halo behind the diamond for legibility on bright terrain
            DrawLine(top, right, HudTheme.Outline, 3f);
            DrawLine(right, bottom, HudTheme.Outline, 3f);
            DrawLine(bottom, left, HudTheme.Outline, 3f);
            DrawLine(left, top, HudTheme.Outline, 3f);

            if (!_lastKnown)
            {
                DrawRect(new Rect2(6.5f, 3.5f, 5f, 5f), color);
            }
            DrawLine(top, right, color, 1.3f);
            DrawLine(right, bottom, color, 1.3f);
            DrawLine(bottom, left, color, 1.3f);
            DrawLine(left, top, color, 1.3f);
            return;
        }

        Rect2 frame = new(1f, 1f, 16f, 10f);
        DrawRect(frame, BackingFill, filled: true);
        DrawRect(frame.Grow(0.8f), HudTheme.Outline, filled: false, width: 1.4f);
        DrawRect(frame, color, filled: false, width: _selected ? 1.4f : 1.1f);

        switch (_classification)
        {
            case ContactClassification.Tank:
                // Classic NATO armor horizontal stadium/oval
                DrawLine(new Vector2(6f, 3.5f), new Vector2(12f, 3.5f), color, 1.1f);
                DrawLine(new Vector2(6f, 8.5f), new Vector2(12f, 8.5f), color, 1.1f);
                DrawArc(new Vector2(6f, 6f), 2.5f, Mathf.Pi * 0.5f, Mathf.Pi * 1.5f, 8, color, 1.1f);
                DrawArc(new Vector2(12f, 6f), 2.5f, -Mathf.Pi * 0.5f, Mathf.Pi * 0.5f, 8, color, 1.1f);
                break;
            case ContactClassification.Infantry:
                DrawLine(new Vector2(2f, 2f), new Vector2(16f, 10f), color, 1.1f);
                DrawLine(new Vector2(2f, 10f), new Vector2(16f, 2f), color, 1.1f);
                break;
            case ContactClassification.Recon:
                DrawLine(new Vector2(2f, 10f), new Vector2(16f, 2f), color, 1.1f);
                break;
            case ContactClassification.Apc:
                DrawLine(new Vector2(4.5f, 5.5f), new Vector2(13.5f, 5.5f), color, 1.1f);
                DrawCircle(new Vector2(6f, 8f), 1.1f, color);
                DrawCircle(new Vector2(12f, 8f), 1.1f, color);
                break;
            case ContactClassification.CommandVehicle:
                DrawLine(new Vector2(9f, 2f), new Vector2(9f, 10f), color, 1.1f);
                DrawLine(new Vector2(4.5f, 6f), new Vector2(13.5f, 6f), color, 1.1f);
                break;
        }
    }
}
