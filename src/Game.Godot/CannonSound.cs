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
    internal const float Ak74Duration = 0.72f;
    internal const float RpgLaunchDuration = 0.92f;
    internal const float RpgImpactDuration = 0.95f;
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

    internal static AudioStreamWav CreateAk74Stream(int seed) => CreateWavFromSamples(RenderAk74Burst(seed));
    internal static AudioStreamWav CreateRpgLaunchStream(int seed) => CreateWavFromSamples(RenderRpgLaunch(seed));
    internal static AudioStreamWav CreateRpgImpactStream(int seed) => CreateWavFromSamples(RenderRpgImpact(seed));

    // Soviet 5.45mm AK-74 3-round burst: rhythmic 600 rpm cadence (95 ms spacing), punchy
    // two-chamber muzzle brake mid-low bark, stamped receiver mechanical clack, and sharp crack.
    internal static float[] RenderAk74Burst(int seed)
    {
        int count = (int)(SampleRate * Ak74Duration);
        Random rand = new(seed);
        float[] dry = new float[count];

        const int shotCount = 3;
        const float shotInterval = 0.095f;

        float hp = 0f, hpPrev = 0f;
        float b1 = 0f, b2 = 0f;
        float bark1 = 0f, bark2 = 0f;
        float phase = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;

            hp = 0.88f * (hp + white - hpPrev);
            hpPrev = white;

            float kCrack = Coefficient(900f + 1900f * MathF.Exp(-t / 0.12f));
            b1 += (white - b1) * kCrack;
            b2 += (b1 - b2) * kCrack;

            float kBark = Coefficient(260f + 420f * MathF.Exp(-t / 0.14f));
            bark1 += (white - bark1) * kBark;
            bark2 += (bark1 - bark2) * kBark;

            float signal = 0f;
            for (int s = 0; s < shotCount; s++)
            {
                float shotTime = s * shotInterval;
                if (t < shotTime) continue;
                float dt = t - shotTime;
                if (dt >= 0.11f) continue;

                // Crisp supersonic 5.45mm muzzle crack + heavy compensator gas bark
                float crack = hp * MathF.Exp(-dt / 0.0055f) * 1.15f;
                float brakeBark = bark2 * MathF.Exp(-dt / 0.022f) * 1.85f;
                // Distinct Kalashnikov gas-piston chesty thump (250 Hz -> 145 Hz)
                phase = MathF.Tau * (145f + 110f * MathF.Exp(-dt / 0.010f)) * dt;
                float body = MathF.Sin(phase) * MathF.Exp(-dt / 0.015f) * 0.55f;
                // Subtle metallic bolt-carrier cycle clack (~32 ms after ignition)
                float boltDt = dt - 0.032f;
                float boltClack = boltDt > 0f && boltDt < 0.012f
                    ? hp * MathF.Exp(-boltDt / 0.0025f) * 0.22f
                    : 0f;

                signal += crack + brakeBark + body + boltClack;
            }

            dry[i] = signal + b2 * 0.09f * MathF.Exp(-t / 0.22f);
        }

        // Short dry battlefield slapback echo
        float[] output = (float[])dry.Clone();
        int echoOffset = (int)(0.14f * SampleRate);
        float ek = Coefficient(950f);
        float e1 = 0f, e2 = 0f;
        for (int i = 0; i + echoOffset < count; i++)
        {
            e1 += (dry[i] - e1) * ek;
            e2 += (e1 - e2) * ek;
            output[i + echoOffset] += e2 * 0.16f;
        }

        int fade = (int)(0.09f * SampleRate);
        for (int i = count - fade; i < count; i++)
            output[i] *= (float)(count - i) / fade;

        NormalizeSoftClip(output, driveFactor: 1.35f, targetPeak: Peak * 0.88f);
        return output;
    }

    // Soviet RPG-7V launch: Stage 1 hollow steel tube booster pop + backblast pressure slap (0-45 ms),
    // followed by Stage 2 PG-7V solid sustainer rocket ignition whoosh (75-700 ms).
    internal static float[] RenderRpgLaunch(int seed)
    {
        int count = (int)(SampleRate * RpgLaunchDuration);
        Random rand = new(seed);
        float[] output = new float[count];

        float hp = 0f, hpPrev = 0f;
        float tube1 = 0f, tube2 = 0f;
        float jet1 = 0f, jet2 = 0f;
        float low1 = 0f, low2 = 0f;
        float popPhase = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;

            hp = 0.87f * (hp + white - hpPrev);
            hpPrev = white;

            // Stage 1: Hollow venturi booster ejection "THOOM-CRACK" (0 - 65 ms)
            float popFreq = 115f + 290f * MathF.Exp(-t / 0.014f);
            popPhase += MathF.Tau * popFreq / SampleRate;
            float tubeResonance = MathF.Sin(popPhase) * MathF.Exp(-t / 0.030f) * 1.35f;

            float kTube = Coefficient(380f + 1200f * MathF.Exp(-t / 0.028f));
            tube1 += (white - tube1) * kTube;
            tube2 += (tube1 - tube2) * kTube;
            float backblastSlap = (hp * MathF.Exp(-t / 0.014f) * 1.45f) + (tube2 * MathF.Exp(-t / 0.055f) * 2.15f);

            // Stage 2: PG-7V rocket sustainer ignition whoosh (kicks in around 65 ms after tube exit)
            float sustainer = 0f;
            float ts = t - 0.065f;
            if (ts > 0f)
            {
                float kJet = Coefficient(520f + 1850f * MathF.Exp(-ts / 0.24f));
                jet1 += (white - jet1) * kJet;
                jet2 += (jet1 - jet2) * kJet;
                float kLow = Coefficient(210f + 180f * MathF.Exp(-ts / 0.20f));
                low1 += (white - low1) * kLow;
                low2 += (low1 - low2) * kLow;

                float jetEnv = (1f - MathF.Exp(-ts / 0.022f)) * MathF.Exp(-ts / 0.28f);
                sustainer = ((jet2 - low2 * 0.4f) * 1.95f + low2 * 1.15f) * jetEnv;
            }

            output[i] = tubeResonance + backblastSlap + sustainer;
        }

        int fade = (int)(0.14f * SampleRate);
        for (int i = count - fade; i < count; i++)
            output[i] *= (float)(count - i) / fade;

        NormalizeSoftClip(output, driveFactor: 1.45f, targetPeak: Peak * 0.90f);
        return output;
    }

    // PG-7V 85mm HEAT warhead detonation: blistering shaped-charge crack + tight concussion punch + debris scatter.
    internal static float[] RenderRpgImpact(int seed)
    {
        int count = (int)(SampleRate * RpgImpactDuration);
        Random rand = new(seed);
        float[] output = new float[count];

        float hp = 0f, hpPrev = 0f;
        float b1 = 0f, b2 = 0f;
        float r1 = 0f, r2 = 0f;
        float phase = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;

            // Razor-sharp shaped-charge explosive shock snap (0 - 25 ms)
            float pulse = t < 0.05f ? (1f - t / 0.007f) * MathF.Exp(-t / 0.006f) : 0f;
            hp = 0.85f * (hp + white - hpPrev);
            hpPrev = white;
            float crack = hp * MathF.Exp(-t / 0.011f) * 1.55f;

            // Punchy 85mm HE detonation body (2.8 kHz -> 130 Hz)
            float k = Coefficient(130f + 2650f * MathF.Exp(-t / 0.048f));
            b1 += (white - b1) * k;
            b2 += (b1 - b2) * k;
            float boom = b2 * MathF.Min(1f, t / 0.002f) * MathF.Exp(-t / 0.19f) * (1f + 2.4f * MathF.Exp(-t / 0.025f));

            // Tight chest thump (95 Hz -> 48 Hz, keeps energy well above 60 Hz for laptop speakers)
            phase += MathF.Tau * (48f + 47f * MathF.Exp(-t / 0.045f)) / SampleRate;
            float thump = MathF.Sin(phase) * MathF.Min(1f, t / 0.003f) * MathF.Exp(-t / 0.10f) * 0.72f;

            // Brief shrapnel & dust tail
            float kr = Coefficient(240f + 500f * MathF.Exp(-t / 0.22f));
            r1 += (white - r1) * kr;
            r2 += (r1 - r2) * kr;
            float tail = r2 * (1f - MathF.Exp(-t / 0.03f)) * MathF.Exp(-t / 0.32f) * 1.45f;

            output[i] = pulse * 1.05f + crack + boom * 2.5f + thump + tail;
        }

        int fade = (int)(0.16f * SampleRate);
        for (int i = count - fade; i < count; i++)
            output[i] *= (float)(count - i) / fade;

        NormalizeSoftClip(output, driveFactor: 1.45f, targetPeak: Peak * 0.92f);
        return output;
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

    internal const float SamLaunchDuration = 1.15f;
    internal const float FlarePopDuration = 0.92f;
    internal const float AirburstDuration = 0.90f;
    internal const float MissileWarningDuration = 0.28f;

    internal static AudioStreamWav CreateSamLaunchStream(int seed) => CreateWavFromSamples(RenderSamLaunch(seed));
    internal static AudioStreamWav CreateFlarePopStream(int seed) => CreateWavFromSamples(RenderFlarePop(seed));
    internal static AudioStreamWav CreateAirburstStream(int seed) => CreateWavFromSamples(RenderAirburst(seed));
    internal static AudioStreamWav CreateMissileWarningStream() => CreateWavFromSamples(RenderMissileWarning());

    private static AudioStreamWav CreateWavFromSamples(float[] samples)
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

    // 9M37M Strela-10 SAM launch: sharp canister ejection pop + high-velocity supersonic solid-booster shriek.
    internal static float[] RenderSamLaunch(int seed)
    {
        int count = (int)(SampleRate * SamLaunchDuration);
        Random rand = new(seed);
        float[] output = new float[count];

        float lp1 = 0f, lp2 = 0f;
        float hp = 0f, hpPrev = 0f;
        float roar1 = 0f, roar2 = 0f;
        float phase = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;

            // 1. Sharp pneumatic canister uncorking pop (0 - 35ms)
            float popFreq = 190f + 520f * MathF.Exp(-t / 0.012f);
            phase += MathF.Tau * popFreq / SampleRate;
            float pop = (MathF.Sin(phase) * 0.7f + white * 0.6f) * MathF.Exp(-t / 0.024f) * 1.9f;

            // 2. Scorching high-frequency supersonic rocket shriek (20ms - 1.15s)
            float burnEnv = (1f - MathF.Exp(-t / 0.018f)) * MathF.Exp(-t / 0.42f);
            float hiCut = 1100f + 2600f * MathF.Exp(-t / 0.25f);
            float kHi = Coefficient(hiCut);
            lp1 += (white - lp1) * kHi;
            lp2 += (lp1 - lp2) * kHi;
            hp = 0.89f * (hp + lp2 - hpPrev);
            hpPrev = lp2;

            // 3. Tight mid-bass booster exhaust roar
            float kRoar = Coefficient(380f + 300f * MathF.Exp(-t / 0.20f));
            roar1 += (white - roar1) * kRoar;
            roar2 += (roar1 - roar2) * kRoar;

            output[i] = pop + hp * burnEnv * 2.2f + roar2 * burnEnv * 1.45f;
        }

        int fade = (int)(0.16f * SampleRate);
        for (int i = count - fade; i < count; i++)
            output[i] *= (float)(count - i) / fade;

        NormalizeSoftClip(output, driveFactor: 1.45f, targetPeak: Peak * 0.88f);
        return output;
    }

    // M130 aircraft flare dispenser salvo: 4 staggered twin-cartridge pairs every 0.20s with pyrotechnic hiss.
    internal static float[] RenderFlarePop(int seed)
    {
        int count = (int)(SampleRate * FlarePopDuration);
        Random rand = new(seed);
        float[] output = new float[count];

        ReadOnlySpan<float> burstTimes = [0.00f, 0.025f, 0.20f, 0.225f, 0.40f, 0.425f, 0.60f, 0.625f];
        for (int b = 0; b < burstTimes.Length; b++)
        {
            int start = (int)(burstTimes[b] * SampleRate);
            float gain = 0.82f + 0.18f * (float)rand.NextDouble();
            float baseFreq = 260f + 90f * (float)rand.NextDouble();
            float lp = 0f, hp = 0f, hpPrev = 0f;
            float phase = 0f;
            int popSamples = Math.Min(count - start, (int)(0.18f * SampleRate));

            for (int i = 0; i < popSamples; i++)
            {
                float t = (float)i / SampleRate;
                float white = (float)rand.NextDouble() * 2f - 1f;
                float freq = baseFreq + 420f * MathF.Exp(-t / 0.006f);
                phase += MathF.Tau * freq / SampleRate;

                float thump = MathF.Sin(phase) * MathF.Exp(-t / 0.011f) * 1.25f;
                lp += (white - lp) * Coefficient(2800f);
                hp = 0.82f * (hp + lp - hpPrev);
                hpPrev = lp;
                float crack = hp * MathF.Exp(-t / 0.016f) * 1.65f;
                float hiss = lp * MathF.Exp(-t / 0.085f) * 0.45f;

                output[start + i] += (thump + crack + hiss) * gain;
            }
        }

        int fade = (int)(0.06f * SampleRate);
        for (int i = count - fade; i < count; i++)
            output[i] *= (float)(count - i) / fade;

        NormalizeSoftClip(output, driveFactor: 1.35f, targetPeak: Peak * 0.72f);
        return output;
    }

    // High-altitude proximity-fuze HE-Frag SAM airburst: razor-sharp sky crack + hollow atmospheric echo.
    internal static float[] RenderAirburst(int seed)
    {
        int count = (int)(SampleRate * AirburstDuration);
        Random rand = new(seed);
        float[] output = new float[count];

        float bp1 = 0f, bp2 = 0f;
        float hp = 0f, hpPrev = 0f;
        float r1 = 0f, r2 = 0f;
        float phase = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float white = (float)rand.NextDouble() * 2f - 1f;

            // Razor-sharp shock crack (0 - 30ms)
            float snapFreq = 125f + 620f * MathF.Exp(-t / 0.009f);
            phase += MathF.Tau * snapFreq / SampleRate;
            float snap = (MathF.Sin(phase) * 0.75f + white * 0.9f) * MathF.Exp(-t / 0.022f) * 2.4f;

            // High-velocity shrapnel tear (mid-high band)
            float kBp = Coefficient(900f + 1600f * MathF.Exp(-t / 0.07f));
            bp1 += (white - bp1) * kBp;
            bp2 += (bp1 - bp2) * kBp;
            hp = 0.88f * (hp + bp2 - hpPrev);
            hpPrev = bp2;
            float frag = hp * MathF.Exp(-t / 0.14f) * 1.8f;

            // Crisp sky echo tail (lighter than ground blast, cleaner decay)
            float kR = Coefficient(210f + 350f * MathF.Exp(-t / 0.25f));
            r1 += (white - r1) * kR;
            r2 += (r1 - r2) * kR;
            float skyTail = r2 * (1f - MathF.Exp(-t / 0.025f)) * MathF.Exp(-t / 0.38f) * 1.5f;

            output[i] = snap + frag + skyTail;
        }

        int fade = (int)(0.14f * SampleRate);
        for (int i = count - fade; i < count; i++)
            output[i] *= (float)(count - i) / fade;

        NormalizeSoftClip(output, driveFactor: 1.45f, targetPeak: Peak * 0.86f);
        return output;
    }

    // Crisp dual-pip Cold War cockpit missile warning tone.
    internal static float[] RenderMissileWarning()
    {
        int count = (int)(SampleRate * MissileWarningDuration);
        float[] output = new float[count];
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float env = 0f;
            float freq = 1280f;
            if (t < 0.085f)
            {
                env = MathF.Sin(Math.Clamp(t / 0.085f, 0f, 1f) * MathF.PI);
                freq = 1240f;
            }
            else if (t >= 0.135f && t < 0.235f)
            {
                env = MathF.Sin(Math.Clamp((t - 0.135f) / 0.10f, 0f, 1f) * MathF.PI);
                freq = 1620f;
            }
            float tone = MathF.Sin(MathF.Tau * freq * t) * 0.78f + MathF.Sin(MathF.Tau * freq * 2f * t) * 0.22f;
            output[i] = tone * env * 0.22f;
        }
        return output;
    }

    private static void NormalizeSoftClip(float[] output, float driveFactor, float targetPeak)
    {
        float peak = 0.0001f;
        for (int i = 0; i < output.Length; i++)
            peak = MathF.Max(peak, MathF.Abs(output[i]));
        float drive = driveFactor / peak;
        float norm = targetPeak / MathF.Tanh(driveFactor);
        for (int i = 0; i < output.Length; i++)
            output[i] = MathF.Tanh(output[i] * drive) * norm;
    }

    // One-pole low-pass coefficient for a cutoff in Hz.
    private static float Coefficient(float cutoff) => 1f - MathF.Exp(-MathF.Tau * cutoff / SampleRate);
}
