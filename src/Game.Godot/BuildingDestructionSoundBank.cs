using Godot;

namespace DeadGround.Client;

/// <summary>
/// Physics-based granular procedural 3D spatial audio bank (44.1 kHz 16-bit PCM) for concrete
/// shell impacts, window glass shatter, partial structural collapse, and full panelka demolition.
/// Synthesizes discrete staggered masonry/slab impact grains, modal concrete fracture transients,
/// non-periodic crystalline glass pings, low-passed brown-noise structural sub-bass (zero synth
/// sine sweeps or periodic LFO warble), and urban courtyard slapback reflections. Pre-baked at
/// startup and played through a fixed AudioStreamPlayer3D pool with zero runtime allocations.
/// </summary>
internal sealed class BuildingDestructionSoundBank : IDisposable
{
    private const int SampleRate = 44100;
    private const int PoolSize = 12;

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
        PlayClip(_hitClips, worldPos, volumeDb: -1.5f, pitchBase: 0.94f, pitchStep: 0.06f);

    internal void PlayFloorCollapse(Vector3 worldPos) =>
        PlayClip(_floorCollapseClips, worldPos, volumeDb: 1.5f, pitchBase: 0.93f, pitchStep: 0.05f);

    internal void PlayFullCollapse(Vector3 worldPos) =>
        PlayClip(_fullCollapseClips, worldPos, volumeDb: 3.5f, pitchBase: 0.91f, pitchStep: 0.04f);

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
        float[] buf = new float[count];
        Random rng = new(seed);

        // 1. Sharp dry concrete fracture snap + low-end masonry punch (filtered brown noise, no pure sine)
        AddFilteredBlastBurst(buf, rng, startSec: 0.000f, durationSec: 0.24f, cutoffHz: 140f, amp: 1.05f, attackSec: 0.003f, decaySec: 0.075f);
        AddResonantConcreteCrack(buf, rng, startSec: 0.000f, amp: 0.88f, baseFreq: 420f);
        AddResonantConcreteCrack(buf, rng, startSec: 0.012f, amp: 0.55f, baseFreq: 860f);

        // 2. Staggered falling masonry chunks & concrete gravel trickle (24 discrete physics-timed impacts)
        for (int g = 0; g < 24; g++)
        {
            float u = (float)rng.NextDouble();
            float tStart = 0.03f + u * u * 0.85f;
            float grainAmp = (0.14f + 0.34f * (1f - u)) * (0.65f + 0.70f * (float)rng.NextDouble());
            float grainFreq = 260f + (float)rng.NextDouble() * 1450f;
            float grainDecay = 0.009f + (float)rng.NextDouble() * 0.022f;
            AddMasonryGrain(buf, rng, tStart, grainAmp, grainFreq, grainDecay);
        }

        // 3. Non-periodic shattering window glass shards (14 tiny high-Q crystalline pings)
        for (int k = 0; k < 14; k++)
        {
            float tStart = 0.018f + (float)rng.NextDouble() * 0.36f;
            float freq = 3100f + (float)rng.NextDouble() * 4600f;
            float amp = 0.05f + 0.11f * (float)rng.NextDouble();
            float decay = 0.008f + (float)rng.NextDouble() * 0.022f;
            AddGlassShard(buf, tStart, amp, freq, decay);
        }

