using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Sandbox;

/// <summary>
/// A preset observation scenario on the line-of-sight test sandbox.
/// </summary>
public sealed class LosSandboxPreset
{
    public int Id { get; }
    public string Name { get; }
    public string Description { get; }
    public Vector3 Observer { get; }
    public Vector3[] TargetPoints { get; }
    public string[] TargetLabels { get; }
    public ObstructionKind[] ExpectedObstructions { get; }
    public StructureId?[] ExpectedStructureIds { get; }
    public Vector3 CameraFocus { get; }
    public float CameraSize { get; }

    public LosSandboxPreset(
        int id,
        string name,
        string description,
        Vector3 observer,
        Vector3[] targetPoints,
        string[] targetLabels,
        ObstructionKind[] expectedObstructions,
        StructureId?[] expectedStructureIds,
        Vector3 cameraFocus,
        float cameraSize = 64f)
    {
        Id = id;
        Name = name;
        Description = description;
        Observer = observer;
        TargetPoints = targetPoints;
        TargetLabels = targetLabels;
        ExpectedObstructions = expectedObstructions;
        ExpectedStructureIds = expectedStructureIds;
        CameraFocus = cameraFocus;
        CameraSize = cameraSize;
    }
}

/// <summary>
/// Standalone sandbox geometry and test scenarios for line-of-sight validation.
/// Serves as the single source of truth for both regression tests and Godot rendering.
/// </summary>
public static class LosSandboxScenario
{
    public static readonly StructureId BuildingId = new(1);
    public static readonly StructureId LowWallId = new(2);

    /// <summary>
    /// Creates the static geometry containing the prototype terrain,
    /// a house structure (StructureId 1), and a low stone wall (StructureId 2).
    /// </summary>
    public static StaticGeometry CreateGeometry()
    {
        HeightMap heightMap = HeightMap.CreatePrototype();

        // 1. Opaque building (house): width 12m, depth 8m, height 6m (roof at Y=6.0)
        // Positioned in flat sector: X in [80..92], Z in [96..104]
        OpaqueStructure building = new(
            BuildingId,
            new Vector3(80f, 0f, 96f),
            new Vector3(92f, 6.0f, 104f));

        // 2. Low wall: width 14m, depth 1m, height 1.3m (top at Y=1.3)
        // Positioned in flat sector: X in [14..28], Z in [20..21]
        OpaqueStructure lowWall = new(
            LowWallId,
            new Vector3(14f, 0f, 20f),
            new Vector3(28f, 1.3f, 21f));

        return new StaticGeometry(heightMap, [building, lowWall], structureCellSize: 16f);
    }

