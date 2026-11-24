using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Variant A pure-C# cosmetic concrete debris physics rendered via GPU MultiMesh batches.
/// Uses local client-only RNG (never touches match RNG streams), analytic HeightMap + rubble-floor
/// bounce with damping and automatic sleeping, zero RigidBody3D/PhysicsServer3D nodes, explicit
/// CustomAabb + VisibleInstanceCount culling (no -10000 hidden transforms), and zero steady-state
/// heap allocations.
/// </summary>
internal sealed class DebrisPhysicsPool : IDisposable
{
    internal const int TotalCapacity = 384;
    private const int SlabACapacity = 144;
    private const int SlabBCapacity = 120;
    private const int ChunkCCapacity = 120;
    private const float Gravity = 9.81f;
    private const float SleepSpeedSq = 0.09f; // 0.3 m/s
    private const float SleepSpinSq = 0.16f;  // 0.4 rad/s

    private static readonly Aabb PoolCustomAabb = new(new Vector3(-2500f, -50f, -2500f), new Vector3(5000f, 350f, 5000f));

    private struct Particle
    {
        public bool Active;
        public bool Sleeping;
        public byte Kind; // 0 = SlabA, 1 = SlabB, 2 = ChunkC
        public int BatchSlot;
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 RotationEuler;
        public Vector3 AngularVelocity;
        public float Scale;
        public float HalfExtentY;
        public float FloorMinY;
        public float Age;
    }

    private readonly Node3D _root;
    private readonly MultiMeshInstance3D[] _batches = new MultiMeshInstance3D[3];
    private readonly MultiMesh[] _multiMeshes = new MultiMesh[3];
    private readonly int[] _batchCapacities = [SlabACapacity, SlabBCapacity, ChunkCCapacity];
    private readonly int[] _batchVisibleCounts = new int[3];
    private readonly int[] _batchCursors = new int[3];
    private readonly Particle[] _particles = new Particle[TotalCapacity];
    private readonly Random _localRng;
    private bool _disposed;

    internal int ActiveCount { get; private set; }
    internal int AwakeCount { get; private set; }
    internal int SleepingCount => ActiveCount - AwakeCount;

    internal DebrisPhysicsPool(Node3D parent, Mesh? slabAMesh, Mesh? slabBMesh, Mesh? chunkCMesh, int localSeed = 14641)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _localRng = new Random(localSeed);
        _root = new Node3D { Name = "BuildingDebrisPool" };
        parent.AddChild(_root);

        Mesh fallbackSlabA = slabAMesh ?? CreateFallbackBox(new Vector3(1.45f, 0.22f, 0.95f), new Color("9e9a8f"));
        Mesh fallbackSlabB = slabBMesh ?? CreateFallbackBox(new Vector3(0.95f, 0.26f, 0.68f), new Color("8b867c"));
        Mesh fallbackChunkC = chunkCMesh ?? CreateFallbackBox(new Vector3(0.48f, 0.38f, 0.44f), new Color("7f7b72"));
        Mesh[] meshes = [fallbackSlabA, fallbackSlabB, fallbackChunkC];

        int particleIndex = 0;
        for (int kind = 0; kind < 3; kind++)
        {
            int cap = _batchCapacities[kind];
            MultiMesh mm = new()
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = cap,
                VisibleInstanceCount = 0,
                Mesh = meshes[kind]
            };
            for (int slot = 0; slot < cap; slot++)
            {
                mm.SetInstanceTransform(slot, Transform3D.Identity);
                _particles[particleIndex].Kind = (byte)kind;
                _particles[particleIndex].BatchSlot = slot;
                particleIndex++;
            }