        ApplyUrbanCourtyardSlapback(buf, delay1Sec: 0.074f, gain1: 0.24f, delay2Sec: 0.148f, gain2: 0.13f);
        SoftClipAndFadeOut(buf, drive: 1.25f, fadeTailSec: 0.12f);
        return Normalize(buf, 0.90f);
    }

    private static float[] RenderFloorCollapse(int seed)
    {
        int count = (int)(SampleRate * 2.35f);
        float[] buf = new float[count];
        Random rng = new(seed);

        // 1. Initial shear crack + heavy structural low-end slam (brown noise rumble, zero synth sweep)
        AddResonantConcreteCrack(buf, rng, startSec: 0.000f, amp: 0.92f, baseFreq: 340f);
        AddResonantConcreteCrack(buf, rng, startSec: 0.035f, amp: 0.65f, baseFreq: 680f);
        AddFilteredBlastBurst(buf, rng, startSec: 0.000f, durationSec: 0.95f, cutoffHz: 115f, amp: 1.15f, attackSec: 0.008f, decaySec: 0.32f);
        AddFilteredBlastBurst(buf, rng, startSec: 0.16f, durationSec: 1.15f, cutoffHz: 95f, amp: 1.25f, attackSec: 0.014f, decaySec: 0.38f);

        // 2. Soft low-mid dust/grit rushing bed (subtle, buried beneath discrete stone impacts)
        AddFilteredBlastBurst(buf, rng, startSec: 0.08f, durationSec: 1.85f, cutoffHz: 360f, amp: 0.26f, attackSec: 0.18f, decaySec: 0.72f);

        // 3. Dense granular cascade of 58 falling concrete slabs, bricks, and settling rubble
        for (int g = 0; g < 58; g++)
        {
            float u = (float)rng.NextDouble();
            // Cluster early-to-mid heavy slabs, taper into sparse rolling stones near 2.1 s
            float tStart = 0.04f + MathF.Pow(u, 1.45f) * 1.95f;
            bool heavySlab = g < 14;
            float grainAmp = heavySlab
                ? (0.32f + 0.45f * (float)rng.NextDouble())
                : (0.09f + 0.28f * (1f - u * 0.7f) * (float)rng.NextDouble());
            float grainFreq = heavySlab
                ? (150f + (float)rng.NextDouble() * 340f)
                : (380f + (float)rng.NextDouble() * 1550f);
            float grainDecay = heavySlab
                ? (0.025f + (float)rng.NextDouble() * 0.045f)
                : (0.008f + (float)rng.NextDouble() * 0.024f);
            AddMasonryGrain(buf, rng, tStart, grainAmp, grainFreq, grainDecay);
        }

        // 4. Shattering apartment windows during floor shear
        for (int k = 0; k < 18; k++)
        {
            float tStart = 0.02f + (float)rng.NextDouble() * 0.55f;
            float freq = 2900f + (float)rng.NextDouble() * 4800f;
            AddGlassShard(buf, tStart, 0.04f + 0.09f * (float)rng.NextDouble(), freq, 0.010f + (float)rng.NextDouble() * 0.024f);
        }

        ApplyUrbanCourtyardSlapback(buf, delay1Sec: 0.082f, gain1: 0.28f, delay2Sec: 0.165f, gain2: 0.15f);
        SoftClipAndFadeOut(buf, drive: 1.35f, fadeTailSec: 0.22f);
        return Normalize(buf, 0.94f);
    }

    private static float[] RenderFullCollapse(int seed)
    {
        int count = (int)(SampleRate * 3.15f);
        float[] buf = new float[count];
        Random rng = new(seed);

        // 1. Multi-stage heavy structural concrete failure & earth-shaking low-end slams
        AddResonantConcreteCrack(buf, rng, startSec: 0.000f, amp: 0.98f, baseFreq: 290f);
        AddResonantConcreteCrack(buf, rng, startSec: 0.065f, amp: 0.76f, baseFreq: 540f);
        AddResonantConcreteCrack(buf, rng, startSec: 0.22f, amp: 0.72f, baseFreq: 380f);

        AddFilteredBlastBurst(buf, rng, startSec: 0.00f, durationSec: 1.20f, cutoffHz: 110f, amp: 1.25f, attackSec: 0.010f, decaySec: 0.36f);
        AddFilteredBlastBurst(buf, rng, startSec: 0.24f, durationSec: 1.40f, cutoffHz: 92f, amp: 1.35f, attackSec: 0.018f, decaySec: 0.45f);
        AddFilteredBlastBurst(buf, rng, startSec: 0.58f, durationSec: 1.55f, cutoffHz: 84f, amp: 1.15f, attackSec: 0.022f, decaySec: 0.54f);

        // 2. Expanding concrete dust rush & low rumble bed
        AddFilteredBlastBurst(buf, rng, startSec: 0.12f, durationSec: 2.65f, cutoffHz: 320f, amp: 0.30f, attackSec: 0.26f, decaySec: 1.05f);

        // 3. Heavy granular avalanche of 92 concrete wall panels, floor slabs, and late rolling boulders
        for (int g = 0; g < 92; g++)
        {
            float u = (float)rng.NextDouble();
            float tStart = 0.05f + MathF.Pow(u, 1.38f) * 2.65f;
            bool heavySlab = g < 24;
            float grainAmp = heavySlab
                ? (0.36f + 0.52f * (float)rng.NextDouble())
                : (0.08f + 0.26f * (1f - u * 0.65f) * (float)rng.NextDouble());
            float grainFreq = heavySlab
                ? (130f + (float)rng.NextDouble() * 320f)
                : (360f + (float)rng.NextDouble() * 1650f);
            float grainDecay = heavySlab
                ? (0.028f + (float)rng.NextDouble() * 0.052f)
                : (0.008f + (float)rng.NextDouble() * 0.025f);
            AddMasonryGrain(buf, rng, tStart, grainAmp, grainFreq, grainDecay);
        }

        // 4. Shattering windows across collapsing facade
        for (int k = 0; k < 24; k++)
        {
            float tStart = 0.02f + (float)rng.NextDouble() * 0.85f;
            float freq = 2800f + (float)rng.NextDouble() * 5000f;
            AddGlassShard(buf, tStart, 0.04f + 0.085f * (float)rng.NextDouble(), freq, 0.010f + (float)rng.NextDouble() * 0.026f);
        }

        ApplyUrbanCourtyardSlapback(buf, delay1Sec: 0.086f, gain1: 0.30f, delay2Sec: 0.176f, gain2: 0.16f);
        SoftClipAndFadeOut(buf, drive: 1.40f, fadeTailSec: 0.28f);
        return Normalize(buf, 0.95f);
    }

    private static void AddFilteredBlastBurst(
        float[] buf,
        Random rng,
        float startSec,
        float durationSec,
        float cutoffHz,
        float amp,
        float attackSec,
        float decaySec)
    {
        int startIdx = Math.Clamp((int)(startSec * SampleRate), 0, buf.Length);
        int endIdx = Math.Clamp(startIdx + (int)(durationSec * SampleRate), startIdx, buf.Length);
        float k = 1f - MathF.Exp(-MathF.Tau * cutoffHz / SampleRate);
        float lp1 = 0f, lp2 = 0f, brown = 0f;

        for (int i = startIdx; i < endIdx; i++)
        {
            float t = (float)(i - startIdx) / SampleRate;
            float w = (float)rng.NextDouble() * 2f - 1f;
            // Integrate into leaky brown noise for rich earthy low-end texture
            brown = 0.96f * brown + 0.22f * w;
            lp1 += (brown - lp1) * k;
            lp2 += (lp1 - lp2) * k;

            float env = (1f - MathF.Exp(-t / MathF.Max(0.001f, attackSec))) * MathF.Exp(-t / MathF.Max(0.01f, decaySec));
            buf[i] += lp2 * env * amp;
        }
    }

    private static void AddResonantConcreteCrack(float[] buf, Random rng, float startSec, float amp, float baseFreq)
    {
        int startIdx = Math.Clamp((int)(startSec * SampleRate), 0, buf.Length);
        int len = Math.Min((int)(0.16f * SampleRate), buf.Length - startIdx);
        float f1 = baseFreq;
        float f2 = baseFreq * 2.13f;
        float f3 = baseFreq * 3.87f;

        // 3 damped resonant modes excited by short broadband fracture impulse
        float y1 = 0f, y1p = 0f, y2 = 0f, y2p = 0f, y3 = 0f, y3p = 0f;
        float r1 = 0.988f, r2 = 0.982f, r3 = 0.975f;
        float c1 = 2f * r1 * MathF.Cos(MathF.Tau * f1 / SampleRate);
        float c2 = 2f * r2 * MathF.Cos(MathF.Tau * f2 / SampleRate);
        float c3 = 2f * r3 * MathF.Cos(MathF.Tau * f3 / SampleRate);

        for (int n = 0; n < len; n++)
        {
            float t = (float)n / SampleRate;
            float exc = ((float)rng.NextDouble() * 2f - 1f) * MathF.Exp(-t / 0.009f);

            float ny1 = c1 * y1 - (r1 * r1) * y1p + exc * 0.28f;
            y1p = y1; y1 = ny1;

            float ny2 = c2 * y2 - (r2 * r2) * y2p + exc * 0.22f;
            y2p = y2; y2 = ny2;

            float ny3 = c3 * y3 - (r3 * r3) * y3p + exc * 0.16f;
            y3p = y3; y3 = ny3;

            buf[startIdx + n] += (y1 * 0.55f + y2 * 0.35f + y3 * 0.25f + exc * 0.45f) * amp;
        }
    }

    private static void AddMasonryGrain(float[] buf, Random rng, float startSec, float amp, float centerFreq, float decaySec)
    {
        int startIdx = Math.Clamp((int)(startSec * SampleRate), 0, buf.Length);
        int len = Math.Min((int)(decaySec * 5.0f * SampleRate), buf.Length - startIdx);
        if (len <= 4) return;

        // Second-order resonant bandpass filter excited by a crisp stone-impact transient
        float r = Math.Clamp(1f - (1.8f / (decaySec * SampleRate + 8f)), 0.92f, 0.994f);
        float cosW = MathF.Cos(MathF.Tau * Math.Clamp(centerFreq, 80f, 6000f) / SampleRate);
        float a1 = 2f * r * cosW;
        float a2 = r * r;
        float y1 = 0f, y2 = 0f;

        for (int n = 0; n < len; n++)
        {
            float t = (float)n / SampleRate;
            float impact = ((float)rng.NextDouble() * 2f - 1f) * MathF.Exp(-t / (decaySec * 0.32f));
            float y = a1 * y1 - a2 * y2 + impact * (1f - r);
            y2 = y1;
            y1 = y;

            float env = MathF.Exp(-t / decaySec);
            buf[startIdx + n] += (y * 2.8f + impact * 0.28f) * env * amp;
        }
    }

    private static void AddGlassShard(float[] buf, float startSec, float amp, float freqHz, float decaySec)
    {
        int startIdx = Math.Clamp((int)(startSec * SampleRate), 0, buf.Length);
        int len = Math.Min((int)(decaySec * 5.0f * SampleRate), buf.Length - startIdx);
        float w1 = MathF.Tau * freqHz / SampleRate;
        float w2 = MathF.Tau * (freqHz * 1.47f) / SampleRate;

        for (int n = 0; n < len; n++)
        {
            float t = (float)n / SampleRate;
            float env = MathF.Exp(-t / decaySec);
            buf[startIdx + n] += (MathF.Sin(w1 * n) * 0.65f + MathF.Sin(w2 * n) * 0.35f) * env * amp;
        }
    }

    private static void ApplyUrbanCourtyardSlapback(float[] buf, float delay1Sec, float gain1, float delay2Sec, float gain2)
    {
        int d1 = (int)(delay1Sec * SampleRate);
        int d2 = (int)(delay2Sec * SampleRate);
        float[] dry = new float[buf.Length];
        Array.Copy(buf, dry, buf.Length);

        // Low-pass filter reflected walls energy so reflections feel distant and natural
        float lp1 = 0f, lp2 = 0f;
        float k1 = 1f - MathF.Exp(-MathF.Tau * 1400f / SampleRate);
        float k2 = 1f - MathF.Exp(-MathF.Tau * 820f / SampleRate);

        for (int i = 0; i < buf.Length; i++)
        {
            if (i >= d1)
            {
                lp1 += (dry[i - d1] - lp1) * k1;
                buf[i] += lp1 * gain1;
            }
            if (i >= d2)
            {
                lp2 += (dry[i - d2] - lp2) * k2;
                buf[i] += lp2 * gain2;
            }
        }
    }

    private static void SoftClipAndFadeOut(float[] buf, float drive, float fadeTailSec)
    {
        int fadeSamples = Math.Min((int)(fadeTailSec * SampleRate), buf.Length);
        int fadeStart = buf.Length - fadeSamples;
        for (int i = 0; i < buf.Length; i++)
        {
            float s = MathF.Tanh(buf[i] * drive);
            if (i >= fadeStart && fadeSamples > 0)
            {
                float u = (float)(buf.Length - i) / fadeSamples;
                s *= u * u;
            }
            buf[i] = s;
        }
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
