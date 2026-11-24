using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Variant A pure-C# cosmetic concrete debris physics rendered via GPU MultiMesh batches.
/// Uses local client-only RNG (never touches match RNG streams), analytic HeightMap + rubble-floor
/// bounce with damping and automatic sleeping, zero RigidBody3D/PhysicsServer3D nodes, and zero
/// steady-state heap allocations.
/// </summary>
internal sealed class DebrisPhysicsPool : IDisposable
{
    internal const int TotalCapacity = 256;
    private const int SlabACapacity = 96;
    private const int SlabBCapacity = 80;
    private const int ChunkCCapacity = 80;
    private const float Gravity = 9.81f;
    private const float SleepSpeedSq = 0.09f; // 0.3 m/s
    private const float SleepSpinSq = 0.16f;  // 0.4 rad/s
    private const float HiddenY = -10000f;

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
    private readonly int[] _batchCursors = new int[3];
    private readonly int[] _batchSlotToParticle = new int[TotalCapacity];
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
                Mesh = meshes[kind]
            };
            Transform3D hidden = new(Basis.Identity.Scaled(new Vector3(0.0001f, 0.0001f, 0.0001f)), new Vector3(0f, HiddenY, 0f));
            for (int slot = 0; slot < cap; slot++)
            {
                mm.SetInstanceTransform(slot, hidden);
                _particles[particleIndex].Kind = (byte)kind;
                _particles[particleIndex].BatchSlot = slot;
                _batchSlotToParticle[particleIndex] = particleIndex;
                particleIndex++;
            }

            MultiMeshInstance3D mmi = new()
            {
                Name = kind switch { 0 => "DebrisSlabA", 1 => "DebrisSlabB", _ => "DebrisChunkC" },
                Multimesh = mm,
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
            }
            else if (p.Sleeping)
            {
                p.Sleeping = false;
                AwakeCount++;
            }

            float angle = (float)(_localRng.NextDouble() * Math.Tau);
            float r = (float)Math.Sqrt(_localRng.NextDouble()) * spreadRadius;
            float jitterY = (float)(_localRng.NextDouble() * 1.4 - 0.35);
            p.Position = new Vector3(
                origin.X + MathF.Cos(angle) * r,
                MathF.Max(rubbleFloorY + 0.35f, origin.Y + jitterY),
                origin.Z + MathF.Sin(angle) * r);

            float speed = minSpeed + (float)_localRng.NextDouble() * (maxSpeed - minSpeed);
            Vector3 randomDir = new(
                MathF.Cos(angle) * (0.45f + (float)_localRng.NextDouble() * 0.55f),
                0.25f + (float)_localRng.NextDouble() * 0.85f,
                MathF.Sin(angle) * (0.45f + (float)_localRng.NextDouble() * 0.55f));
            Vector3 dir = (randomDir + biasDir * 0.65f).Normalized();

            p.Velocity = dir * speed;
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

        Transform3D hidden = new(Basis.Identity.Scaled(new Vector3(0.0001f, 0.0001f, 0.0001f)), new Vector3(0f, HiddenY, 0f));
        for (int i = 0; i < TotalCapacity; i++)
        {
            ref Particle p = ref _particles[i];
            if (p.Active)
            {
                p.Active = false;
                p.Sleeping = false;
                _multiMeshes[p.Kind].SetInstanceTransform(p.BatchSlot, hidden);
            }
        }
        ActiveCount = 0;
        AwakeCount = 0;
        _batchCursors[0] = 0;
        _batchCursors[1] = 0;
        _batchCursors[2] = 0;
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
        int cursor = _batchCursors[kind];

        // Prefer unused or sleeping slots before recycling an actively flying slab
        for (int step = 0; step < cap; step++)
        {
            int slot = (cursor + step) % cap;
            int idx = startOffset + slot;
            if (!_particles[idx].Active || _particles[idx].Sleeping)
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
