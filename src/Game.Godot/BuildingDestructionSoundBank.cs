using Godot;

namespace DeadGround.Client;

/// <summary>
/// Non-tonal filtered-noise 3D spatial audio bank (44.1 kHz 16-bit PCM) matching CannonSound's
/// dry military acoustic palette. Zero sine chimes, zero high-Q pitched resonators, zero LFO warble:
/// combines Friedlander concrete fracture snaps, sweeping 2-pole low-pass structural slams,
/// unpitched broadband gravel/rubble avalanche bursts, and dull urban courtyard echoes.
/// Pre-baked at startup and played through a fixed AudioStreamPlayer3D pool with zero runtime allocations.
/// </summary>
internal sealed class BuildingDestructionSoundBank : IDisposable
{
    private const int SampleRate = 44100;
    private const int PoolSize = 12;
    private const float Peak = 0.94f;

    private static readonly (float Delay, float Gain, float Cutoff)[] UrbanEchoes =
    [
        (0.24f, 0.14f, 680f),
        (0.58f, 0.07f, 420f),
    ];

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

        _hitClips = [CreateWav(RenderConcreteHit(101)), CreateWav(RenderConcreteHit(202)), CreateWav(RenderConcreteHit(303))];
        _floorCollapseClips = [CreateWav(RenderFloorCollapse(404)), CreateWav(RenderFloorCollapse(505)), CreateWav(RenderFloorCollapse(606))];
        _fullCollapseClips = [CreateWav(RenderFullCollapse(707)), CreateWav(RenderFullCollapse(808)), CreateWav(RenderFullCollapse(909))];

