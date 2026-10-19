using System.Numerics;
using DeadGround.Sandbox;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class CameraModelTests
{
    [Fact]
    public void PanDisplacement_IsFrameRateInvariant()
    {
        // Moving in direction (1, 0) for 1 second total
        float[] frameRates = [30f, 60f, 120f, 144f];
        Vector3 initialFocus = new(100f, 0f, 100f);
        float size = 112f;

        Vector3 expectedDisplacement = Vector3.Zero;

        for (int i = 0; i < frameRates.Length; i++)
        {
            float fps = frameRates[i];
            float dt = 1.0f / fps;
            int frameCount = (int)fps; // 1 second total

            CameraModel model = new(initialFocus, size);
            model.SetBounds(0f, 1000f, 0f, 1000f);
            model.SetInput(1f, 0f);

            for (int f = 0; f < frameCount; f++)
            {
                model.Update(dt);
            }

            Vector3 displacement = model.Focus - initialFocus;

            if (i == 0)
            {
                expectedDisplacement = displacement;
                // At base pan speed 120 and size 112 (scale 1.0), 1 second should move 120 units in X
                Assert.True(MathF.Abs(displacement.X - 120f) < 0.1f, $"Initial displacement X was {displacement.X}");
            }
            else
            {
                Assert.True(
                    MathF.Abs(displacement.X - expectedDisplacement.X) < 1e-3f,
                    $"Displacement at {fps} FPS ({displacement.X}) did not match reference ({expectedDisplacement.X})");
            }
        }
    }

    [Fact]
    public void DiagonalPan_NormalizesSpeedToMatchAxialSpeed()
    {
        CameraModel axial = new(new Vector3(500f, 0f, 500f), 112f);
        CameraModel diagonal = new(new Vector3(500f, 0f, 500f), 112f);

        axial.SetInput(1f, 0f);
        diagonal.SetInput(1f, 1f);

        float dt = 0.016f;
        axial.Update(dt);
        diagonal.Update(dt);

        float axialDist = (axial.Focus - new Vector3(500f, 0f, 500f)).Length();
        float diagonalDist = (diagonal.Focus - new Vector3(500f, 0f, 500f)).Length();

        Assert.True(
            MathF.Abs(axialDist - diagonalDist) < 1e-4f,
            $"Axial speed {axialDist} did not match diagonal speed {diagonalDist}");
    }

    [Fact]
    public void PanSpeed_ScalesLinearlyWithZoomSize()
    {
        float refSize = 112f;
        CameraModel normal = new(new Vector3(500f, 0f, 500f), refSize);
        CameraModel zoomedOut = new(new Vector3(500f, 0f, 500f), refSize * 2f);
        CameraModel zoomedIn = new(new Vector3(500f, 0f, 500f), refSize * 0.5f);

        normal.SetInput(1f, 0f);
        zoomedOut.SetInput(1f, 0f);
        zoomedIn.SetInput(1f, 0f);

        float dt = 0.05f;
        normal.Update(dt);
        zoomedOut.Update(dt);
        zoomedIn.Update(dt);

        float normalDelta = normal.Focus.X - 500f;
        float zoomedOutDelta = zoomedOut.Focus.X - 500f;
        float zoomedInDelta = zoomedIn.Focus.X - 500f;

        Assert.True(MathF.Abs(zoomedOutDelta - (normalDelta * 2f)) < 1e-4f);
        Assert.True(MathF.Abs(zoomedInDelta - (normalDelta * 0.5f)) < 1e-4f);
    }

    [Fact]
    public void Focus_IsClampedToMapBounds()
    {
        CameraModel model = new(new Vector3(10f, 0f, 10f), 112f);
        model.SetBounds(0f, 100f, 0f, 100f);

        // Move far past negative bound
        model.SetInput(-1f, -1f);
        model.Update(10.0f);
        Assert.Equal(0f, model.Focus.X);
        Assert.Equal(0f, model.Focus.Z);

        // Move far past positive bound
        model.SetInput(1f, 1f);
        model.Update(10.0f);
        Assert.Equal(100f, model.Focus.X);
        Assert.Equal(100f, model.Focus.Z);
    }

    [Fact]
    public void StoppingInput_StopsMovementImmediatelyWithoutInertia()
    {
        CameraModel model = new(new Vector3(500f, 0f, 500f), 112f);
        model.SetInput(1f, 0f);
        model.Update(0.1f);

        Vector3 posAfterMove = model.Focus;
        Assert.True(posAfterMove.X > 500f);

        // Clear input / release keys
        model.ClearInput();
        model.Update(0.1f);

        Assert.Equal(posAfterMove.X, model.Focus.X);
        Assert.Equal(posAfterMove.Z, model.Focus.Z);
    }

    [Fact]
    public void ExponentialZoom_IsFrameRateInvariant()
    {
        float[] frameRates = [30f, 60f, 120f, 144f];
        float initialSize = 112f;
        float targetSize = 50f;
        float totalTime = 0.5f;

        float expectedFinalSize = 0f;

        for (int i = 0; i < frameRates.Length; i++)
        {
            float fps = frameRates[i];
            float dt = 1.0f / fps;
            int steps = (int)(totalTime * fps);

            CameraModel model = new(Vector3.Zero, initialSize);
            model.SetZoom(targetSize);

            for (int s = 0; s < steps; s++)
            {
                model.Update(dt);
            }

            if (i == 0)
            {
                expectedFinalSize = model.CurrentSize;
                Assert.True(expectedFinalSize < initialSize && expectedFinalSize > targetSize);
            }
            else
            {
                Assert.True(
                    MathF.Abs(model.CurrentSize - expectedFinalSize) < 0.05f,
                    $"Zoom at {fps} FPS ({model.CurrentSize}) differed from reference ({expectedFinalSize})");
            }
        }
    }

    [Fact]
    public void ZoomLimits_AreEnforced()
    {
        CameraModel model = new(Vector3.Zero, 100f);
        model.SetSizeLimits(35f, 200f);

        // Zoom in excessively
        for (int i = 0; i < 20; i++) model.ZoomIn();
        Assert.Equal(35f, model.TargetSize);

        // Zoom out excessively
        for (int i = 0; i < 20; i++) model.ZoomOut();
        Assert.Equal(200f, model.TargetSize);
    }

    [Fact]
    public void Reset_RestoresDefaultFocusAndSize()
    {
        Vector3 initial = new(100f, 0f, 200f);
        float initialSize = 112f;

        CameraModel model = new(initial, initialSize);
        model.SetInput(1f, 1f);
        model.ZoomIn();
        model.Update(1.0f);

        Assert.NotEqual(initial, model.Focus);
        Assert.NotEqual(initialSize, model.CurrentSize);

        model.Reset(initial, initialSize);

        Assert.Equal(initial, model.Focus);
        Assert.Equal(initialSize, model.CurrentSize);
        Assert.Equal(initialSize, model.TargetSize);
        Assert.Equal(Vector2.Zero, model.InputDirection);
    }

    [Fact]
    public void SimultaneousPanAndZoom_IsFrameRateAndPartitionInvariant()
    {
        // Half-second input: initial size 112, target size 2500, held right, large bounds
        float totalTime = 0.5f;
        float initialSize = 112f;
        float targetSize = 2500f;
        Vector3 initialFocus = new(5000f, 0f, 5000f);

        float[] frameRates = [30f, 60f, 120f, 144f];
        float expectedDisplacementX = 0f;

        for (int i = 0; i < frameRates.Length; i++)
        {
            float fps = frameRates[i];
            float dt = 1.0f / fps;
            int steps = (int)(totalTime * fps);

            CameraModel model = new(initialFocus, initialSize);
            model.SetBounds(0f, 50000f, 0f, 50000f);
            model.SetSizeLimits(10f, 5000f);
            model.SetZoom(targetSize);
            model.SetInput(1f, 0f);

            for (int s = 0; s < steps; s++)
            {
                model.Update(dt);
            }

            float displacementX = model.Focus.X - initialFocus.X;

            if (i == 0)
            {
                expectedDisplacementX = displacementX;
            }
            else
            {
                Assert.True(
                    MathF.Abs(displacementX - expectedDisplacementX) < 0.05f,
                    $"Simultaneous pan/zoom at {fps} FPS ({displacementX:F4}) differed from reference ({expectedDisplacementX:F4})");
            }
        }

        // Test with irregular time partition summing to 0.5s
        float[] irregularDts = [0.033f, 0.016f, 0.008f, 0.045f, 0.022f, 0.076f, 0.100f, 0.050f, 0.080f, 0.070f];
        // Sum is 0.500f
        CameraModel irregularModel = new(initialFocus, initialSize);
        irregularModel.SetBounds(0f, 50000f, 0f, 50000f);
        irregularModel.SetSizeLimits(10f, 5000f);
        irregularModel.SetZoom(targetSize);
        irregularModel.SetInput(1f, 0f);

        for (int step = 0; step < irregularDts.Length; step++)
        {
            irregularModel.Update(irregularDts[step]);
        }

        float irregularDisplacementX = irregularModel.Focus.X - initialFocus.X;
        Assert.True(
            MathF.Abs(irregularDisplacementX - expectedDisplacementX) < 0.05f,
            $"Irregular partition displacement ({irregularDisplacementX:F4}) differed from reference ({expectedDisplacementX:F4})");
    }

    [Fact]
    public void ManySmallWheelEvents_ZoomAsFarAsTheirSum()
    {
        // A trackpad or smooth wheel sends many small events for one gesture. Each used to count as
        // a full notch, so one swipe jumped between the closest and the widest zoom.
        CameraModel model = new(Vector3.Zero, 112f);
        model.SetSizeLimits(35f, 500f);
        for (int i = 0; i < 20; i++) model.ZoomSteps(0.05f);
        float expected = 112f * MathF.Pow(CameraModel.DefaultZoomStepFactor, 1f);
        Assert.True(MathF.Abs(model.TargetSize - expected) < 0.01f, $"20 events of 0.05 steps gave {model.TargetSize}, want {expected}");
    }

    [Fact]
    public void OneWheelEvent_IsCappedAtTwoSteps()
    {
        CameraModel model = new(Vector3.Zero, 112f);
        model.SetSizeLimits(35f, 500f);
        model.ZoomSteps(50f);
        float expected = 112f * MathF.Pow(CameraModel.DefaultZoomStepFactor, CameraModel.MaxStepsPerEvent);
        Assert.True(MathF.Abs(model.TargetSize - expected) < 0.01f, $"One large event gave {model.TargetSize}, want {expected}");
    }

    [Fact]
    public void PlainWheelNotches_KeepTheirStep()
    {
        CameraModel model = new(Vector3.Zero, 112f);
        model.SetSizeLimits(35f, 500f);
        model.ZoomIn();
        Assert.True(MathF.Abs(model.TargetSize - 112f * CameraModel.DefaultZoomStepFactor) < 0.01f);
        model.ZoomOut();
        Assert.True(MathF.Abs(model.TargetSize - 112f) < 0.01f);
    }

    [Fact]
    public void Pinch_ZoomsByItsScale_AndIgnoresBadValues()
    {
        CameraModel model = new(Vector3.Zero, 112f);
        model.SetSizeLimits(35f, 500f);
        model.ZoomByScale(1.25f);
        Assert.True(MathF.Abs(model.TargetSize - 112f / 1.25f) < 0.01f);
        model.ZoomByScale(0f);
        model.ZoomByScale(float.NaN);
        model.ZoomSteps(float.PositiveInfinity);
        Assert.True(MathF.Abs(model.TargetSize - 112f / 1.25f) < 0.01f);
    }
}
