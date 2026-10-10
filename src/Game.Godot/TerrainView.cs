using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

internal static class TerrainView
{
    public static MeshInstance3D Create(HeightMap map)
    {
        int columns = map.Columns, rows = map.Rows;
        Vector3[] vertices = new Vector3[columns * rows];
        Vector3[] normals = new Vector3[vertices.Length];
        Color[] colors = new Color[vertices.Length];
        int[] indices = new int[(columns - 1) * (rows - 1) * 6];
        Color low = new("53644c"), high = new("849070");
        for (int z = 0; z < rows; z++)
            for (int x = 0; x < columns; x++)
            {
                int i = z * columns + x;
                float wx = x * map.CellSize, wz = z * map.CellSize;
                float h = map.GetHeight(wx, wz);
                vertices[i] = new Vector3(wx, h, wz);
                normals[i] = new Vector3(map.GetHeight(wx - map.CellSize, wz) - map.GetHeight(wx + map.CellSize, wz),
                    2 * map.CellSize, map.GetHeight(wx, wz - map.CellSize) - map.GetHeight(wx, wz + map.CellSize)).Normalized();
                colors[i] = low.Lerp(high, Mathf.Clamp(h / 10, 0, 1));
            }
        int offset = 0;
        for (int z = 0; z < rows - 1; z++)
            for (int x = 0; x < columns - 1; x++)
            {
                int a = z * columns + x, b = a + 1, c = a + columns, d = c + 1;
                // Godot uses clockwise front faces. The opposite winding hides the ground from above.
                indices[offset++] = a; indices[offset++] = b; indices[offset++] = c;
                indices[offset++] = b; indices[offset++] = d; indices[offset++] = c;
            }
        using Godot.Collections.Array arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1 }
        };
    }

    internal static bool HasUpwardFrontFaces(MeshInstance3D terrain)
    {
        using Godot.Collections.Array arrays = terrain.Mesh.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        for (int i = 0; i < indices.Length; i += 3)
        {
            Vector3 a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
            if ((b - a).Cross(c - a).Y >= 0) return false;
        }
        return indices.Length > 0;
    }

    public static MeshInstance3D CreateGrid(HeightMap map)
    {
        List<Vector3> vertices = new();
        for (float line = 0; line <= map.Width; line += 8)
            for (float p = 0; p < map.Depth; p += map.CellSize)
            {
                float end = Math.Min(p + map.CellSize, map.Depth);
                vertices.Add(new Vector3(line, map.GetHeight(line, p) + 0.06f, p));
                vertices.Add(new Vector3(line, map.GetHeight(line, end) + 0.06f, end));
            }
        for (float line = 0; line <= map.Depth; line += 8)
            for (float p = 0; p < map.Width; p += map.CellSize)
            {
                float end = Math.Min(p + map.CellSize, map.Width);
                vertices.Add(new Vector3(p, map.GetHeight(p, line) + 0.06f, line));
                vertices.Add(new Vector3(end, map.GetHeight(end, line) + 0.06f, line));
            }
        using Godot.Collections.Array arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.19f, 0.26f, 0.21f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
    }
}