            MultiMeshInstance3D mmi = new()
            {
                Name = kind switch { 0 => "DebrisSlabA", 1 => "DebrisSlabB", _ => "DebrisChunkC" },
                Multimesh = mm,
                CustomAabb = PoolCustomAabb,
                CastShadow = kind == 0 ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off
            };
            _root.AddChild(mmi);
            _multiMeshes[kind] = mm;
            _batches[kind] = mmi;
        }
    }

    internal void SpawnBurst(
        Vector3 origin,
        Vector3 outwardBias,
        float spreadRadius,
        int count,
        float minSpeed,
        float maxSpeed,
        float rubbleFloorY,
        bool includeLargeSlabs = true)
    {
        if (_disposed || count <= 0)
            return;

        Vector3 biasDir = outwardBias.LengthSquared() > 1e-4f ? outwardBias.Normalized() : Vector3.Up;
        bool isWallImpact = MathF.Abs(biasDir.Y) < 0.55f && (biasDir.X * biasDir.X + biasDir.Z * biasDir.Z) > 0.25f;
        Vector2 wallNormalXZ = isWallImpact
            ? new Vector2(biasDir.X, biasDir.Z).Normalized()
            : Vector2.Zero;

        for (int i = 0; i < count; i++)
        {
            byte kind = PickKind(includeLargeSlabs);
            int pIdx = AllocateParticle(kind);
            ref Particle p = ref _particles[pIdx];

            if (!p.Active)
            {
                p.Active = true;
                ActiveCount++;
                AwakeCount++;
                if (p.BatchSlot + 1 > _batchVisibleCounts[kind])
                {
                    _batchVisibleCounts[kind] = p.BatchSlot + 1;
                    _multiMeshes[kind].VisibleInstanceCount = _batchVisibleCounts[kind];
                }
            }
            else if (p.Sleeping)
            {
                p.Sleeping = false;
                AwakeCount++;
            }

            float angle = (float)(_localRng.NextDouble() * Math.Tau);
            float radialX = MathF.Cos(angle);
            float radialZ = MathF.Sin(angle);

            Vector3 spawnPos;
            Vector3 velocityDir;

            if (isWallImpact)
            {
                // Project radial scatter into the outward hemisphere away from the struck wall
                // so shell debris blasts outward into the street rather than through the building interior.
                float dot = radialX * wallNormalXZ.X + radialZ * wallNormalXZ.Y;
                if (dot < 0.15f)
                {
                    float push = 0.30f - dot;
                    radialX += wallNormalXZ.X * push;
                    radialZ += wallNormalXZ.Y * push;
                    float len = MathF.Sqrt(radialX * radialX + radialZ * radialZ);
                    if (len > 1e-4f)
                    {
                        radialX /= len;
                        radialZ /= len;
                    }
                }

                float r = (float)Math.Sqrt(_localRng.NextDouble()) * spreadRadius;
                float jitterY = (float)(_localRng.NextDouble() * 1.2 - 0.3);
                spawnPos = new Vector3(
                    origin.X + wallNormalXZ.X * 0.35f + radialX * r,
                    MathF.Max(rubbleFloorY + 0.35f, origin.Y + jitterY),
                    origin.Z + wallNormalXZ.Y * 0.35f + radialZ * r);

                Vector3 randomDir = new(
                    radialX * (0.55f + (float)_localRng.NextDouble() * 0.45f),
                    0.18f + (float)_localRng.NextDouble() * 0.68f,
                    radialZ * (0.55f + (float)_localRng.NextDouble() * 0.45f));
                velocityDir = (randomDir + biasDir * 0.75f).Normalized();
            }
            else
            {
                // Collapse burst: spawn along outer perimeter annulus and scatter radially outward
                float annulusT = 0.55f + 0.50f * (float)_localRng.NextDouble();
                float r = spreadRadius * annulusT;
                float jitterY = (float)(_localRng.NextDouble() * 1.4 - 0.35);
                spawnPos = new Vector3(
                    origin.X + radialX * r,
                    MathF.Max(rubbleFloorY + 0.35f, origin.Y + jitterY),
                    origin.Z + radialZ * r);

                Vector3 outwardDir = new(
                    radialX * (0.55f + (float)_localRng.NextDouble() * 0.45f),
                    0.22f + (float)_localRng.NextDouble() * 0.78f,
                    radialZ * (0.55f + (float)_localRng.NextDouble() * 0.45f));
                velocityDir = (outwardDir + biasDir * 0.45f).Normalized();
            }

            p.Position = spawnPos;
            float speed = minSpeed + (float)_localRng.NextDouble() * (maxSpeed - minSpeed);
            p.Velocity = velocityDir * speed;
            p.RotationEuler = new Vector3(
                (float)(_localRng.NextDouble() * Math.Tau),
                (float)(_localRng.NextDouble() * Math.Tau),
                (float)(_localRng.NextDouble() * Math.Tau));
            float spinScale = kind == 0 ? 3.2f : (kind == 1 ? 4.8f : 6.8f);
            p.AngularVelocity = new Vector3(
                ((float)_localRng.NextDouble() * 2f - 1f) * spinScale,
                ((float)_localRng.NextDouble() * 2f - 1f) * spinScale,
                ((float)_localRng.NextDouble() * 2f - 1f) * spinScale);

            p.Scale = 0.72f + (float)_localRng.NextDouble() * 0.52f;
            p.HalfExtentY = kind switch { 0 => 0.14f, 1 => 0.17f, _ => 0.22f } * p.Scale;
            p.FloorMinY = rubbleFloorY;
            p.Age = 0f;

            WriteTransform(in p);
        }
    }

    internal void Update(float deltaSeconds, HeightMap? heightMap)
    {
        if (_disposed || AwakeCount == 0 || deltaSeconds <= 0f)
            return;

        float dt = MathF.Min(deltaSeconds, 0.05f);
        for (int i = 0; i < TotalCapacity; i++)
        {
            ref Particle p = ref _particles[i];
            if (!p.Active || p.Sleeping)
                continue;

            p.Age += dt;
            p.Velocity.Y -= Gravity * dt;
            p.Position += p.Velocity * dt;
            p.RotationEuler += p.AngularVelocity * dt;

            float groundY = heightMap is not null ? heightMap.GetHeight(p.Position.X, p.Position.Z) : 0f;
            float floorY = MathF.Max(groundY, p.FloorMinY) + p.HalfExtentY;

            if (p.Position.Y <= floorY)
            {
                p.Position.Y = floorY;
                if (p.Velocity.Y < -0.55f)
                {
                    float restitution = p.Kind == 0 ? 0.22f : 0.30f;
                    p.Velocity.Y = -p.Velocity.Y * restitution;
                    p.Velocity.X *= 0.62f;
                    p.Velocity.Z *= 0.62f;
                    p.AngularVelocity *= 0.52f;
                }
                else
                {
                    p.Velocity.Y = 0f;
                    p.Velocity.X *= 0.72f;
                    p.Velocity.Z *= 0.72f;
                    p.AngularVelocity *= 0.60f;
                }

                // Settle slabs flatter against the rubble surface when nearly at rest
                if (p.Velocity.LengthSquared() < SleepSpeedSq && p.AngularVelocity.LengthSquared() < SleepSpinSq)
                {
                    p.Velocity = Vector3.Zero;
                    p.AngularVelocity = Vector3.Zero;
                    p.Sleeping = true;
                    AwakeCount--;
                }
            }

            WriteTransform(in p);
        }
    }

    internal void Clear()
    {
        if (_disposed)
            return;

        for (int i = 0; i < TotalCapacity; i++)
        {
            _particles[i].Active = false;
            _particles[i].Sleeping = false;
        }
        for (int kind = 0; kind < 3; kind++)
        {
            _batchVisibleCounts[kind] = 0;
            _batchCursors[kind] = 0;
            _multiMeshes[kind].VisibleInstanceCount = 0;
        }
        ActiveCount = 0;
        AwakeCount = 0;
    }

    private byte PickKind(bool includeLargeSlabs)
    {
        double roll = _localRng.NextDouble();
        if (!includeLargeSlabs)
            return roll < 0.38 ? (byte)1 : (byte)2;
        if (roll < 0.34) return 0;
        if (roll < 0.66) return 1;
        return 2;
    }

    private int AllocateParticle(byte kind)
    {
        int startOffset = kind switch { 0 => 0, 1 => SlabACapacity, _ => SlabACapacity + SlabBCapacity };
        int cap = _batchCapacities[kind];

        // Fill contiguous slots first so VisibleInstanceCount only covers active instances
        int visible = _batchVisibleCounts[kind];
        if (visible < cap)
        {
            _batchCursors[kind] = (visible + 1) % cap;
            return startOffset + visible;
        }

        // Pool is full: prefer recycling sleeping slabs before actively flying ones
        int cursor = _batchCursors[kind];
        for (int step = 0; step < cap; step++)
        {
            int slot = (cursor + step) % cap;
            int idx = startOffset + slot;
            if (_particles[idx].Sleeping)
            {
                _batchCursors[kind] = (slot + 1) % cap;
                return idx;
            }
        }

        int fallbackIdx = startOffset + cursor;
        _batchCursors[kind] = (cursor + 1) % cap;
        return fallbackIdx;
    }

    private void WriteTransform(in Particle p)
    {
        Basis basis = Basis.FromEuler(p.RotationEuler).Scaled(new Vector3(p.Scale, p.Scale, p.Scale));
        _multiMeshes[p.Kind].SetInstanceTransform(p.BatchSlot, new Transform3D(basis, p.Position));
    }

    private static BoxMesh CreateFallbackBox(Vector3 size, Color color)
    {
        return new BoxMesh
        {
            Size = size,
            Material = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.94f }
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (GodotObject.IsInstanceValid(_root))
        {
            _root.QueueFree();
        }
    }
}
