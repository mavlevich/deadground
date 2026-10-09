using System.Numerics;
using DeadGround.Engine;
using Xunit;

namespace DeadGround.Tests;

public sealed class HeightMapTests
{
    [Fact]
    public void HeightSamplingInterpolatesAndOwnsItsInput()
    {
        ushort[] heights = [0, 10, 20, 30];
        HeightMap map = new(2, 2, 1, 1, heights);
        heights[0] = 999;
        Assert.Equal(15f, map.GetHeight(0.5f, 0.5f));
        Assert.Equal(0f, map.GetHeight(0, 0));
        Assert.False(map.Contains(new Vector2(float.NaN, 0)));
    }

    [Fact]
    public void ClientRayPickingUsesTheHeightmapSurface()
    {
        HeightMap map = new(2, 2, 10, 1, [3, 3, 3, 3]);
        Assert.True(map.TryRaycast(new Vector3(5, 20, 5), -Vector3.UnitY, 100, out Vector3 hit));
        Assert.InRange(hit.Y, 2.999f, 3.001f);
        Assert.False(map.TryRaycast(new Vector3(-5, 20, 5), -Vector3.UnitY, 100, out _));
    }

    [Fact]
    public void PickingRespectsTheRequestedRangeIncludingItsEndPoint()
    {
        HeightMap map = new(2, 2, 10, 1, [3, 3, 3, 3]);
        Vector3 origin = new(5, 3003, 5);
        Assert.False(map.TryRaycast(origin, -Vector3.UnitY, 2000, out _));
        Assert.True(map.TryRaycast(origin, -Vector3.UnitY, 4000, out Vector3 far));
        Assert.InRange(far.Y, 2.999f, 3.001f);
        Assert.True(map.TryRaycast(new Vector3(5, 20, 5), -Vector3.UnitY, 17, out _));
        Assert.False(map.TryRaycast(origin, -Vector3.UnitY, float.NaN, out _));
        Assert.False(map.TryRaycast(new Vector3(float.NaN, 1, 1), -Vector3.UnitY, 100, out _));
    }
}
