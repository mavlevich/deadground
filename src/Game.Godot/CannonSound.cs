using Godot;

namespace DeadGround.Client;

// Procedural tank gun report. Everything is filtered noise except one short low thump, so it
// has no tonal beeps and keeps energy in the range small speakers can play. Layers:
// a Friedlander blast wave, a short noise crack, a boom whose low-pass sweeps from bright to
// dull, a falling low thump, two dull terrain echoes and a rolling rumble over about 3 s.
internal static class CannonSound
{
    internal const int SampleRate = 44100;
    internal const float Duration = 3.0f;
    internal const float BurstDuration = 1.0f;
    internal const float RifleDuration = 0.6f;
    internal const float GroundImpactDuration = 0.4f;
    internal const float RocketLaunchDuration = 1.15f;
    internal const float ArtilleryImpactDuration = 2.4f;
    internal const float Peak = 0.95f;

    // Delay in seconds, gain and low-pass cutoff in Hz of each echo of the dry blast.
    private static readonly (float Delay, float Gain, float Cutoff)[] Echoes =
    [
        (0.31f, 0.12f, 700f),
        (0.78f, 0.06f, 450f),
    ];

    internal static AudioStreamWav CreateBurstStream(int seed)
    {
        float[] samples = RenderBurst(seed);
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = (short)MathF.Round(samples[i] * 32767f);
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

    internal static AudioStreamWav CreateRifleStream(int seed)
    {
        float[] samples = RenderRifleBurst(seed);
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = (short)MathF.Round(samples[i] * 32767f);
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

    internal static float[] RenderRifleBurst(int seed)
    {
        int count = (int)(SampleRate * RifleDuration);
        Random rand = new(seed);
        float[] dry = new float[count];

        int shotCount = 3;
        float shotInterval = 0.075f;

        float hp = 0f, hpPrev = 0f, b1 = 0f, b2 = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;

            hp = 0.90f * (hp + white - hpPrev);
            hpPrev = white;

            float k = Coefficient(800f + 1600f * MathF.Exp(-t / 0.10f));
            b1 += (white - b1) * k;
            b2 += (b1 - b2) * k;

            float burstSignal = 0f;
            for (int s = 0; s < shotCount; s++)
            {
                float shotTime = s * shotInterval;
                if (t >= shotTime)
                {
                    float dt = t - shotTime;
                    if (dt < 0.05f)
                    {
                        float crack = hp * MathF.Exp(-dt / 0.004f);
                        float pop = b2 * MathF.Sin(MathF.Tau * 380f * dt) * MathF.Exp(-dt / 0.018f);
                        burstSignal += crack * 0.8f + pop * 0.45f;
                    }
                }
            }

            float tail = b1 * 0.08f * MathF.Exp(-t / 0.15f);
            dry[i] = burstSignal + tail;
        }

        float maxAbs = 0.0001f;
        for (int i = 0; i < count; i++)
        {
            float a = MathF.Abs(dry[i]);
            if (a > maxAbs) maxAbs = a;
        }
        float scale = (Peak * 0.85f) / maxAbs;
        for (int i = 0; i < count; i++)
        {
            dry[i] *= scale;
        }

        return dry;
    }

    internal static float[] RenderBurst(int seed)
    {
        int count = (int)(SampleRate * BurstDuration);
        Random rand = new(seed);
        float[] dry = new float[count];

        int shotCount = 5;
        float shotInterval = 0.08f;

        float hp = 0f, hpPrev = 0f, b1 = 0f, b2 = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;

            hp = 0.85f * (hp + white - hpPrev);
            hpPrev = white;

            float k = Coefficient(450f + 1200f * MathF.Exp(-t / 0.15f));
            b1 += (white - b1) * k;
            b2 += (b1 - b2) * k;

            float burstSignal = 0f;
            for (int s = 0; s < shotCount; s++)
            {
                float shotTime = s * shotInterval;
                if (t >= shotTime)
                {
                    float dt = t - shotTime;
                    if (dt < 0.07f)
                    {
                        float crack = hp * MathF.Exp(-dt / 0.006f);
                        float pop = b2 * MathF.Sin(MathF.Tau * 220f * dt) * MathF.Exp(-dt / 0.025f);
                        float punch = dt < 0.02f ? (1f - dt / 0.005f) * MathF.Exp(-dt / 0.004f) : 0f;
                        burstSignal += crack * 1.4f + pop * 2.0f + punch * 1.0f;
                    }
                }
            }

            dry[i] = burstSignal;
        }

        float[] output = (float[])dry.Clone();

        int echoOffset = (int)(0.12f * SampleRate);
        float ek = Coefficient(800f);
        float es1 = 0f, es2 = 0f;
        for (int i = 0; i + echoOffset < count; i++)
        {
            es1 += (dry[i] - es1) * ek;
            es2 += (es1 - es2) * ek;
            output[i + echoOffset] += es2 * 0.18f;
        }

        float r1 = 0f, r2 = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;
            float k = Coefficient(350f);
            r1 += (white - r1) * k;
            r2 += (r1 - r2) * k;
            output[i] += r2 * MathF.Exp(-t / 0.45f) * 0.4f;
        }

