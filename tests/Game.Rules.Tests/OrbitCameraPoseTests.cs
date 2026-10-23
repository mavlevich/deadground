using System.Numerics;
using DeadGround.Sandbox;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class OrbitCameraPoseTests
{
    [Fact]
    public void DefaultPose_MatchesExistingCameraAndResetRestoresIt()
    {
        OrbitCameraPose pose = new();
        Assert.True(Vector3.Distance(Vector3.Normalize(new Vector3(0, 115, 80)), pose.OffsetDirection) < 0.0001f);
        pose.Rotate(0.8f, -0.3f);
        pose.Reset();
        Assert.Equal(0f, pose.Yaw);
        Assert.Equal(OrbitCameraPose.DefaultPitch, pose.Pitch);
    }

    [Fact]
    public void Orbit_ClampsPitchWrapsYawAndRejectsNonfiniteInput()
    {
        OrbitCameraPose pose = new();
        pose.Rotate(100f, -100f);
        Assert.Equal(OrbitCameraPose.MinPitch, pose.Pitch);
        Assert.InRange(pose.Yaw, -MathF.PI, MathF.PI);
        pose.Rotate(0, 100f);
        Assert.Equal(OrbitCameraPose.MaxPitch, pose.Pitch);
        float yaw = pose.Yaw;
        pose.Rotate(float.NaN, 0);
        Assert.Equal(yaw, pose.Yaw);
    }

    [Theory]
    [InlineData(8f)]
    [InlineData(20f)]
    [InlineData(55f)]
    [InlineData(80f)]
    [InlineData(85f)]
    public void LowAngleCamera_KeepsAllGroundRowsInFront(float degrees)
    {
        float pitch = degrees * MathF.PI / 180f;
        const float size = 850f;
        float distance = OrbitCameraPose.Distance(size, pitch);
        float nearestRowHeight = distance * MathF.Sin(pitch) - size * 0.5f * MathF.Cos(pitch);
        Assert.True(nearestRowHeight > 40f);
    }

    [Fact]
    public void MouseDrag_RightAndDownUsePositiveYawAndPitch()
    {
        OrbitCameraPose pose = new();
        pose.Drag(20f, 20f);
        Assert.True(pose.Yaw > 0f);
        Assert.True(pose.Pitch > OrbitCameraPose.DefaultPitch);
        pose.Drag(0f, -1000f);
        Assert.Equal(OrbitCameraPose.MinPitch, pose.Pitch);
    }

    [Fact]
    public void PanDirection_FollowsRotatedScreenAxes()
    {
        OrbitCameraPose pose = new();
        pose.Rotate(MathF.PI / 2f, 0);
        Assert.True(Vector2.Distance(pose.GroundInput(1, 0), new Vector2(0, -1)) < 0.0001f);
        Assert.True(Vector2.Distance(pose.GroundInput(0, -1), new Vector2(-1, 0)) < 0.0001f);
    }
}
