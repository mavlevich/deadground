using DeadGround.Engine;
using Xunit;

namespace DeadGround.Core.Tests;

public sealed class DeterministicRandomTests
{
    [Fact]
    public void SameSeed_ProducesIdenticalSequence()
    {
        DeterministicRandom rng1 = new(123456789UL);
        DeterministicRandom rng2 = new(123456789UL);

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(rng1.NextULong(), rng2.NextULong());
            Assert.Equal(rng1.NextFloat(), rng2.NextFloat());
            Assert.Equal(rng1.NextDouble(), rng2.NextDouble());
            Assert.Equal(rng1.Next(10, 100), rng2.Next(10, 100));
        }
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        DeterministicRandom rng1 = new(111UL);
        DeterministicRandom rng2 = new(222UL);

        bool anyDifferent = false;
        for (int i = 0; i < 100; i++)
        {
            if (rng1.NextULong() != rng2.NextULong())
            {
                anyDifferent = true;
                break;
            }
        }
        Assert.True(anyDifferent);
    }

    [Fact]
    public void DomainStreams_AreIndependent()
    {
        ulong matchSeed = 987654321UL;

        DeterministicRandom combat1 = DeterministicRandom.CreateDomainStream(matchSeed, RandomDomain.Combat);
        DeterministicRandom artillery1 = DeterministicRandom.CreateDomainStream(matchSeed, RandomDomain.Artillery);
        DeterministicRandom ai1 = DeterministicRandom.CreateDomainStream(matchSeed, RandomDomain.Ai);

        DeterministicRandom combat2 = DeterministicRandom.CreateDomainStream(matchSeed, RandomDomain.Combat);
        DeterministicRandom artillery2 = DeterministicRandom.CreateDomainStream(matchSeed, RandomDomain.Artillery);

        // Draw heavily from combat1 and ai1
        for (int i = 0; i < 500; i++)
        {
            combat1.NextDouble();
            ai1.NextUInt();
        }

        // artillery1 was untouched, must match artillery2 exactly
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(artillery1.NextFloat(), artillery2.NextFloat());
        }

        // Draw from combat2 to catch up
        for (int i = 0; i < 500; i++)
        {
            combat2.NextDouble();
        }

        // combat1 and combat2 should now still be in sync
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(combat1.NextDouble(), combat2.NextDouble());
        }
    }

    [Fact]
    public void StateSaveAndRestore_ContinuesIdenticalSequence()
    {
        DeterministicRandom rng = new(42UL);

        // Advance 200 rolls
        for (int i = 0; i < 200; i++)
        {
            rng.NextFloat();
        }

        ulong savedState = rng.State;

        // Generate next 100 values
        float[] expected = new float[100];
        for (int i = 0; i < 100; i++)
        {
            expected[i] = rng.NextFloat();
        }

        // Restore state
        rng.State = savedState;

        // Verify sequence resumes identically
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(expected[i], rng.NextFloat());
        }
    }

    [Fact]
    public void NextFloat_AndNextDouble_StayStrictlyWithinBounds()
    {
        DeterministicRandom rng = new(55555UL);

        for (int i = 0; i < 5000; i++)
        {
            float f = rng.NextFloat();
            Assert.InRange(f, 0.0f, 0.9999999f);

            double d = rng.NextDouble();
            Assert.InRange(d, 0.0, 0.999999999999999);

            int bounded = rng.Next(5, 12);
            Assert.InRange(bounded, 5, 11);
        }
    }

    [Fact]
    public void DrawingRolls_ProducesZeroGcAllocations()
    {
        DeterministicRandom rng = new(999UL);

        // Warmup
        for (int i = 0; i < 100; i++)
        {
            rng.NextULong();
            rng.NextFloat();
            rng.NextDouble();
            rng.Next(1, 100);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10000; i++)
        {
            rng.NextULong();
            rng.NextFloat();
            rng.NextDouble();
            rng.Next(1, 100);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