        for (int i = 0; i < PoolSize; i++)
        {
            AudioStreamPlayer3D player = new()
            {
                Name = $"DestructionAudio_{i}",
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                UnitSize = 52.0f,
                MaxDistance = 1000.0f,
                MaxDb = 6.0f
            };
            _root.AddChild(player);
            _players[i] = player;
        }
    }

    internal void PlayConcreteHit(Vector3 worldPos) =>
        PlayClip(_hitClips, worldPos, volumeDb: -2.0f, pitchBase: 0.94f, pitchStep: 0.06f);

    internal void PlayFloorCollapse(Vector3 worldPos) =>
        PlayClip(_floorCollapseClips, worldPos, volumeDb: 1.0f, pitchBase: 0.93f, pitchStep: 0.05f);

    internal void PlayFullCollapse(Vector3 worldPos) =>
        PlayClip(_fullCollapseClips, worldPos, volumeDb: 3.0f, pitchBase: 0.90f, pitchStep: 0.05f);

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
        player.PitchScale = pitchBase + ((_variantCursor % 4) * 0.45f * pitchStep);
        player.Play();
    }

    private static float[] RenderConcreteHit(int seed)
    {
        int count = (int)(SampleRate * 1.15f);
        float[] dry = new float[count];
        Random rng = new(seed);

        float hp = 0f, hpPrev = 0f, b1 = 0f, b2 = 0f, phase = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float w = (float)rng.NextDouble() * 2f - 1f;

            // Dry masonry shock snap (Friedlander transient + sharp high-pass crack)
            float pulse = t < 0.045f ? (1f - t / 0.008f) * MathF.Exp(-t / 0.006f) : 0f;
            hp = 0.86f * (hp + w - hpPrev);
            hpPrev = w;
            float crack = hp * MathF.Exp(-t / 0.011f);

            // Heavy unpitched concrete impact thud sweeping 1.9 kHz -> 105 Hz
            float k = Coefficient(105f + 1800f * MathF.Exp(-t / 0.055f));
            b1 += (w - b1) * k;
            b2 += (b1 - b2) * k;
            float slam = b2 * MathF.Min(1f, t / 0.002f) * MathF.Exp(-t / 0.19f) * (1f + 2.2f * MathF.Exp(-t / 0.032f));

            // Short damped sub-bass punch (52 Hz -> 28 Hz, strictly sub-bass, no pitch hang)
            phase += MathF.Tau * (28f + 24f * MathF.Exp(-t / 0.06f)) / SampleRate;
            float thump = MathF.Sin(phase) * MathF.Min(1f, t / 0.004f) * MathF.Exp(-t / 0.12f);

            dry[i] = pulse * 0.85f + crack * 0.95f + slam * 2.6f + thump * 0.65f;
        }

        // Unpitched broadband falling concrete rubble & grit trickle (zero resonance)
        AddUnpitchedRubbleShower(dry, rng, burstCount: 16, startMinSec: 0.04f, spanSec: 0.72f, minCutoffHz: 320f, maxCutoffHz: 1850f, gainScale: 0.45f);
        // Very subtle dry high-frequency dust/glass grit scatter (pure HP noise, zero sine tones)
        AddUnpitchedRubbleShower(dry, rng, burstCount: 8, startMinSec: 0.02f, spanSec: 0.28f, minCutoffHz: 3200f, maxCutoffHz: 6200f, gainScale: 0.12f, highPassOnly: true);

        return FinishWithEchoesAndSaturation(dry, rng, rumbleGain: 0.75f, rumbleDecaySec: 0.42f, drive: 1.45f, fadeTailSec: 0.18f);
    }

    private static float[] RenderFloorCollapse(int seed)
    {
        int count = (int)(SampleRate * 2.25f);
        float[] dry = new float[count];
        Random rng = new(seed);

        // Two heavy staggered dry structural slams (initial floor shear + lower slab impact)
        AddNoiseSlam(dry, rng, startSec: 0.00f, durationSec: 0.85f, startCutoffHz: 1550f, endCutoffHz: 85f, decaySec: 0.26f, amp: 2.5f, subFreqStart: 48f, subAmp: 0.75f);
        AddNoiseSlam(dry, rng, startSec: 0.24f, durationSec: 1.15f, startCutoffHz: 1150f, endCutoffHz: 72f, decaySec: 0.36f, amp: 2.9f, subFreqStart: 42f, subAmp: 0.85f);

        // Dry broadband concrete avalanche & settling debris chunks (100% unpitched noise bursts)
        AddUnpitchedRubbleShower(dry, rng, burstCount: 38, startMinSec: 0.05f, spanSec: 1.65f, minCutoffHz: 190f, maxCutoffHz: 1350f, gainScale: 0.65f);
        AddUnpitchedRubbleShower(dry, rng, burstCount: 12, startMinSec: 0.04f, spanSec: 0.55f, minCutoffHz: 2800f, maxCutoffHz: 5800f, gainScale: 0.10f, highPassOnly: true);

        return FinishWithEchoesAndSaturation(dry, rng, rumbleGain: 1.75f, rumbleDecaySec: 0.88f, drive: 1.50f, fadeTailSec: 0.30f);
    }

    private static float[] RenderFullCollapse(int seed)
    {
        int count = (int)(SampleRate * 3.10f);
        float[] dry = new float[count];
        Random rng = new(seed);

        // Three heavy cascading pancake structural collapses (pure sweeping filtered noise + deep earth thump)
        AddNoiseSlam(dry, rng, startSec: 0.00f, durationSec: 1.00f, startCutoffHz: 1650f, endCutoffHz: 82f, decaySec: 0.30f, amp: 2.6f, subFreqStart: 46f, subAmp: 0.80f);
        AddNoiseSlam(dry, rng, startSec: 0.32f, durationSec: 1.25f, startCutoffHz: 1300f, endCutoffHz: 68f, decaySec: 0.40f, amp: 3.1f, subFreqStart: 40f, subAmp: 0.95f);
        AddNoiseSlam(dry, rng, startSec: 0.70f, durationSec: 1.45f, startCutoffHz: 980f, endCutoffHz: 60f, decaySec: 0.48f, amp: 2.8f, subFreqStart: 36f, subAmp: 0.85f);

        // Massive unpitched concrete slab & gravel avalanche tapering into quiet late settling dust
        AddUnpitchedRubbleShower(dry, rng, burstCount: 64, startMinSec: 0.06f, spanSec: 2.35f, minCutoffHz: 160f, maxCutoffHz: 1250f, gainScale: 0.72f);
        AddUnpitchedRubbleShower(dry, rng, burstCount: 16, startMinSec: 0.05f, spanSec: 0.90f, minCutoffHz: 2600f, maxCutoffHz: 5400f, gainScale: 0.10f, highPassOnly: true);

        return FinishWithEchoesAndSaturation(dry, rng, rumbleGain: 2.35f, rumbleDecaySec: 1.25f, drive: 1.55f, fadeTailSec: 0.38f);
    }

    private static void AddNoiseSlam(
        float[] buf,
        Random rng,
        float startSec,
        float durationSec,
        float startCutoffHz,
        float endCutoffHz,
        float decaySec,
        float amp,
        float subFreqStart,
        float subAmp)
    {
        int startIdx = Math.Clamp((int)(startSec * SampleRate), 0, buf.Length);
        int endIdx = Math.Clamp(startIdx + (int)(durationSec * SampleRate), startIdx, buf.Length);
        float hp = 0f, hpPrev = 0f, b1 = 0f, b2 = 0f, phase = 0f;

        for (int i = startIdx; i < endIdx; i++)
        {
            float t = (float)(i - startIdx) / SampleRate;
            float w = (float)rng.NextDouble() * 2f - 1f;

            // Brief unpitched shear tear on onset
            hp = 0.84f * (hp + w - hpPrev);
            hpPrev = w;
            float tear = hp * MathF.Exp(-t / 0.018f) * 0.55f;

            // Heavy sweeping 2-pole low-pass noise boom (exact acoustic weight of concrete slamming earth)
            float cutoff = endCutoffHz + (startCutoffHz - endCutoffHz) * MathF.Exp(-t / (decaySec * 0.35f));
            float k = Coefficient(cutoff);
            b1 += (w - b1) * k;
            b2 += (b1 - b2) * k;
            float boom = b2 * MathF.Min(1f, t / 0.006f) * MathF.Exp(-t / decaySec) * (1f + 1.8f * MathF.Exp(-t / 0.06f));

            // Damped sub-bass chest thump (strictly < 50 Hz, decays rapidly)
            float subFreq = 22f + (subFreqStart - 22f) * MathF.Exp(-t / 0.10f);
            phase += MathF.Tau * subFreq / SampleRate;
            float sub = MathF.Sin(phase) * MathF.Min(1f, t / 0.008f) * MathF.Exp(-t / 0.22f);

            buf[i] += tear + boom * amp + sub * subAmp;
        }
    }

    private static void AddUnpitchedRubbleShower(
        float[] buf,
        Random rng,
        int burstCount,
        float startMinSec,
        float spanSec,
        float minCutoffHz,
        float maxCutoffHz,
        float gainScale,
        bool highPassOnly = false)
    {
        for (int g = 0; g < burstCount; g++)
        {
            float u = (float)rng.NextDouble();
            float tStart = startMinSec + MathF.Pow(u, 1.35f) * spanSec;
            float decaySec = highPassOnly
                ? (0.006f + (float)rng.NextDouble() * 0.016f)
                : (0.018f + (float)rng.NextDouble() * 0.065f);
            int startIdx = Math.Clamp((int)(tStart * SampleRate), 0, buf.Length);
            int len = Math.Min((int)(decaySec * 4.5f * SampleRate), buf.Length - startIdx);
            if (len <= 4) continue;

            float cutoff = minCutoffHz + (float)rng.NextDouble() * (maxCutoffHz - minCutoffHz);
            float kLp = Coefficient(cutoff);
            float amp = gainScale * (1f - 0.55f * u) * (0.45f + 0.85f * (float)rng.NextDouble());

            // Pure critically-damped 1-pole / 2-pole filtered noise (Q <= 0.5, ZERO pitch/ringing!)
            float lp1 = 0f, lp2 = 0f, hp = 0f, hpPrev = 0f;
            float hpAlpha = highPassOnly ? 0.90f : 0.72f;
            for (int n = 0; n < len; n++)
            {
                float t = (float)n / SampleRate;
                float w = (float)rng.NextDouble() * 2f - 1f;
                hp = hpAlpha * (hp + w - hpPrev);
                hpPrev = w;

                float sample;
                if (highPassOnly)
                {
                    sample = hp;
                }
                else
                {
                    lp1 += (hp - lp1) * kLp;
                    lp2 += (lp1 - lp2) * kLp;
                    sample = lp2 * 2.2f;
                }

                float env = MathF.Min(1f, t / 0.003f) * MathF.Exp(-t / decaySec);
                buf[startIdx + n] += sample * env * amp;
            }
        }
    }

    private static float[] FinishWithEchoesAndSaturation(
        float[] dry,
        Random rng,
        float rumbleGain,
        float rumbleDecaySec,
        float drive,
        float fadeTailSec)
    {
        int count = dry.Length;
        float[] output = (float[])dry.Clone();

        // Dull distant concrete courtyard echoes (same 2-pole damped architecture as CannonSound)
        foreach ((float delay, float gain, float cutoff) in UrbanEchoes)
        {
            int offset = (int)(delay * SampleRate);
            float k = Coefficient(cutoff);
            float s1 = 0f, s2 = 0f;
            for (int i = 0; i + offset < count; i++)
            {
                s1 += (dry[i] - s1) * k;
                s2 += (s1 - s2) * k;
                output[i + offset] += s2 * gain;
            }
        }

        // Low-passed concrete dust & rolling debris rumble bed
        float r1 = 0f, r2 = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float w = (float)rng.NextDouble() * 2f - 1f;
            float k = Coefficient(110f + 340f * MathF.Exp(-t / (rumbleDecaySec * 0.65f)));
            r1 += (w - r1) * k;
            r2 += (r1 - r2) * k;
            float env = (1f - MathF.Exp(-t / 0.06f)) * MathF.Exp(-t / rumbleDecaySec);
            output[i] += r2 * env * rumbleGain;
        }

        // Smooth tail fade to zero
        int fade = Math.Min((int)(fadeTailSec * SampleRate), count);
        for (int i = count - fade; i < count; i++)
        {
            float u = (float)(count - i) / fade;
            output[i] *= u;
        }

        // Warm soft-clipping normalization matching CannonSound
        float maxAbs = 0.0001f;
        for (int i = 0; i < count; i++)
            maxAbs = MathF.Max(maxAbs, MathF.Abs(output[i]));
        float scaleIn = drive / maxAbs;
        float scaleOut = Peak / MathF.Tanh(drive);
        for (int i = 0; i < count; i++)
            output[i] = MathF.Tanh(output[i] * scaleIn) * scaleOut;

        return output;
    }

    private static float Coefficient(float cutoffHz) =>
        1f - MathF.Exp(-MathF.Tau * cutoffHz / SampleRate);

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
