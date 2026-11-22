using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

// Screen-space bars stay readable at every render scale and camera rotation.
internal sealed partial class UnitReadinessBars : Control
{
    private readonly bool _compact;
    private float _health = -1;
    private WeaponReadinessPresentation _readiness;
    public float HealthFraction => _health;
    public WeaponReadinessPresentation Readiness => _readiness;

    public UnitReadinessBars(bool compact = false)
    {
        _compact = compact;
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = compact ? new Vector2(42, 8) : new Vector2(46, 11);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Resized += QueueRedraw;
    }

    public void Update(float health, WeaponReadinessPresentation readiness)
    {
        health = Math.Clamp(health, 0f, 1f);
        if (_health == health && _readiness == readiness) return;
        _health = health;
        _readiness = readiness;
        QueueRedraw();
    }

    public override void _Draw()
    {
        float width = Size.X;
        if (width <= 1f) return;

        float hpHeight = _compact ? 3f : 4.5f;
        float reloadY = _compact ? 5f : 7.5f;
        float reloadHeight = _compact ? 2f : 2.5f;

        // Crisp dark outline backing keeps frameless overhead bars sharp over bright terrain.
        DrawRect(new Rect2(-1f, -1f, width + 2f, hpHeight + 2f), HudTheme.Outline);
        DrawRect(new Rect2(0, 0, width, hpHeight), HudTheme.Track);
        DrawRect(new Rect2(0, 0, width * Math.Max(0, _health), hpHeight), _health <= 0.25f ? HudTheme.Danger : HudTheme.Text);
        DrawSegmentTicks(width, 0f, hpHeight);

        if (_readiness.State == WeaponReadinessState.Unarmed) return;

        DrawRect(new Rect2(-1f, reloadY - 1f, width + 2f, reloadHeight + 2f), HudTheme.Outline);
        DrawRect(new Rect2(0, reloadY, width, reloadHeight), HudTheme.Track);
        if (_readiness.State == WeaponReadinessState.Empty)
        {
            DrawLine(new Vector2(0, reloadY), new Vector2(width, reloadY + reloadHeight), HudTheme.Danger, 1.2f);
            DrawLine(new Vector2(width, reloadY), new Vector2(0, reloadY + reloadHeight), HudTheme.Danger, 1.2f);
            return;
        }
        DrawRect(new Rect2(0, reloadY, width * _readiness.Progress, reloadHeight), HudTheme.Accent);
        DrawSegmentTicks(width, reloadY, reloadHeight);
    }

    private void DrawSegmentTicks(float width, float y, float height)
    {
        if (width < 36f) return;
        for (int i = 1; i <= 3; i++)
        {
            float x = width * (i * 0.25f);
            DrawLine(new Vector2(x, y), new Vector2(x, y + height), HudTheme.Outline, 1f);
        }
    }
}
