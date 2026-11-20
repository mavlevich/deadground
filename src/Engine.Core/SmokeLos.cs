using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Allocation-free 3D ray-cylinder optical depth evaluator for tactical smoke clouds.
/// Produces strictly symmetric two-way line-of-sight blocking: ray(A -> B) equals ray(B -> A).
/// </summary>
public static class SmokeLos
{
    public const float BlockingOpticalDepth = 1.0f;
    private const float Epsilon = 1e-6f;

    private struct DensityEvent
    {
        public float T;
        public float DeltaDensity;
    }

    /// <summary>
    /// Fast boolean check: returns true if accumulated optical depth along the 3D segment stays below 1.0.
    /// Zero heap allocations.
    /// </summary>
    public static bool IsSmokeClear(
        Vector3 start,
        Vector3 end,
        ReadOnlySpan<SmokeVolume> volumes,
        SmokeCloudId excludeId = default)
    {
        if (volumes.Length == 0)
        {
            return true;
        }

        Vector3 delta = end - start;
        float totalDistanceSq = delta.LengthSquared();
        if (totalDistanceSq < 1e-10f)
        {
            return true;
        }

        float totalDistance = MathF.Sqrt(totalDistanceSq);
        float minRayX = MathF.Min(start.X, end.X);
        float maxRayX = MathF.Max(start.X, end.X);
        float minRayY = MathF.Min(start.Y, end.Y);
        float maxRayY = MathF.Max(start.Y, end.Y);
        float minRayZ = MathF.Min(start.Z, end.Z);
        float maxRayZ = MathF.Max(start.Z, end.Z);

        float tau = 0f;
        for (int i = 0; i < volumes.Length; i++)
        {
            ref readonly SmokeVolume v = ref volumes[i];
            if (v.OpticalDensity <= 0f || v.Radius <= 0f)
            {
                continue;
            }
            if (excludeId.Value != 0 && v.Id == excludeId)
            {
                continue;
            }

            // Fast AABB rejection
            if (v.CenterX + v.Radius < minRayX || v.CenterX - v.Radius > maxRayX ||
                v.CenterZ + v.Radius < minRayZ || v.CenterZ - v.Radius > maxRayZ ||
                v.TopY < minRayY || v.BaseY > maxRayY)
            {
                continue;
            }

            if (TryIntersectCylinder(start, delta, in v, out float tEnter, out float tExit))
            {
                float pathLen = (tExit - tEnter) * totalDistance;
                tau += pathLen * v.OpticalDensity;
                if (tau >= BlockingOpticalDepth - Epsilon)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Exact ray trace returning the normalized ray fraction hitT where accumulated smoke optical depth reaches 1.0.
    /// Zero heap allocations (uses stackalloc up to WorldState.MaxSmokeClouds * 2 events).
    /// </summary>
    public static bool TryTraceSmoke(
        Vector3 start,
        Vector3 delta,
        float totalDistance,
        ReadOnlySpan<SmokeVolume> volumes,
        out float hitT,
        SmokeCloudId excludeId = default)
    {
        hitT = 1.0f;
        if (volumes.Length == 0 || totalDistance < 1e-5f)
        {
            return false;
        }

        Vector3 end = start + delta;
        float minRayX = MathF.Min(start.X, end.X);
        float maxRayX = MathF.Max(start.X, end.X);
        float minRayY = MathF.Min(start.Y, end.Y);
        float maxRayY = MathF.Max(start.Y, end.Y);
        float minRayZ = MathF.Min(start.Z, end.Z);
        float maxRayZ = MathF.Max(start.Z, end.Z);

        int maxEvents = Math.Min(volumes.Length, WorldState.MaxSmokeClouds) * 2;
        Span<DensityEvent> events = stackalloc DensityEvent[maxEvents];
        int eventCount = 0;

        int count = Math.Min(volumes.Length, WorldState.MaxSmokeClouds);
        for (int i = 0; i < count; i++)
        {
            ref readonly SmokeVolume v = ref volumes[i];
            if (v.OpticalDensity <= 0f || v.Radius <= 0f)
            {
                continue;
            }
            if (excludeId.Value != 0 && v.Id == excludeId)
            {
                continue;
            }

            if (v.CenterX + v.Radius < minRayX || v.CenterX - v.Radius > maxRayX ||
                v.CenterZ + v.Radius < minRayZ || v.CenterZ - v.Radius > maxRayZ ||
                v.TopY < minRayY || v.BaseY > maxRayY)
            {
                continue;
            }

            if (TryIntersectCylinder(start, delta, in v, out float tEnter, out float tExit))
            {
                events[eventCount++] = new DensityEvent { T = tEnter, DeltaDensity = v.OpticalDensity };
                events[eventCount++] = new DensityEvent { T = tExit, DeltaDensity = -v.OpticalDensity };
            }
        }

        if (eventCount == 0)
        {
            return false;
        }

        // Insertion sort events by normalized ray fraction T
        for (int i = 1; i < eventCount; i++)
        {
            DensityEvent key = events[i];
            int j = i - 1;
            while (j >= 0 && events[j].T > key.T)
            {
                events[j + 1] = events[j];
                j--;
            }
            events[j + 1] = key;
        }

        float tau = 0f;
        float activeDensity = 0f;
        float prevT = events[0].T;
        int idx = 0;
        while (idx < eventCount)
        {
            float currT = events[idx].T;
            if (currT > prevT && activeDensity > 0f)
            {
                float segDist = (currT - prevT) * totalDistance;
                float segTau = segDist * activeDensity;
                if (tau + segTau >= BlockingOpticalDepth - Epsilon)
                {
                    float remainingTau = MathF.Max(0f, BlockingOpticalDepth - tau);
                    float fracWithinSeg = segTau > Epsilon ? remainingTau / segTau : 0f;
                    hitT = Math.Clamp(prevT + (currT - prevT) * fracWithinSeg, 0f, 1f);
                    return true;
                }
                tau += segTau;
            }

            while (idx < eventCount && events[idx].T == currT)
            {
                activeDensity += events[idx].DeltaDensity;
                idx++;
            }
            if (activeDensity < 0f)
            {
                activeDensity = 0f;
            }
            prevT = currT;
        }

        return false;
    }

    private static bool TryIntersectCylinder(
        Vector3 start,
        Vector3 delta,
        in SmokeVolume v,
        out float tEnter,
        out float tExit)
    {
        tEnter = 0f;
        tExit = 1f;

        // Vertical Y slab clip [BaseY, TopY]
        float dy = delta.Y;
        if (MathF.Abs(dy) < 1e-8f)
        {
            if (start.Y < v.BaseY || start.Y > v.TopY)
            {
                return false;
            }
        }
        else
        {
            float invDy = 1f / dy;
            float ty0 = (v.BaseY - start.Y) * invDy;
            float ty1 = (v.TopY - start.Y) * invDy;
            if (ty0 > ty1)
            {
                (ty0, ty1) = (ty1, ty0);
            }
            if (ty0 > tEnter) tEnter = ty0;
            if (ty1 < tExit) tExit = ty1;
            if (tExit <= tEnter)
            {
                return false;
            }
        }

        // Horizontal XZ infinite cylinder clip
        float dx = delta.X;
        float dz = delta.Z;
        float fx = start.X - v.CenterX;
        float fz = start.Z - v.CenterZ;
        float a = dx * dx + dz * dz;
        float rSq = v.Radius * v.Radius;

        if (a < 1e-10f)
        {
            return (fx * fx + fz * fz) <= rSq && tExit > tEnter;
        }

        float b = 2f * (fx * dx + fz * dz);
        float c = fx * fx + fz * fz - rSq;
        float disc = b * b - 4f * a * c;
        if (disc <= 0f)
        {
            return false;
        }

        float sqrtDisc = MathF.Sqrt(disc);
        float inv2A = 0.5f / a;
        float tx0 = (-b - sqrtDisc) * inv2A;
        float tx1 = (-b + sqrtDisc) * inv2A;

        if (tx0 > tEnter) tEnter = tx0;
        if (tx1 < tExit) tExit = tx1;

        return tExit > tEnter;
    }
}
