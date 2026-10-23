using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

// Visual terrain outside the playable map. It neither expands navigation nor supplies spotting cover.
internal static class TankBattleBackdrop
{
    internal const float Margin = 5120f;
    private const float Spacing = 80f;

    internal static float SampleHeight(HeightMap map, float x, float z)
    {
        float edgeX = Math.Clamp(x, 0f, map.Width);
        float edgeZ = Math.Clamp(z, 0f, map.Depth);
        float distance = new Vector2(x - edgeX, z - edgeZ).Length();
        float blend = Mathf.SmoothStep(0f, 1f, Math.Min(distance / 400f, 1f));
        return map.GetHeight(edgeX, edgeZ) + blend *
            (12f + 9f * MathF.Sin(x / 310f) * MathF.Cos(z / 430f) + distance * 0.008f);
    }

    internal static MeshInstance3D Create(HeightMap map, Material material)
    {
        int columns = (int)((map.Width + 2f * Margin) / Spacing) + 1;
        int rows = (int)((map.Depth + 2f * Margin) / Spacing) + 1;
        Vector3[] vertices = new Vector3[columns * rows];
        Vector3[] normals = new Vector3[vertices.Length];
        int insideCells = (int)(map.Width / Spacing) * (int)(map.Depth / Spacing);
        int[] indices = new int[((columns - 1) * (rows - 1) - insideCells) * 6];
        for (int z = 0; z < rows; z++)
        {
            for (int x = 0; x < columns; x++)
            {
                float wx = x * Spacing - Margin, wz = z * Spacing - Margin;
                int i = z * columns + x;
                vertices[i] = new Vector3(wx, SampleHeight(map, wx, wz), wz);
                normals[i] = new Vector3(
                    SampleHeight(map, wx - 1f, wz) - SampleHeight(map, wx + 1f, wz), 2f,
                    SampleHeight(map, wx, wz - 1f) - SampleHeight(map, wx, wz + 1f)).Normalized();
            }
        }
        int count = 0;
        for (int z = 0; z < rows - 1; z++)
        {
            for (int x = 0; x < columns - 1; x++)
            {
                float wx = x * Spacing - Margin, wz = z * Spacing - Margin;
                if (wx >= 0 && wx < map.Width && wz >= 0 && wz < map.Depth) continue;
                int a = z * columns + x, b = a + 1, c = a + columns, d = c + 1;
                indices[count++] = a; indices[count++] = b; indices[count++] = c;
                indices[count++] = b; indices[count++] = d; indices[count++] = c;
            }
        }
        using Godot.Collections.Array arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return new MeshInstance3D { Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }
}