        int fade = (int)(0.15f * SampleRate);
        for (int i = count - fade; i < count; i++)
            output[i] *= (float)(count - i) / fade;

        float peak = 0f;
        foreach (float sample in output)
            peak = MathF.Max(peak, MathF.Abs(sample));
        if (peak > 1e-5f)
        {
            float drive = 1.3f / peak;
            float norm = Peak / MathF.Tanh(1.3f);
            for (int i = 0; i < count; i++)
                output[i] = MathF.Tanh(output[i] * drive) * norm;
        }

        return output;
    }

    internal static AudioStreamWav CreateGroundImpactStream(int seed)
    {
        float[] samples = RenderGroundImpact(seed);
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = (short)MathF.Round(samples[i] * 32767f);
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

    internal static float[] RenderGroundImpact(int seed)
    {
        int count = (int)(SampleRate * GroundImpactDuration);
        Random rand = new(seed);
        float[] output = new float[count];

        float b1 = 0f, b2 = 0f;
        float p1 = 0f, p2 = 0f;
        float d1 = 0f, d2 = 0f;
        float hp = 0f, hpPrev = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;

            // Remove sub-60Hz rumble with a gentle highpass so small speakers can play it
            hp = 0.88f * (hp + white - hpPrev);
            hpPrev = white;

            // Initial impact punch: filtered noise sweeping from 750 Hz down to 180 Hz
            float punchCutoff = 180f + 570f * MathF.Exp(-t / 0.02f);
            float pk = Coefficient(punchCutoff);
            p1 += (hp - p1) * pk;
            p2 += (p1 - p2) * pk;
            float punch = p2 * MathF.Exp(-t / 0.04f) * 2.2f;

            // Dull earth displacement thud: two-pole low-pass sweeping from 320 Hz down to 160 Hz
            float thudCutoff = 160f + 160f * MathF.Exp(-t / 0.07f);
            float tk = Coefficient(thudCutoff);
            b1 += (hp - b1) * tk;
            b2 += (hp - b2) * tk;
            float thud = b2 * MathF.Exp(-t / 0.14f) * 2.8f;

            // Loose soil / debris scatter: gentle mid-frequency noise
            float dk = Coefficient(850f);
            d1 += (hp - d1) * dk;
            d2 += (d1 - d2) * dk;
            float debris = d2 * MathF.Exp(-t / 0.20f) * 0.45f;

            output[i] = punch + thud + debris;
        }

        // Fade the last 0.08 s so the sample ends in silence.
        int fade = (int)(0.08f * SampleRate);
        for (int i = count - fade; i < count; i++)
        {
            output[i] *= (float)(count - i) / fade;
        }

        float peak = 0f;
        foreach (float sample in output)
        {
            peak = MathF.Max(peak, MathF.Abs(sample));
        }

        if (peak > 1e-5f)
        {
            float drive = 1.2f / peak;
            float norm = Peak / MathF.Tanh(1.2f);
            for (int i = 0; i < count; i++)
            {
                output[i] = MathF.Tanh(output[i] * drive) * norm;
            }
        }

        return output;
    }

    internal static AudioStreamWav CreateStream(int seed)
    {
        float[] samples = Render(seed);
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = (short)MathF.Round(samples[i] * 32767f);
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

    // Mono samples in [-Peak, Peak]. The same seed always gives the same sound.
    internal static float[] Render(int seed)
    {
        int count = (int)(SampleRate * Duration);
        Random rand = new(seed);
        float[] dry = new float[count];

        float hp = 0f, hpPrev = 0f, b1 = 0f, b2 = 0f, phase = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;

            // Blast front: positive overpressure, then a longer suction phase.
            float pulse = t < 0.06f ? (1f - t / 0.007f) * MathF.Exp(-t / 0.005f) : 0f;

            // Crack: high-passed noise, a few milliseconds long.
            hp = 0.82f * (hp + white - hpPrev);
            hpPrev = white;
            float crack = hp * MathF.Exp(-t / 0.007f);

            // Boom: two-pole low-pass on noise, sweeping from 3.3 kHz down to 140 Hz.
            float k = Coefficient(140f + 3200f * MathF.Exp(-t / 0.045f));
            b1 += (white - b1) * k;
            b2 += (b1 - b2) * k;
            float boom = b2 * MathF.Min(1f, t / 0.002f) * MathF.Exp(-t / 0.20f) * (1f + 2.5f * MathF.Exp(-t / 0.03f));

            // Thump: a short low tone falling from 72 Hz to 38 Hz, for weight on headphones.
            phase += MathF.Tau * (38f + 34f * MathF.Exp(-t / 0.05f)) / SampleRate;
            float thump = MathF.Sin(phase) * MathF.Min(1f, t / 0.004f) * MathF.Exp(-t / 0.11f);

            dry[i] = pulse * 0.95f + crack * 1.1f + boom * 2.6f + thump * 0.55f;
        }

        float[] output = (float[])dry.Clone();
        foreach ((float delay, float gain, float cutoff) in Echoes)
        {
            int offset = (int)(delay * SampleRate);
            float k = Coefficient(cutoff), s1 = 0f, s2 = 0f;
            for (int i = 0; i + offset < count; i++)
            {
                s1 += (dry[i] - s1) * k;
                s2 += (s1 - s2) * k;
                output[i + offset] += s2 * gain;
            }
        }

        // Rolling rumble: dark noise that swells after the blast and dies over seconds.
        float r1 = 0f, r2 = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;
            float k = Coefficient(260f + 500f * MathF.Exp(-t / 0.4f));
            r1 += (white - r1) * k;
            r2 += (r1 - r2) * k;
            output[i] += r2 * (1f - MathF.Exp(-t / 0.06f)) * MathF.Exp(-t / 0.75f) * 2.6f;
        }

        // Fade the last 0.3 s so the sample ends in silence, then normalize with a soft clip.
        int fade = (int)(0.3f * SampleRate);
        for (int i = count - fade; i < count; i++)
            output[i] *= (float)(count - i) / fade;

        float peak = 0f;
        foreach (float sample in output)
            peak = MathF.Max(peak, MathF.Abs(sample));
        float drive = 1.3f / peak;
        float norm = Peak / MathF.Tanh(1.3f);
        for (int i = 0; i < count; i++)
            output[i] = MathF.Tanh(output[i] * drive) * norm;
        return output;
    }

    internal static AudioStreamWav CreateRocketLaunchStream(int seed)
    {
        float[] samples = RenderRocketLaunch(seed);
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = (short)MathF.Round(samples[i] * 32767f);
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

    internal static float[] RenderRocketLaunch(int seed)
    {
        int count = (int)(SampleRate * RocketLaunchDuration);
        Random rand = new(seed);
        float[] output = new float[count];

        float hp = 0f, hpPrev = 0f;
        float jet1 = 0f, jet2 = 0f;
        float low1 = 0f, low2 = 0f;
        float rumblePhase = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;

            // Tube ignition crack (0-25ms)
            hp = 0.86f * (hp + white - hpPrev);
            hpPrev = white;
            float ignite = hp * MathF.Exp(-t / 0.014f) * 1.1f;

            // High-pressure solid-fuel exhaust roar sweeping down as rocket clears the tube
            float jetCutoff = 650f + 2200f * MathF.Exp(-t / 0.35f);
            float kJet = Coefficient(jetCutoff);
            jet1 += (white - jet1) * kJet;
            jet2 += (jet1 - jet2) * kJet;
            float jetEnv = (1f - MathF.Exp(-t / 0.025f)) * MathF.Exp(-t / 0.42f);
            float jetRoar = (jet2 - low2 * 0.45f) * jetEnv * 2.2f;

            // Deep rocket motor backblast rumble
            float kLow = Coefficient(180f + 220f * MathF.Exp(-t / 0.25f));
            low1 += (white - low1) * kLow;
            low2 += (low1 - low2) * kLow;
            float lowEnv = (1f - MathF.Exp(-t / 0.018f)) * MathF.Exp(-t / 0.36f);
            rumblePhase += MathF.Tau * (72f + 48f * MathF.Exp(-t / 0.18f)) / SampleRate;
            float subThrust = MathF.Sin(rumblePhase) * MathF.Exp(-t / 0.16f) * 0.35f;

            output[i] = ignite + jetRoar + low2 * lowEnv * 1.9f + subThrust;
        }

        int fade = (int)(0.18f * SampleRate);
        for (int i = count - fade; i < count; i++)
            output[i] *= (float)(count - i) / fade;

        float peak = 0.0001f;
        for (int i = 0; i < count; i++)
            peak = MathF.Max(peak, MathF.Abs(output[i]));
        float drive = 1.4f / peak;
        float norm = Peak / MathF.Tanh(1.4f);
        for (int i = 0; i < count; i++)
            output[i] = MathF.Tanh(output[i] * drive) * norm;

        return output;
    }

    internal static AudioStreamWav CreateArtilleryImpactStream(int seed)
    {
        float[] samples = RenderArtilleryImpact(seed);
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = (short)MathF.Round(samples[i] * 32767f);
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

    internal static float[] RenderArtilleryImpact(int seed)
    {
        int count = (int)(SampleRate * ArtilleryImpactDuration);
        Random rand = new(seed);
        float[] dry = new float[count];

        float hp = 0f, hpPrev = 0f, b1 = 0f, b2 = 0f, phase = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;

            // Sharp HE shockwave overpressure snap
            float pulse = t < 0.08f ? (1f - t / 0.011f) * MathF.Exp(-t / 0.008f) : 0f;

            // Fragmentation crack
            hp = 0.85f * (hp + white - hpPrev);
            hpPrev = white;
            float crack = hp * MathF.Exp(-t / 0.012f);

            // Massive 122mm HE blast boom sweeping from 2.6 kHz down to 95 Hz
            float k = Coefficient(95f + 2500f * MathF.Exp(-t / 0.075f));
            b1 += (white - b1) * k;
            b2 += (b1 - b2) * k;
            float boom = b2 * MathF.Min(1f, t / 0.003f) * MathF.Exp(-t / 0.34f) * (1f + 2.8f * MathF.Exp(-t / 0.045f));

            // Deep sub-bass earth thump (58 Hz -> 26 Hz)
            phase += MathF.Tau * (26f + 32f * MathF.Exp(-t / 0.09f)) / SampleRate;
            float thump = MathF.Sin(phase) * MathF.Min(1f, t / 0.005f) * MathF.Exp(-t / 0.22f);

            dry[i] = pulse * 1.1f + crack * 1.0f + boom * 3.0f + thump * 0.95f;
        }

        float[] output = (float[])dry.Clone();
        foreach ((float delay, float gain, float cutoff) in Echoes)
        {
            int offset = (int)(delay * SampleRate);
            float k = Coefficient(cutoff * 0.85f), s1 = 0f, s2 = 0f;
            for (int i = 0; i + offset < count; i++)
            {
                s1 += (dry[i] - s1) * k;
                s2 += (s1 - s2) * k;
                output[i + offset] += s2 * (gain * 1.35f);
            }
        }

        // Earth shower & rolling artillery thunder tail
        float r1 = 0f, r2 = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;
            float k = Coefficient(180f + 420f * MathF.Exp(-t / 0.55f));
            r1 += (white - r1) * k;
            r2 += (r1 - r2) * k;
            output[i] += r2 * (1f - MathF.Exp(-t / 0.05f)) * MathF.Exp(-t / 0.95f) * 2.4f;
        }

        int fade = (int)(0.3f * SampleRate);
        for (int i = count - fade; i < count; i++)
            output[i] *= (float)(count - i) / fade;

        float peak = 0.0001f;
        for (int i = 0; i < count; i++)
            peak = MathF.Max(peak, MathF.Abs(output[i]));
        float drive = 1.5f / peak;
        float norm = Peak / MathF.Tanh(1.5f);
        for (int i = 0; i < count; i++)
            output[i] = MathF.Tanh(output[i] * drive) * norm;

        return output;
    }

    internal const float RocketIncomingDuration = 0.92f;

    internal static AudioStreamWav CreateRocketIncomingStream(int seed)
    {
        float[] samples = RenderRocketIncoming(seed);
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = (short)MathF.Round(samples[i] * 32767f);
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

    // Realistic incoming 122mm rocket aerodynamic air-tearing rush & Doppler descending scream right before impact.
    internal static float[] RenderRocketIncoming(int seed)
    {
        int count = (int)(SampleRate * RocketIncomingDuration);
        Random rand = new(seed);
        float[] output = new float[count];

        float lp1 = 0f, lp2 = 0f;
        float hp = 0f, hpPrev = 0f;
        float flutterLp = 0f;
        float finPhase = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float u = t / RocketIncomingDuration; // 0 -> 1 approaching impact
            float white = (float)rand.NextDouble() * 2f - 1f;

            // Steep approach crescendo with clean 25ms tail taper right at impact
            float swell = MathF.Pow(u, 1.85f) * MathF.Min(1f, t / 0.08f);
            if (u > 0.94f)
            {
                swell *= Math.Clamp((1f - u) / 0.06f, 0f, 1f);
            }

            // Descending Doppler air-tearing band-pass noise (1750 Hz -> 480 Hz)
            float hiCut = 480f + 1270f * MathF.Pow(1f - u, 1.35f);
            float kLp = Coefficient(hiCut);
            lp1 += (white - lp1) * kLp;
            lp2 += (lp1 - lp2) * kLp;
            hp = 0.86f * (hp + lp2 - hpPrev);
            hpPrev = lp2;

            // Subtle turbulent fin-flutter scream (noisy resonant steel hiss, never a clean cartoon whistle)
            flutterLp += (white - flutterLp) * Coefficient(45f);
            float finFreq = (390f + 560f * MathF.Pow(1f - u, 1.5f)) * (1f + flutterLp * 0.14f);
            finPhase += MathF.Tau * finFreq / SampleRate;
            float finHiss = MathF.Sin(finPhase) * (0.30f + 0.70f * MathF.Abs(flutterLp)) * 0.24f;

            // Low air-pressure whoosh directly before touchdown
            float rush = lp2 * 0.65f + hp * 1.45f + finHiss;
            output[i] = rush * swell;
        }

        float peak = 0.0001f;
        for (int i = 0; i < count; i++)
            peak = MathF.Max(peak, MathF.Abs(output[i]));
        float targetPeak = Peak * 0.62f;
        float drive = 1.25f / peak;
        float norm = targetPeak / MathF.Tanh(1.25f);
        for (int i = 0; i < count; i++)
            output[i] = MathF.Tanh(output[i] * drive) * norm;

        return output;
    }

    // One-pole low-pass coefficient for a cutoff in Hz.
    private static float Coefficient(float cutoff) => 1f - MathF.Exp(-MathF.Tau * cutoff / SampleRate);
}
