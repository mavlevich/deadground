using System.IO;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Automated view capture driver for environment profile visual regression and reporting.
/// Frames 7 standardized camera presets on the Ustye battlefield derived from UstyePlanData:
/// 1. 01_wide_map: Full battlefield map overview showing aerial perspective.
/// 2. 02_play_zoom_river_bridge: Central bridge (CROSS-C) at play zoom showing river banks and terrain relief.
/// 3. 03_close_zoom_tank_rifleman: Close inspection framing lead tank and rifleman with ground shadows.
/// 4. 04_forest_edge: Dense woodland border from UstyePlanData showing tree shadows and height fog.
/// 5. 05_combat_flashes_smoke: Combat line with muzzle flash, tracers and burning residue smoke.
/// 6. 06_fog_of_war_borders: Visibility boundary showing seen versus unseen desaturation and relief.
/// 7. 07_low_pitch_horizon_sky: Lowest allowed camera pitch angle showing procedural sky, sun and horizon haze.
/// </summary>
internal sealed class EnvironmentViewCapture
{
    private readonly Main _main;
    private readonly string _outputDir;
    private readonly UstyePlanData _plan;
    private readonly HeightMap _map;
    private readonly CameraRig _cameraRig;
    private readonly CombatEffects? _combatEffects;
    private readonly UnitViewSet _unitViews;
    private readonly IGameSession? _session;

    private int _step;
    private int _framesInStep;
    private Node3D? _stagedRifleman;

    private const int WarmupFrames = 12;
    private const int EffectInjectFrame = 9;

    public EnvironmentViewCapture(
        Main main,
        string outputDir,
        UstyePlanData plan,
        HeightMap map,
        CameraRig cameraRig,
        CombatEffects? combatEffects,
        UnitViewSet unitViews,
        IGameSession? session)
    {
        _main = main;
        _outputDir = outputDir;
        _plan = plan;
        _map = map;
        _cameraRig = cameraRig;
        _combatEffects = combatEffects;
        _unitViews = unitViews;
        _session = session;
        Directory.CreateDirectory(_outputDir);
    }

