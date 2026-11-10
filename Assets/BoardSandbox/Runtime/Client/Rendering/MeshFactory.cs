using BoardSandbox.Client.Geometry;
using UnityEngine;
using UnityEngine.Rendering;

namespace BoardSandbox.Client.Rendering
{
    public static class MeshFactory
    {
        private const int SixteenBitLimit = 65000;

        public static Mesh Create(MeshBuilder builder, string name)
        {
            Mesh mesh = new Mesh { name = name };
            if (builder.VertexCount > SixteenBitLimit)
                mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(builder.VertexList);
            mesh.SetNormals(builder.NormalList);
            mesh.SetColors(builder.ColorList);
            mesh.SetTriangles(builder.TriangleList, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
