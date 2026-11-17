using DeadGround.Engine;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Regression tests for the environment profile system, quality levels,
/// haze zoom scaling, profile switching, and zero allocation performance.
/// </summary>
internal static class EnvironmentProfileRegressionTests
{
    public static void Run(Node3D scene)
    {
        CheckProfilesLoad();
        CheckValidationErrors();
        CheckRequiredFieldsAbsence();
        CheckApplyProfileAndLight(scene);
        CheckRuntimeProfileSwitching(scene);
        CheckHazeScalingAtZoomLevels(scene);
        CheckHazeModeAndDensity(scene);
        CheckHashesUnaffected();
        CheckZeroAllocations(scene);
        CheckQualityLevels(scene);
        CheckPreviewSavedQuality(scene);
        CheckRoadFrontNormalsUpward(scene);
        CheckRegressionFailurePropagation();

        GD.Print("ENVIRONMENT_PROFILE_REGRESSIONS_OK 12/12 passed: load_profiles validation required_fields apply_light runtime_switching haze_scaling haze_mode hashes_unaffected zero_allocations quality_levels preview_saved_quality road_normals");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckProfilesLoad()
    {
        EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
        Require(overcast.Id == "overcast", $"Expected id 'overcast', got '{overcast.Id}'");
        Require(overcast.Sun.Energy > 0f, "Overcast sun energy must be positive.");
        Require(overcast.Haze.Enabled, "Overcast haze must be enabled.");
        Require(overcast.Haze.CloseZoomDensity > overcast.Haze.WideZoomDensity, "Close zoom density must be higher than wide zoom density.");

        EnvironmentProfile clear = EnvironmentProfile.Load("clear_afternoon");
        Require(clear.Id == "clear_afternoon", $"Expected id 'clear_afternoon', got '{clear.Id}'");
        Require(clear.Sun.Energy > 0f, "Clear afternoon sun energy must be positive.");
        Require(clear.Haze.Enabled, "Clear afternoon haze must be enabled.");
    }

    private static void CheckValidationErrors()
    {
        string rawJson = EnvironmentProfile.ReadProfileJson("overcast");
        Require(!string.IsNullOrWhiteSpace(rawJson), "Unable to read overcast.json for validation tests.");

        // 1. Missing required field 'id'
        AssertValidationError(rawJson.Replace("\"id\": \"overcast\",", ""), "id");

        // 2. Out of range sun.pitch
        string badSunPitch = rawJson.Replace("\"pitch\": -48.0,", "\"pitch\": 10.0,");
        AssertValidationError(badSunPitch, "sun.pitch");

        // 3. Out of range sun.energy
        string badSunEnergy = rawJson.Replace("\"energy\": 0.95,", "\"energy\": 25.0,");
        AssertValidationError(badSunEnergy, "sun.energy");

        // 4. Invalid sun.color hex
        string badSunColor = rawJson.Replace("\"color\": \"#dce2e8\",", "\"color\": \"not-a-color\",");
        AssertValidationError(badSunColor, "sun.color");

        // 5. Out of range sky.sun_angle_max
        string badSky = rawJson.Replace("\"sun_angle_max\": 35.0,", "\"sun_angle_max\": 120.0,");
        AssertValidationError(badSky, "sky.sun_angle_max");

        // 6. Invalid ambient.source
        string badAmbient = rawJson.Replace("\"source\": \"Sky\",", "\"source\": \"InvalidSource\",");
        AssertValidationError(badAmbient, "ambient.source");

        // 7. Invalid tonemap.mode
        string badTonemap = rawJson.Replace("\"mode\": \"Filmic\",", "\"mode\": \"UnsupportedTonemap\",");
        AssertValidationError(badTonemap, "tonemap.mode");

        // 8. Out of range haze.density
        string badHazeDensity = rawJson.Replace("\"density\": 0.00014,", "\"density\": 0.8,");
        AssertValidationError(badHazeDensity, "haze.density");

        // 9. haze.depth_end <= haze.depth_begin
        string badHazeDepth = rawJson.Replace("\"depth_begin\": 15.0,", "\"depth_begin\": 5000.0,").Replace("\"depth_end\": 4500.0", "\"depth_end\": 1000.0");
        AssertValidationError(badHazeDepth, "haze.depth_end");
    }

    private static void AssertValidationError(string json, string expectedField)
    {
        try
        {
            EnvironmentProfile.Parse(json, "test_validation");
            throw new InvalidOperationException($"Expected validation error for field '{expectedField}', but parse succeeded.");
        }
        catch (EnvironmentProfileValidationException vex)
        {
            Require(vex.FieldName == expectedField || vex.Message.Contains(expectedField, StringComparison.OrdinalIgnoreCase),
                $"Expected field '{expectedField}', got '{vex.FieldName}': {vex.Message}");
        }
    }

    private static void CheckApplyProfileAndLight(Node3D scene)
    {
        EnvironmentView view = new();
        scene.AddChild(view);
        try
        {
            EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
            view.ApplyProfile(overcast, EnvironmentQuality.High);

            Require(Mathf.IsEqualApprox(view.Sun.LightEnergy, overcast.Sun.Energy),
                $"Sun energy expected {overcast.Sun.Energy}, got {view.Sun.LightEnergy}");
            Require(Mathf.IsEqualApprox(view.Sun.RotationDegrees.X, overcast.Sun.Pitch),
                $"Sun pitch expected {overcast.Sun.Pitch}, got {view.Sun.RotationDegrees.X}");
            Require(Mathf.IsEqualApprox(view.GodotEnvironment.TonemapExposure, overcast.Tonemap.Exposure),
                $"Tonemap exposure expected {overcast.Tonemap.Exposure}, got {view.GodotEnvironment.TonemapExposure}");
            Require(view.GodotEnvironment.FogEnabled == overcast.Haze.Enabled,
                "FogEnabled mismatch.");
            Require(view.GodotEnvironment.SsaoEnabled == true,
                "SSAO should be enabled on High quality.");
        }
        finally
        {
            view.QueueFree();
        }
    }

    private static void CheckRuntimeProfileSwitching(Node3D scene)
    {
        EnvironmentView view = new();
        scene.AddChild(view);
        try
        {
            EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
            EnvironmentProfile clear = EnvironmentProfile.Load("clear_afternoon");

            view.ApplyProfile(overcast);
            int initialChildCount = view.GetChildCount();

            // Switch profiles repeatedly
            for (int i = 0; i < 10; i++)
            {
                view.SetProfile(clear);
                Require(view.ActiveProfile.Id == "clear_afternoon", "Active profile should be clear_afternoon");
                Require(Mathf.IsEqualApprox(view.Sun.LightEnergy, clear.Sun.Energy), "Sun energy should update on switch.");

                view.SetProfile(overcast);
                Require(view.ActiveProfile.Id == "overcast", "Active profile should be overcast");
                Require(Mathf.IsEqualApprox(view.Sun.LightEnergy, overcast.Sun.Energy), "Sun energy should update on switch.");

                Require(view.GetChildCount() == initialChildCount,
                    $"Child node count changed during profile switch: expected {initialChildCount}, got {view.GetChildCount()}");
            }
        }
        finally
        {
            view.QueueFree();
        }
    }

    private static void CheckHazeScalingAtZoomLevels(Node3D scene)
    {
        EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
        float close = overcast.Haze.CloseZoomDensity;
        float wide = overcast.Haze.WideZoomDensity;

        // At close zoom (size <= 120), density is close zoom density
        float dClose = EnvironmentView.CalculateHazeDensity(45f, close, wide);
        Require(Mathf.IsEqualApprox(dClose, close, 1e-6f),
            $"Close zoom density expected {close}, got {dClose}");

        // At wide zoom (size >= 5500), density is wide zoom density
        float dWide = EnvironmentView.CalculateHazeDensity(5500f, close, wide);
        Require(Mathf.IsEqualApprox(dWide, wide, 1e-6f),
            $"Wide zoom density expected {wide}, got {dWide}");

        // At play zoom (size 220), density is intermediate
        float dPlay = EnvironmentView.CalculateHazeDensity(220f, close, wide);
        Require(dPlay < close && dPlay > wide,
            $"Play zoom density must be between {wide} and {close}, got {dPlay}");

        // Readability check: at maximum camera distance on Ustye (~8800m at size 5500),
        // transmission exp(-distance * density) must remain >= 0.60 (not washed out)
        float maxDistance = 8800f;
        float transmission = MathF.Exp(-maxDistance * dWide);
        Require(transmission >= 0.60f,
            $"Wide zoom transmission must be >= 0.60, got {transmission:F3} (haze density {dWide})");
    }

    private static void CheckHashesUnaffected()
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();
        ulong mapHash1 = MatchSetup.ComputeMapHash(geometry);
        ulong contentHash1 = ScenarioContent.LoadCatalog("ustye-ground").ContentHash;
        MatchSetup setup1 = UstyeGroundScenario.CreateMatchSetup(geometry);

        // Load and switch profiles
        EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
        EnvironmentProfile clear = EnvironmentProfile.Load("clear_afternoon");

        ulong mapHash2 = MatchSetup.ComputeMapHash(geometry);
        ulong contentHash2 = ScenarioContent.LoadCatalog("ustye-ground").ContentHash;
        MatchSetup setup2 = UstyeGroundScenario.CreateMatchSetup(geometry);

        Require(mapHash1 == mapHash2, "MapHash must not be affected by environment profile.");
        Require(contentHash1 == contentHash2, "ContentHash must not be affected by environment profile.");
        Require(setup1.MapHash == setup2.MapHash, "MatchSetup MapHash mismatch.");
        Require(setup1.ContentHash == setup2.ContentHash, "MatchSetup ContentHash mismatch.");
    }

