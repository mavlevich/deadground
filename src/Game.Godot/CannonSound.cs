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

    // One-pole low-pass coefficient for a cutoff in Hz.
    private static float Coefficient(float cutoff) => 1f - MathF.Exp(-MathF.Tau * cutoff / SampleRate);
}
