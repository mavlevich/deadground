using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

// The first cannon sound was sine tones with most of its energy below 60 Hz, which laptop and
// phone speakers barely play, so players heard only the beeps.
internal static class CannonSoundRegressionTests
{
    internal static void Run(Node3D scene)
    {
        CheckRender();
        CheckBurstRender();
        CheckAudibleRange();
        CheckVariants(scene);
    }

    private static void CheckBurstRender()
    {
        float[] a = CannonSound.RenderBurst(42);
        float[] b = CannonSound.RenderBurst(42);
        Require(a.Length == (int)(CannonSound.SampleRate * CannonSound.BurstDuration), "Burst sound has the wrong length.");

        float peak = 0f;
        bool same = true;
        for (int i = 0; i < a.Length; i++)
        {
            peak = MathF.Max(peak, MathF.Abs(a[i]));
            same &= a[i] == b[i];
        }
        Require(same, "The same seed must give the same burst sound.");
        Require(peak <= CannonSound.Peak + 1e-4f && peak > CannonSound.Peak * 0.8f, $"Burst sound peak {peak} is out of range.");

        for (int i = a.Length - 100; i < a.Length; i++)
            Require(MathF.Abs(a[i]) < 0.01f, "Burst sound must end in silence, without a click.");
    }

    private static void CheckRender()
    {
        float[] a = CannonSound.Render(7);
        float[] b = CannonSound.Render(7);
        Require(a.Length == (int)(CannonSound.SampleRate * CannonSound.Duration), "Cannon sound has the wrong length.");

        float peak = 0f;
        bool same = true;
        for (int i = 0; i < a.Length; i++)
        {
            peak = MathF.Max(peak, MathF.Abs(a[i]));
            same &= a[i] == b[i];
        }
        Require(same, "The same seed must give the same cannon sound.");
        Require(peak <= CannonSound.Peak + 1e-4f && peak > CannonSound.Peak * 0.9f, $"Cannon sound peak {peak} is out of range.");

        for (int i = a.Length - 100; i < a.Length; i++)
            Require(MathF.Abs(a[i]) < 0.01f, "Cannon sound must end in silence, without a click.");
    }

    private static void CheckAudibleRange()
    {
        float[] samples = CannonSound.Render(1337);
        float k = 1f - MathF.Exp(-MathF.Tau * 60f / CannonSound.SampleRate);
        float s1 = 0f, s2 = 0f;
        double low = 0, total = 0;
        foreach (float sample in samples)
        {
            s1 += (sample - s1) * k;
            s2 += (s1 - s2) * k;
            low += s2 * s2;
            total += sample * sample;
        }
        double share = low / total;
        Require(share < 0.25, $"{share:P0} of the cannon sound is below 60 Hz; small speakers would lose it.");
    }

    private static void CheckVariants(Node3D scene)
    {
        CombatEffects effects = new(HeightMap.CreatePrototype());
        scene.AddChild(effects);
        try
        {
            AudioStreamWav[] variants = effects.CannonVariants;
            Require(variants.Length == CombatEffects.CannonVariantCount, "Cannon variants are missing.");
            for (int i = 0; i < variants.Length; i++)
            {
                Require(variants[i] is { Data.Length: > 0 }, "A cannon variant has no data.");
                for (int j = 0; j < i; j++)
                    Require(!variants[i].Data.AsSpan().SequenceEqual(variants[j].Data), "Cannon variants must differ.");
            }

            AudioStreamWav[] mgVariants = effects.MgBurstVariants;
            Require(mgVariants.Length == CombatEffects.MgBurstVariantCount, "MG burst variants are missing.");
            for (int i = 0; i < mgVariants.Length; i++)
            {
                Require(mgVariants[i] is { Data.Length: > 0 }, "An MG burst variant has no data.");
                for (int j = 0; j < i; j++)
                    Require(!mgVariants[i].Data.AsSpan().SequenceEqual(mgVariants[j].Data), "MG burst variants must differ.");
            }
        }
        finally { effects.Free(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