    /// <summary>
    /// Standard reproducible scenarios covering open field, hill, building, corner peek,
    /// low wall occlusion difference (hull vs turret), and elevated vantage.
    /// </summary>
    public static IReadOnlyList<LosSandboxPreset> CreatePresets()
    {
        return
        [
            // Preset 0: Open Field (Clear)
            new LosSandboxPreset(
                id: 0,
                name: "Open Field",
                description: "Observer and target in open flat terrain. All points clear.",
                observer: new Vector3(15f, 1.0f, 10f),
                targetPoints:
                [
                    new Vector3(15f, 0.5f, 18f),
                    new Vector3(15f, 2.2f, 18f)
                ],
                targetLabels: ["Hull (0.5m)", "Turret (2.2m)"],
                expectedObstructions: [ObstructionKind.None, ObstructionKind.None],
                expectedStructureIds: [null, null],
                cameraFocus: new Vector3(15f, 1f, 14f),
                cameraSize: 32f),

            // Preset 1: Hill Occlusion (Blocked by Terrain)
            // Peak at (86, 44) reaches 9m elevation
            new LosSandboxPreset(
                id: 1,
                name: "Hill Ridge Occlusion",
                description: "Observer at base and target behind hill. Ray blocked by terrain ridge.",
                observer: new Vector3(50f, 1.5f, 44f),
                targetPoints:
                [
                    new Vector3(122f, 1.5f, 44f),
                    new Vector3(122f, 3.2f, 44f)
                ],
                targetLabels: ["Hull (1.5m)", "Turret (3.2m)"],
                expectedObstructions: [ObstructionKind.Terrain, ObstructionKind.Terrain],
                expectedStructureIds: [null, null],
                cameraFocus: new Vector3(86f, 4.5f, 44f),
                cameraSize: 64f),

            // Preset 2: Building Occlusion (Blocked by Structure)
            // Building at X in [80..92], Z in [96..104], height 6m
            new LosSandboxPreset(
                id: 2,
                name: "Building Occlusion",
                description: "Observer directly in front of opaque house, target behind it. Blocked by building.",
                observer: new Vector3(86f, 1.5f, 88f),
                targetPoints:
                [
                    new Vector3(86f, 1.0f, 112f),
                    new Vector3(86f, 2.5f, 112f)
                ],
                targetLabels: ["Hull (1.0m)", "Turret (2.5m)"],
                expectedObstructions: [ObstructionKind.Structure, ObstructionKind.Structure],
                expectedStructureIds: [BuildingId, BuildingId],
                cameraFocus: new Vector3(86f, 3f, 100f),
                cameraSize: 42f),

            // Preset 3: Corner Peek (Stepping Out)
            // Observer shifted sideways to X=106 (past house corner at X=92)
            new LosSandboxPreset(
                id: 3,
                name: "Corner Peek",
                description: "Observer stepped out past building corner. Line of sight opens.",
                observer: new Vector3(106f, 1.5f, 88f),
                targetPoints:
                [
                    new Vector3(86f, 1.0f, 112f),
                    new Vector3(86f, 2.5f, 112f)
                ],
                targetLabels: ["Hull (1.0m)", "Turret (2.5m)"],
                expectedObstructions: [ObstructionKind.None, ObstructionKind.None],
                expectedStructureIds: [null, null],
                cameraFocus: new Vector3(96f, 3f, 100f),
                cameraSize: 42f),

            // Preset 4: Low Wall (Hull Hidden, Turret Visible)
            // Wall at Z in [20..21], height 1.3m. Observer at Z=14, target at Z=28.
            // Ray to Hull (0.5m) crosses wall plane at Y=0.77m < 1.3m -> Blocked by Wall!
            // Ray to Turret (2.3m) crosses wall plane at Y=1.60m > 1.3m -> Clear!
            new LosSandboxPreset(
                id: 4,
                name: "Low Wall Hull-Down",
                description: "Low wall hides lower hull (0.5m) while upper turret (2.3m) remains visible.",
                observer: new Vector3(21f, 1.0f, 14f),
                targetPoints:
                [
                    new Vector3(21f, 0.5f, 28f),
                    new Vector3(21f, 2.3f, 28f)
                ],
                targetLabels: ["Hull (0.5m, Hidden)", "Turret (2.3m, Exposed)"],
                expectedObstructions: [ObstructionKind.Structure, ObstructionKind.None],
                expectedStructureIds: [LowWallId, null],
                cameraFocus: new Vector3(21f, 1f, 21f),
                cameraSize: 32f),

            // Preset 5: Elevated Vantage (High Ground Advantage)
            // Observer on forward slope of hill (elevation 8.25m + 1.6m eye height) looking over valley
            new LosSandboxPreset(
                id: 5,
                name: "Elevated Vantage",
                description: "Observer on forward slope of 9m hill with commanding view over valley.",
                observer: new Vector3(76f, 9.85f, 44f),
                targetPoints:
                [
                    new Vector3(50f, 0.5f, 44f),
                    new Vector3(50f, 2.3f, 44f)
                ],
                targetLabels: ["Valley Target Low (0.5m)", "Valley Target High (2.3m)"],
                expectedObstructions: [ObstructionKind.None, ObstructionKind.None],
                expectedStructureIds: [null, null],
                cameraFocus: new Vector3(63f, 5f, 44f),
                cameraSize: 48f)
        ];
    }
}
