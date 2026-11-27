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
        CheckAk74Render();
        CheckRpgRenders();
        CheckGroundImpactRender();
        CheckAudibleRange();
        CheckVariants(scene);
    }

    private static void CheckAk74Render()
    {
        float[] a = CannonSound.RenderAk74Burst(42);
        float[] b = CannonSound.RenderAk74Burst(42);
        Require(a.Length == (int)(CannonSound.SampleRate * CannonSound.Ak74Duration), "AK-74 sound has the wrong length.");

        float peak = 0f;
        bool same = true;
        for (int i = 0; i < a.Length; i++)
        {
            peak = MathF.Max(peak, MathF.Abs(a[i]));
            same &= a[i] == b[i];
        }
        Require(same, "The same seed must give the same AK-74 sound.");
        Require(peak <= CannonSound.Peak + 1e-4f && peak > CannonSound.Peak * 0.7f, $"AK-74 sound peak {peak} is out of range.");
        for (int i = a.Length - 100; i < a.Length; i++)
            Require(MathF.Abs(a[i]) < 0.01f, "AK-74 sound must end in silence, without a click.");
    }

    private static void CheckRpgRenders()
    {
        float[] launchA = CannonSound.RenderRpgLaunch(42);
        float[] launchB = CannonSound.RenderRpgLaunch(42);
        Require(launchA.Length == (int)(CannonSound.SampleRate * CannonSound.RpgLaunchDuration), "RPG launch sound has the wrong length.");
        float launchPeak = 0f;
        bool launchSame = true;
        for (int i = 0; i < launchA.Length; i++)
        {
            launchPeak = MathF.Max(launchPeak, MathF.Abs(launchA[i]));
            launchSame &= launchA[i] == launchB[i];
        }
        Require(launchSame, "The same seed must give the same RPG launch sound.");
        Require(launchPeak <= CannonSound.Peak + 1e-4f && launchPeak > CannonSound.Peak * 0.75f, $"RPG launch peak {launchPeak} is out of range.");
        for (int i = launchA.Length - 100; i < launchA.Length; i++)
            Require(MathF.Abs(launchA[i]) < 0.01f, "RPG launch sound must end in silence.");

        float[] impactA = CannonSound.RenderRpgImpact(42);
        float[] impactB = CannonSound.RenderRpgImpact(42);
        Require(impactA.Length == (int)(CannonSound.SampleRate * CannonSound.RpgImpactDuration), "RPG impact sound has the wrong length.");
        float impactPeak = 0f;
        bool impactSame = true;
        for (int i = 0; i < impactA.Length; i++)
        {
            impactPeak = MathF.Max(impactPeak, MathF.Abs(impactA[i]));
            impactSame &= impactA[i] == impactB[i];
        }
        Require(impactSame, "The same seed must give the same RPG impact sound.");
        Require(impactPeak <= CannonSound.Peak + 1e-4f && impactPeak > CannonSound.Peak * 0.75f, $"RPG impact peak {impactPeak} is out of range.");
        for (int i = impactA.Length - 100; i < impactA.Length; i++)
            Require(MathF.Abs(impactA[i]) < 0.01f, "RPG impact sound must end in silence.");
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

    private static void CheckGroundImpactRender()
    {
        float[] a = CannonSound.RenderGroundImpact(42);
        float[] b = CannonSound.RenderGroundImpact(42);
        Require(a.Length == (int)(CannonSound.SampleRate * CannonSound.GroundImpactDuration), "Ground impact sound has the wrong length.");

        float peak = 0f;
        bool same = true;
        for (int i = 0; i < a.Length; i++)
        {
            peak = MathF.Max(peak, MathF.Abs(a[i]));
            same &= a[i] == b[i];
        }
        Require(same, "The same seed must give the same ground impact sound.");
        Require(peak <= CannonSound.Peak + 1e-4f && peak > CannonSound.Peak * 0.8f, $"Ground impact sound peak {peak} is out of range.");

        for (int i = a.Length - 100; i < a.Length; i++)
            Require(MathF.Abs(a[i]) < 0.01f, "Ground impact sound must end in silence, without a click.");
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
        CheckAudibleRangeFor(CannonSound.Render(1337), "cannon sound");
        CheckAudibleRangeFor(CannonSound.RenderGroundImpact(1337), "ground impact sound");
        CheckAudibleRangeFor(CannonSound.RenderAk74Burst(1337), "AK-74 sound");
        CheckAudibleRangeFor(CannonSound.RenderRpgLaunch(1337), "RPG launch sound");
        CheckAudibleRangeFor(CannonSound.RenderRpgImpact(1337), "RPG impact sound");
    }

    private static void CheckAudibleRangeFor(float[] samples, string label)
    {
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
        Require(share < 0.25, $"{share:P0} of the {label} is below 60 Hz; small speakers would lose it.");
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

            AudioStreamWav[] akVariants = effects.Ak74Variants;
            Require(akVariants.Length == CombatEffects.Ak74VariantCount, "AK-74 variants are missing.");
            for (int i = 0; i < akVariants.Length; i++)
            {
                Require(akVariants[i] is { Data.Length: > 0 }, "An AK-74 variant has no data.");
                for (int j = 0; j < i; j++)
                    Require(!akVariants[i].Data.AsSpan().SequenceEqual(akVariants[j].Data), "AK-74 variants must differ.");
            }

            AudioStreamWav[] rpgLaunch = effects.RpgLaunchVariants;
            Require(rpgLaunch.Length == CombatEffects.RpgLaunchVariantCount, "RPG launch variants are missing.");
            for (int i = 0; i < rpgLaunch.Length; i++)
            {
                Require(rpgLaunch[i] is { Data.Length: > 0 }, "An RPG launch variant has no data.");
                for (int j = 0; j < i; j++)
                    Require(!rpgLaunch[i].Data.AsSpan().SequenceEqual(rpgLaunch[j].Data), "RPG launch variants must differ.");
            }

            AudioStreamWav[] rpgImpact = effects.RpgImpactVariants;
            Require(rpgImpact.Length == CombatEffects.RpgImpactVariantCount, "RPG impact variants are missing.");
            for (int i = 0; i < rpgImpact.Length; i++)
            {
                Require(rpgImpact[i] is { Data.Length: > 0 }, "An RPG impact variant has no data.");
                for (int j = 0; j < i; j++)
                    Require(!rpgImpact[i].Data.AsSpan().SequenceEqual(rpgImpact[j].Data), "RPG impact variants must differ.");
            }
        }
        finally { effects.Free(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
