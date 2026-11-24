using Godot;

namespace DeadGround.Client;

/// <summary>
/// Procedural 3D spatial audio bank for concrete impacts, glass shatter, structural floor collapse,
/// and full building demolition. Pre-baked at startup into 16-bit PCM AudioStreamWav clips and
/// played through a fixed AudioStreamPlayer3D pool with zero runtime allocations.
/// </summary>
internal sealed class BuildingDestructionSoundBank : IDisposable
{
    private const int SampleRate = 22050;
    private const int PoolSize = 10;

    private readonly Node3D _root;
    private readonly AudioStreamPlayer3D[] _players = new AudioStreamPlayer3D[PoolSize];
    private readonly AudioStreamWav[] _hitClips;
    private readonly AudioStreamWav[] _floorCollapseClips;
    private readonly AudioStreamWav[] _fullCollapseClips;
    private int _nextPlayer;
    private int _variantCursor;
    private bool _disposed;

    internal int PrebakedClipCount => _hitClips.Length + _floorCollapseClips.Length + _fullCollapseClips.Length;

    internal BuildingDestructionSoundBank(Node3D parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _root = new Node3D { Name = "BuildingDestructionAudio" };
        parent.AddChild(_root);

        _hitClips = [CreateWav(RenderConcreteHit(101)), CreateWav(RenderConcreteHit(202))];
        _floorCollapseClips = [CreateWav(RenderFloorCollapse(303)), CreateWav(RenderFloorCollapse(404))];
        _fullCollapseClips = [CreateWav(RenderFullCollapse(505)), CreateWav(RenderFullCollapse(606))];

        for (int i = 0; i < PoolSize; i++)
        {
            AudioStreamPlayer3D player = new()
            {
                Name = $"DestructionAudio_{i}",
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                UnitSize = 48.0f,
                MaxDistance = 950.0f,
                MaxDb = 6.0f
            };
            _root.AddChild(player);
            _players[i] = player;
        }
    }

    internal void PlayConcreteHit(Vector3 worldPos) =>
        PlayClip(_hitClips, worldPos, volumeDb: -2.0f, pitchBase: 0.96f, pitchStep: 0.08f);

    internal void PlayFloorCollapse(Vector3 worldPos) =>
        PlayClip(_floorCollapseClips, worldPos, volumeDb: 1.5f, pitchBase: 0.94f, pitchStep: 0.06f);

    internal void PlayFullCollapse(Vector3 worldPos) =>
        PlayClip(_fullCollapseClips, worldPos, volumeDb: 3.5f, pitchBase: 0.92f, pitchStep: 0.05f);

    private void PlayClip(AudioStreamWav[] clips, Vector3 worldPos, float volumeDb, float pitchBase, float pitchStep)
    {
        if (_disposed || clips.Length == 0)
            return;

        AudioStreamPlayer3D player = _players[_nextPlayer];
        _nextPlayer = (_nextPlayer + 1) % PoolSize;

        int variant = _variantCursor++ % clips.Length;
        player.GlobalPosition = worldPos;
        player.Stream = clips[variant];
        player.VolumeDb = volumeDb;
        player.PitchScale = pitchBase + ((_variantCursor % 3) * 0.5f * pitchStep);
        player.Play();
    }

    private static float[] RenderConcreteHit(int seed)
    {
        int count = (int)(SampleRate * 0.85f);
        float[] samples = new float[count];
        Random rng = new(seed);
        float lp1 = 0f, lp2 = 0f, hp = 0f, prevW = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float w = (float)rng.NextDouble() * 2f - 1f;

            // Sharp concrete impact crack + heavy low thump
            float crackEnv = MathF.Exp(-t / 0.045f);
            float thump = MathF.Sin(MathF.Tau * (95f - 48f * MathF.Min(1f, t * 8f)) * t) * MathF.Exp(-t / 0.11f);

            // Gritty falling masonry debris crunch
            float k = 1f - MathF.Exp(-MathF.Tau * (420f + 900f * MathF.Exp(-t / 0.16f)) / SampleRate);
            lp1 += (w - lp1) * k;
            lp2 += (lp1 - lp2) * k;
            float crumbleEnv = MathF.Exp(-t / 0.28f) * (0.65f + 0.35f * MathF.Sin(t * 42f));

            // High-frequency glass shatter tinkle (band/high-pass bursts after 0.03 s)
            hp = 0.86f * (hp + w - prevW);
            prevW = w;
            float glassGate = t > 0.025f ? MathF.Exp(-(t - 0.025f) / 0.19f) * (MathF.Abs(MathF.Sin(t * 115f)) > 0.65f ? 1f : 0.2f) : 0f;

            float s = 0.65f * crackEnv * w + 0.55f * thump + 0.50f * crumbleEnv * lp2 + 0.22f * glassGate * hp;
            samples[i] = MathF.Tanh(s * 1.15f);
        }

