using Godot;

namespace DeadGround.Client;

// Run with an actual renderer. The wrapper also checks the engine log for allocation errors.
public partial class VehicleRenderingProbe : Node3D
{
    private const int Vehicles = 200;
    private const int RestartCycles = 3;
    private const int SlotsPerInstance = 16;
    private const int BytesPerSlot = 16;
    private readonly UnitView?[] _views = new UnitView?[Vehicles];
    private readonly Node3D?[] _retiredRoots = new Node3D?[Vehicles];
    private int _stage;
    private int _delay;
    private int _restarts;
    private int _peakInstances;
    private int _slots;
    private bool _failed;

    public override void _Ready()
    {
        if (DisplayServer.GetName() == "headless")
        {
            Fail("This regression requires a real rendering window, not the headless driver.");
            return;
        }
        _slots = ProjectSettings.GetSetting("rendering/limits/global_shader_variables/buffer_size").AsInt32();
        Camera3D camera = new() { Projection = Camera3D.ProjectionType.Orthogonal, Size = 160, Current = true };
        AddChild(camera);
        camera.Position = new Vector3(100, 180, 240);
        camera.LookAt(new Vector3(100, 0, 50), Vector3.Up);
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-60, -30, 0) });
        SpawnBatch();
        _delay = 3;
    }

    public override void _Process(double delta)
    {
        if (_failed || _delay-- > 0) return;
        try
        {
            if (_stage < 3)
            {
                ShowLod(_stage);
                RecordBudget(LoadedInstances());
                _stage++;
                _delay = 3;
                return;
            }
            if (_restarts > 0)
                foreach (Node3D? root in _retiredRoots)
                    Require(!GodotObject.IsInstanceValid(root), "Deferred restart did not free a retired vehicle.");
            if (_restarts < RestartCycles)
            {
                int retiring = LoadedInstances();
                for (int i = 0; i < Vehicles; i++)
                {
                    Node3D root = _views[i]!.Root;
                    _retiredRoots[i] = root;
                    RemoveChild(root);
                    root.QueueFree();
                    _views[i] = null;
                }
                SpawnBatch();
                for (int lod = 0; lod < 3; lod++) ShowLod(lod);
                RecordBudget(retiring + LoadedInstances());
                _restarts++;
                _stage = 4;
                _delay = 3;
                return;
            }
            GD.Print($"VEHICLE_RENDER_STRESS_OK vehicles={Vehicles} restarts={_restarts} loaded_instances={LoadedInstances()} peak_instances={_peakInstances} slots={_slots} bytes={(long)_slots * BytesPerSlot} renderer={RenderingServer.GetCurrentRenderingMethod()}");
            GetTree().Quit();
            SetProcess(false);
        }
        catch (Exception error) { Fail(error.Message); }
    }

    private void SpawnBatch()
    {
        for (int i = 0; i < Vehicles; i++)
        {
            UnitView view = new(i < Vehicles / 2 ? "t62" : "m60a1", i < Vehicles / 2 ? "soviet" : "us");
            _views[i] = view;
            AddChild(view.Root);
            view.UpdatePosition(new Vector3(i % 20 * 10f, 0, i / 20 * 10f));
        }
    }

    private void ShowLod(int lod)
    {
        float size = lod == 0 ? 30f : lod == 1 ? 60f : 120f;
        foreach (UnitView? item in _views)
        {
            UnitView view = item!;
            view.UpdateLod(size);
            float before = view.GetShaderTrackTravel(true);
            view.UpdatePosition(view.Root.Position + new Vector3(0, 0, -0.25f));
            float after = view.GetShaderTrackTravel(true);
            Require(float.IsFinite(after) && after != before, "Track travel did not update on a loaded LOD.");
            Require(view.ActiveLod == lod, "LOD switch failed.");
        }
    }

    private int LoadedInstances()
    {
        int count = 0;
        foreach (UnitView? view in _views) count += view!.LoadedMotionMeshCount;
        return count;
    }

    private void RecordBudget(int instances)
    {
        _peakInstances = Math.Max(_peakInstances, instances);
        long requiredSlots = (long)instances * SlotsPerInstance;
        Require(requiredSlots + requiredSlots / 4 <= _slots,
            $"Instance shader budget has less than 25% headroom: instances={instances}, required_slots={requiredSlots}, configured_slots={_slots}.");
        GD.Print($"VEHICLE_RENDER_BUDGET instances={instances} required_slots={requiredSlots} configured_slots={_slots}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private void Fail(string message)
    {
        _failed = true;
        GD.PrintErr($"VEHICLE_RENDER_STRESS_FAIL {message}");
        GetTree().Quit(1);
    }
}