    public void Process(double delta)
    {
        if (_step < 0 || _step > 6) return;

        _framesInStep++;

        switch (_step)
        {
            case 0: // 01_wide_map
                _cameraRig.ResetOrbit();
                _cameraRig.CenterOn(new Vector3(_plan.ExtentX * 0.5f, 0f, _plan.ExtentZ * 0.5f));
                _cameraRig.Model.TargetSize = 3800f;
                _cameraRig.Model.CurrentSize = 3800f;
                break;

            case 1: // 02_play_zoom_river_bridge
                {
                    UstyePlanData.Crossing cross = _plan.Crossings.Length > 1 ? _plan.Crossings[1] : _plan.Crossings[0];
                    Vector3 bridgePos = new(cross.Center.X, _map.GetHeight(cross.Center.X, cross.Center.Y) + 1.5f, cross.Center.Y);
                    _cameraRig.ResetOrbit();
                    _cameraRig.CenterOn(bridgePos);
                    _cameraRig.Model.TargetSize = 220f;
                    _cameraRig.Model.CurrentSize = 220f;
                    break;
                }

            case 2: // 03_close_zoom_tank_rifleman
                {
                    Vector3 leadPos = _session?.Replica.OwnUnits.Length > 0
                        ? new Vector3(_session.Replica.OwnUnits[0].Position.X, _map.GetHeight(_session.Replica.OwnUnits[0].Position.X, _session.Replica.OwnUnits[0].Position.Y), _session.Replica.OwnUnits[0].Position.Y)
                        : new Vector3(2460f, _map.GetHeight(2460f, 2446f), 2446f);

                    _cameraRig.ResetOrbit();
                    _cameraRig.CenterOn(leadPos + new Vector3(1.2f, 1.0f, 0f));
                    _cameraRig.Model.TargetSize = 32f;
                    _cameraRig.Model.CurrentSize = 32f;

                    if (_framesInStep == 1 && _stagedRifleman is null)
                    {
                        PackedScene? riflemanScene = GD.Load<PackedScene>("res://Assets/Infantry/US/us_rifleman.glb");
                        if (riflemanScene is not null)
                        {
                            _stagedRifleman = riflemanScene.Instantiate<Node3D>();
                            _stagedRifleman.Name = "StagedRifleman";
                            Vector3 soldierPos = leadPos + new Vector3(2.6f, 0f, 0.8f);
                            soldierPos.Y = _map.GetHeight(soldierPos.X, soldierPos.Z);
                            _stagedRifleman.Position = soldierPos;
                            _stagedRifleman.Rotation = new Vector3(0f, Mathf.DegToRad(90f), 0f);

                            AnimationPlayer? anim = _stagedRifleman.FindChild("AnimationPlayer", recursive: true, owned: false) as AnimationPlayer;
                            if (anim is not null)
                            {
                                anim.Play("AimStanding");
                                anim.Advance(0.1);
                            }
                            _main.AddChild(_stagedRifleman);
                        }
                    }
                    break;
                }

            case 3: // 04_forest_edge
                {
                    UstyePlanData.Region? forest = null;
                    foreach (UstyePlanData.Region r in _plan.Regions)
                    {
                        if (r.Kind == "dense_forest")
                        {
                            forest = r;
                            break;
                        }
                    }
                    Vector3 edgeFocus;
                    if (forest is not null && forest.Polygon.Length >= 2)
                    {
                        System.Numerics.Vector2 p1 = forest.Polygon[0];
                        System.Numerics.Vector2 p2 = forest.Polygon[1];
                        System.Numerics.Vector2 edgeMid = (p1 + p2) * 0.5f;
                        edgeFocus = new Vector3(edgeMid.X, _map.GetHeight(edgeMid.X, edgeMid.Y) + 2f, edgeMid.Y);
                    }
                    else
                    {
                        edgeFocus = new Vector3(1750f, _map.GetHeight(1750f, 3100f) + 2f, 3100f);
                    }

                    _cameraRig.ResetOrbit();
                    _cameraRig.CenterOn(edgeFocus);
                    _cameraRig.Model.TargetSize = 130f;
                    _cameraRig.Model.CurrentSize = 130f;
                    break;
                }

            case 4: // 05_combat_flashes_smoke
                {
                    Vector3 combatPos = _session?.Replica.OwnUnits.Length > 0
                        ? new Vector3(_session.Replica.OwnUnits[0].Position.X + 10f, _map.GetHeight(_session.Replica.OwnUnits[0].Position.X + 10f, _session.Replica.OwnUnits[0].Position.Y) + 1.2f, _session.Replica.OwnUnits[0].Position.Y)
                        : new Vector3(2470f, _map.GetHeight(2470f, 2446f) + 1.2f, 2446f);

                    _cameraRig.ResetOrbit();
                    _cameraRig.CenterOn(combatPos);
                    _cameraRig.Model.TargetSize = 95f;
                    _cameraRig.Model.CurrentSize = 95f;

                    if (_framesInStep == EffectInjectFrame)
                    {
                        int tick = _session?.Tick ?? 100;
                        System.Numerics.Vector3 origin = new(combatPos.X - 12f, combatPos.Y + 0.8f, combatPos.Z - 3f);
                        System.Numerics.Vector3 target = new(combatPos.X + 18f, combatPos.Y + 0.8f, combatPos.Z + 4f);
                        System.Numerics.Vector3 wreckPos = new(combatPos.X + 6f, combatPos.Y - 0.5f, combatPos.Z + 10f);

                        _combatEffects?.ProcessEvents(
                        [
                            new CombatEventObservation(1001, tick, CombatEventKind.ShotFired, 101, origin, target),
                            new CombatEventObservation(1002, tick, CombatEventKind.ShotFired, 100, new System.Numerics.Vector3(origin.X + 2f, origin.Y, origin.Z + 2f), target),
                            new CombatEventObservation(1003, tick, CombatEventKind.UnitDestroyed, 102, wreckPos, wreckPos)
                        ], _unitViews, tick);
                    }
                    break;
                }

            case 5: // 06_fog_of_war_borders
                {
                    Vector3 fowPos = _session?.Replica.OwnUnits.Length > 0
                        ? new Vector3(_session.Replica.OwnUnits[0].Position.X + 160f, _map.GetHeight(_session.Replica.OwnUnits[0].Position.X + 160f, _session.Replica.OwnUnits[0].Position.Y), _session.Replica.OwnUnits[0].Position.Y)
                        : new Vector3(2620f, _map.GetHeight(2620f, 2446f), 2446f);

                    _cameraRig.ResetOrbit();
                    _cameraRig.CenterOn(fowPos);
                    _cameraRig.Model.TargetSize = 180f;
                    _cameraRig.Model.CurrentSize = 180f;
                    break;
                }

            case 6: // 07_low_pitch_horizon_sky
                {
                    Vector3 horizonFocus = new(2650f, _map.GetHeight(2650f, 2450f) + 1.0f, 2450f);
                    _cameraRig.CenterOn(horizonFocus);
                    _cameraRig.Model.TargetSize = 350f;
                    _cameraRig.Model.CurrentSize = 350f;

                    float pitchTarget = OrbitCameraPose.MinPitch + 0.05f;
                    float pitchDelta = pitchTarget - _cameraRig.ViewPitch;
                    _cameraRig.Orbit(0f, pitchDelta);
                    break;
                }
        }

        if (_framesInStep >= WarmupFrames)
        {
            string presetName = _step switch
            {
                0 => "01_wide_map",
                1 => "02_play_zoom_river_bridge",
                2 => "03_close_zoom_tank_rifleman",
                3 => "04_forest_edge",
                4 => "05_combat_flashes_smoke",
                5 => "06_fog_of_war_borders",
                6 => "07_low_pitch_horizon_sky",
                _ => $"view_{_step}"
            };

            Image? img = _main.GetViewport().GetTexture().GetImage();
            if (img is not null)
            {
                string outPath = Path.Combine(_outputDir, $"{presetName}.png");
                img.SavePng(outPath);
                GD.Print($"VIEW_CAPTURED {_step}: {presetName} -> {outPath}");
            }

            if (_step == 2 && _stagedRifleman is not null)
            {
                _stagedRifleman.QueueFree();
                _stagedRifleman = null;
            }

            _step++;
            _framesInStep = 0;

            if (_step > 6)
            {
                _cameraRig.ResetOrbit();
                _step = -1;
                GD.Print($"VIEW_CAPTURE_ALL_COMPLETED: 7 presets saved to {_outputDir}");
                _main.GetTree().Quit(0);
            }
        }
    }
}