        return Normalize(samples, 0.90f);
    }

    private static float[] RenderFloorCollapse(int seed)
    {
        int count = (int)(SampleRate * 1.55f);
        float[] samples = new float[count];
        Random rng = new(seed);
        float lp1 = 0f, lp2 = 0f, midLp = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float w = (float)rng.NextDouble() * 2f - 1f;

            // Deep structural concrete groan and heavy pancake slab drop
            float kLow = 1f - MathF.Exp(-MathF.Tau * (180f + 340f * MathF.Exp(-t / 0.45f)) / SampleRate);
            lp1 += (w - lp1) * kLow;
            lp2 += (lp1 - lp2) * kLow;

            float kMid = 1f - MathF.Exp(-MathF.Tau * 950f / SampleRate);
            midLp += (w - midLp) * kMid;

            float groan = MathF.Sin(MathF.Tau * (54f - 18f * MathF.Min(1f, t * 0.9f)) * t) * MathF.Exp(-t / 0.42f);
            float slam = MathF.Exp(-MathF.Pow((t - 0.28f) / 0.14f, 2f)) * 0.95f;
            float rumbleEnv = (1f - MathF.Exp(-t / 0.05f)) * MathF.Exp(-t / 0.68f);
            float cascadeClatter = MathF.Exp(-t / 0.85f) * (0.45f + 0.55f * MathF.Abs(MathF.Sin(t * 23f + 0.7f)));

            float s = 0.95f * rumbleEnv * lp2 + 0.55f * groan + 0.48f * slam * (lp1 + 0.4f * w) + 0.26f * cascadeClatter * midLp;
            samples[i] = MathF.Tanh(s * 1.25f);
        }

        return Normalize(samples, 0.94f);
    }

    private static float[] RenderFullCollapse(int seed)
    {
        int count = (int)(SampleRate * 2.35f);
        float[] samples = new float[count];
        Random rng = new(seed);
        float lp1 = 0f, lp2 = 0f, midLp = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float w = (float)rng.NextDouble() * 2f - 1f;

            float kLow = 1f - MathF.Exp(-MathF.Tau * (150f + 280f * MathF.Exp(-t / 0.85f)) / SampleRate);
            lp1 += (w - lp1) * kLow;
            lp2 += (lp1 - lp2) * kLow;

            float kMid = 1f - MathF.Exp(-MathF.Tau * 780f / SampleRate);
            midLp += (w - midLp) * kMid;

            // Three staggered pancake floor impacts at 0.18s, 0.56s, and 0.98s
            float impacts =
                0.85f * MathF.Exp(-MathF.Pow((t - 0.18f) / 0.12f, 2f)) +
                0.95f * MathF.Exp(-MathF.Pow((t - 0.56f) / 0.15f, 2f)) +
                0.78f * MathF.Exp(-MathF.Pow((t - 0.98f) / 0.18f, 2f));

            float roarEnv = (1f - MathF.Exp(-t / 0.08f)) * MathF.Exp(-t / 1.15f);
            float sub = MathF.Sin(MathF.Tau * (44f - 12f * MathF.Min(1f, t * 0.5f)) * t) * MathF.Exp(-t / 0.85f);
            float tail = t > 0.7f ? MathF.Exp(-(t - 0.7f) / 0.65f) * (0.4f + 0.6f * MathF.Abs(MathF.Sin(t * 17f))) : 0f;

            float s = 1.05f * roarEnv * lp2 + 0.60f * impacts * (lp1 + 0.35f * w) + 0.45f * sub + 0.25f * tail * midLp;
            samples[i] = MathF.Tanh(s * 1.3f);
        }

        return Normalize(samples, 0.95f);
    }

    private static float[] Normalize(float[] samples, float targetPeak)
    {
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float a = MathF.Abs(samples[i]);
            if (a > peak) peak = a;
        }
        if (peak > 1e-5f)
        {
            float scale = targetPeak / peak;
            for (int i = 0; i < samples.Length; i++)
                samples[i] *= scale;
        }
        return samples;
    }

    private static AudioStreamWav CreateWav(float[] samples)
    {
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = (short)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
            data[i * 2] = (byte)(pcm & 0xFF);
            data[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = SampleRate,
            Stereo = false,
            Data = data
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