    private static void CheckZeroAllocations(Node3D scene)
    {
        EnvironmentView view = new();
        scene.AddChild(view);
        try
        {
            EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
            view.ApplyProfile(overcast);

            // Warmup
            for (int w = 0; w < 15; w++)
            {
                view.UpdateHazeForCamera(220f + w * 10f, Vector3.Zero);
            }

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 100; f++)
            {
                float cameraSize = 100f + f * 50f;
                view.UpdateHazeForCamera(cameraSize, new Vector3(f * 2f, 0f, f * 2f));
            }
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAlloc = allocAfter - allocBefore;

            Require(totalAlloc == 0,
                $"EnvironmentView.UpdateHazeForCamera allocated {totalAlloc} bytes across 100 frames. Must be 0 bytes.");
        }
        finally
        {
            view.QueueFree();
        }
    }

    private static void CheckQualityLevels(Node3D scene)
    {
        EnvironmentView view = new();
        scene.AddChild(view);
        try
        {
            EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
            view.ApplyProfile(overcast, EnvironmentQuality.High);

            // 1. High Quality
            Require(view.ActiveQuality == EnvironmentQuality.High, "Expected High quality.");
            Require(view.Sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits,
                "High quality shadow mode should be Parallel4Splits.");
            Require(view.GodotEnvironment.SsaoEnabled == true, "High quality SSAO should be enabled.");
            Require(Mathf.IsEqualApprox(view.GodotEnvironment.FogAerialPerspective, overcast.Haze.AerialPerspective),
                "High quality aerial perspective mismatch.");
            Require(Mathf.IsEqualApprox(view.Sun.DirectionalShadowMaxDistance, overcast.Sun.ShadowMaxDistance),
                "High quality shadow distance mismatch.");

            // 2. Medium Quality
            view.SetQuality(EnvironmentQuality.Medium);
            Require(view.ActiveQuality == EnvironmentQuality.Medium, "Expected Medium quality.");
            Require(view.Sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel2Splits,
                "Medium quality shadow mode should be Parallel2Splits.");
            Require(view.GodotEnvironment.SsaoEnabled == true, "Medium quality SSAO should be enabled.");
            Require(view.Sun.DirectionalShadowMaxDistance == 1600f, "Medium quality shadow distance should be 1600.");

            // 3. Low Quality
            view.SetQuality(EnvironmentQuality.Low);
            Require(view.ActiveQuality == EnvironmentQuality.Low, "Expected Low quality.");
            Require(view.Sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Orthogonal,
                "Low quality shadow mode should be Orthogonal.");
            Require(view.GodotEnvironment.SsaoEnabled == false, "Low quality SSAO must be disabled.");
            Require(view.GodotEnvironment.FogAerialPerspective == 0f, "Low quality aerial perspective must be 0.");
            Require(view.Sun.DirectionalShadowMaxDistance == 1200f, "Low quality shadow distance should be 1200.");
        }
        finally
        {
            view.QueueFree();
        }
    }

    private static void CheckRequiredFieldsAbsence()
    {
        string rawJson = EnvironmentProfile.ReadProfileJson("overcast");
        Require(!string.IsNullOrWhiteSpace(rawJson), "Unable to read overcast.json for required fields test.");

        // 1. Missing sun.energy (must reject rather than default to 0)
        string noSunEnergy = rawJson.Replace("\"energy\": 0.95,", "");
        AssertValidationError(noSunEnergy, "sun.energy");

        // 2. Missing sun.pitch
        string noSunPitch = rawJson.Replace("\"pitch\": -48.0,", "");
        AssertValidationError(noSunPitch, "sun.pitch");

        // 3. Missing sky.sky_top_color
        string noSkyColor = rawJson.Replace("\"sky_top_color\": \"#485a6c\",", "");
        AssertValidationError(noSkyColor, "sky.sky_top_color");

        // 4. Missing ambient.energy
        string noAmbientEnergy = rawJson.Replace("\"energy\": 0.65", "\"energy_removed\": 0.65");
        AssertValidationError(noAmbientEnergy, "ambient.energy");

        // 5. Missing tonemap.mode
        string noTonemapMode = rawJson.Replace("\"mode\": \"Filmic\",", "");
        AssertValidationError(noTonemapMode, "tonemap.mode");

        // 6. Missing haze.density
        string noHazeDensity = rawJson.Replace("\"density\": 0.00014,", "");
        AssertValidationError(noHazeDensity, "haze.density");

        // 7. Missing material_tints.terrain
        string noTerrainTint = rawJson.Replace("\"terrain\": \"#ffffff\",", "");
        AssertValidationError(noTerrainTint, "material_tints.terrain");
    }

    private static void CheckHazeModeAndDensity(Node3D scene)
    {
        EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
        Require(string.Equals(overcast.Haze.Mode, "Exponential", StringComparison.OrdinalIgnoreCase),
            $"Expected haze mode 'Exponential', got '{overcast.Haze.Mode}'");

        EnvironmentView view = new();
        scene.AddChild(view);
        try
        {
            view.ApplyProfile(overcast, EnvironmentQuality.High);
            Require(view.GodotEnvironment.FogMode == Godot.Environment.FogModeEnum.Exponential,
                $"Expected FogMode Exponential, got {view.GodotEnvironment.FogMode}");
            Require(Mathf.IsEqualApprox(view.GodotEnvironment.FogDensity, overcast.Haze.Density),
                $"Expected FogDensity {overcast.Haze.Density}, got {view.GodotEnvironment.FogDensity}");
        }
        finally
        {
            view.QueueFree();
        }
    }

    private static void CheckPreviewSavedQuality(Node3D scene)
    {
        DisplaySettings originalConfig = SettingsPanel.LoadConfig();
        try
        {
            SettingsPanel.SaveConfig(originalConfig with { Quality = EnvironmentQuality.Low });

            // Case 1: Without CLI override, saved Low quality is applied
            UstyeMapPreview previewWithoutOverride = new([]);
            scene.AddChild(previewWithoutOverride);
            previewWithoutOverride.SetProcess(false);
            try
            {
                Require(previewWithoutOverride.EnvironmentView.ActiveQuality == EnvironmentQuality.Low,
                    $"UstyeMapPreview must apply saved Low quality when no CLI override is passed (got {previewWithoutOverride.EnvironmentView.ActiveQuality}).");
            }
            finally
            {
                previewWithoutOverride.QueueFree();
            }

            // Case 2: With CLI override --quality high, High quality is applied regardless of saved Low
            UstyeMapPreview previewWithOverride = new(["--quality", "high"]);
            scene.AddChild(previewWithOverride);
            previewWithOverride.SetProcess(false);
            try
            {
                Require(previewWithOverride.EnvironmentView.ActiveQuality == EnvironmentQuality.High,
                    $"UstyeMapPreview must apply CLI override High even when Low is saved (got {previewWithOverride.EnvironmentView.ActiveQuality}).");
            }
            finally
            {
                previewWithOverride.QueueFree();
            }
        }
        finally
        {
            SettingsPanel.SaveConfig(originalConfig);
        }
    }

    private static void CheckRoadFrontNormalsUpward(Node3D scene)
    {
        UstyePlanData plan = UstyePlanData.Load();
        UstyeMapView mapView = new(plan, includeScaleVehicles: false);
        scene.AddChild(mapView);
        try
        {
            Require(mapView.HasUpwardRoadFrontFaces(),
                "UstyeMapView roads must have upward-facing front faces.");

            Require(mapView.RoadsRoot.GetChildCount() > 0, "RoadsRoot must contain road meshes.");
            foreach (Node child in mapView.RoadsRoot.GetChildren())
            {
                if (child is MeshInstance3D mi && mi.Mesh is ArrayMesh mesh && mesh.GetSurfaceCount() > 0)
                {
                    using Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
                    Vector3[] verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    int[] idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                    Require(idx.Length >= 3, "Road mesh must contain triangles.");

                    for (int i = 0; i < idx.Length; i += 3)
                    {
                        Vector3 a = verts[idx[i]];
                        Vector3 b = verts[idx[i + 1]];
                        Vector3 c = verts[idx[i + 2]];
                        Vector3 clockwiseNormal = (c - a).Cross(b - a).Normalized();
                        float dotUp = clockwiseNormal.Dot(Vector3.Up);
                        Require(dotUp > 0.85f,
                            $"Road front face normal must point upward into the sky (got dotUp={dotUp:F3}).");
                    }
                }
            }
        }
        finally
        {
            mapView.QueueFree();
        }
    }

    private static void CheckRegressionFailurePropagation()
    {
        bool caught = false;
        try
        {
            Require(false, "Intentional failure to test propagation.");
        }
        catch (InvalidOperationException)
        {
            caught = true;
        }
        Require(caught, "Regression assertions must propagate InvalidOperationException.");
    }
}
