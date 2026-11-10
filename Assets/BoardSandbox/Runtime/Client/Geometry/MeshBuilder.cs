using System.Collections.Generic;
using UnityEngine;

namespace BoardSandbox.Client.Geometry
{
    // Collects the vertices of procedural shapes. Every triangle is wound so that its front side agrees with
    // the normals of its vertices, so the shapes only have to provide positions and normals.
    public sealed class MeshBuilder
    {
        private const int MinSegments = 6;

        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<Color32> _colors = new List<Color32>();
        private readonly List<int> _triangles = new List<int>();

        public int VertexCount => _vertices.Count;
        public int TriangleCount => _triangles.Count / 3;

        // The lists the mesh is made of; they are meant to be read only.
        public List<Vector3> VertexList => _vertices;
        public List<Vector3> NormalList => _normals;
        public List<Color32> ColorList => _colors;
        public List<int> TriangleList => _triangles;

        public int AddVertex(Vector3 position, Vector3 normal, Color32 color)
        {
            _vertices.Add(position);
            _normals.Add(normal.normalized);
            _colors.Add(color);
            return _vertices.Count - 1;
        }

        // Triangles without any area are dropped.
        public void AddTriangle(int a, int b, int c)
        {
            Vector3 face = Vector3.Cross(_vertices[b] - _vertices[a], _vertices[c] - _vertices[a]);
            if (face.sqrMagnitude < 1e-12f)
                return;
            Vector3 hint = _normals[a] + _normals[b] + _normals[c];
            if (Vector3.Dot(face, hint) < 0f)
            {
                int swap = b;
                b = c;
                c = swap;
            }
            _triangles.Add(a);
            _triangles.Add(b);
            _triangles.Add(c);
        }

        // A flat quad given by its corners in order around it; it faces the side the normal points to.
        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color32 color)
        {
            int i0 = AddVertex(a, normal, color);
            int i1 = AddVertex(b, normal, color);
            int i2 = AddVertex(c, normal, color);
            int i3 = AddVertex(d, normal, color);
            AddTriangle(i0, i1, i2);
            AddTriangle(i0, i2, i3);
        }

        public void AddBox(Vector3 center, Vector3 size, Color32 color)
        {
            Vector3 half = size * 0.5f;
            for (int axis = 0; axis < 3; axis++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    int uAxis = (axis + 1) % 3;
                    int vAxis = (axis + 2) % 3;
                    Vector3 normal = Vector3.zero;
                    normal[axis] = sign;
                    Vector3 u = Vector3.zero;
                    u[uAxis] = half[uAxis];
                    Vector3 v = Vector3.zero;
                    v[vAxis] = half[vAxis];
                    Vector3 middle = center + normal * half[axis];
                    AddQuad(middle - u - v, middle + u - v, middle + u + v, middle - u + v, normal, color);
                }
            }
        }

        // A flat disc lying in the horizontal plane.
        public void AddDisc(Vector3 center, float radius, int segments, Color32 color, bool facingUp = true)
        {
            segments = Mathf.Max(segments, MinSegments);
            Vector3 normal = facingUp ? Vector3.up : Vector3.down;
            int middle = AddVertex(center, normal, color);
            int first = _vertices.Count;
            for (int i = 0; i < segments; i++)
            {
                float angle = 2f * Mathf.PI * i / segments;
                AddVertex(center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius), normal, color);
            }
            for (int i = 0; i < segments; i++)
                AddTriangle(middle, first + i, first + (i + 1) % segments);
        }

        // A flat ring lying in the horizontal plane, facing up.
        public void AddRing(Vector3 center, float innerRadius, float outerRadius, int segments, Color32 color)
        {
            segments = Mathf.Max(segments, MinSegments);
            int first = _vertices.Count;
            for (int i = 0; i < segments; i++)
            {
                float angle = 2f * Mathf.PI * i / segments;
                Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                AddVertex(center + direction * innerRadius, Vector3.up, color);
                AddVertex(center + direction * outerRadius, Vector3.up, color);
            }
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                AddTriangle(first + i * 2, first + i * 2 + 1, first + next * 2);
                AddTriangle(first + i * 2 + 1, first + next * 2 + 1, first + next * 2);
            }
        }

        // An upright cylinder whose bottom disc is centred on baseCenter.
        public void AddCylinder(Vector3 baseCenter, float radius, float height, int segments, Color32 color)
        {
            segments = Mathf.Max(segments, MinSegments);
            int first = _vertices.Count;
            Vector3 lift = new Vector3(0f, height, 0f);
            for (int i = 0; i < segments; i++)
            {
                float angle = 2f * Mathf.PI * i / segments;
                Vector3 radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                AddVertex(baseCenter + radial * radius, radial, color);
                AddVertex(baseCenter + radial * radius + lift, radial, color);
            }
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                int bottom = first + i * 2;
                int top = bottom + 1;
                int nextBottom = first + next * 2;
                int nextTop = nextBottom + 1;
                AddTriangle(bottom, top, nextBottom);
                AddTriangle(top, nextTop, nextBottom);
            }
            AddDisc(baseCenter + lift, radius, segments, color, true);
            AddDisc(baseCenter, radius, segments, color, false);
        }

        // A flattened sphere standing on baseCenter; the shape of a go stone.
        public void AddStone(Vector3 baseCenter, float radius, float height, int segments, int rings, Color32 color)
        {
            segments = Mathf.Max(segments, MinSegments);
            rings = Mathf.Max(rings, 3);
            float halfHeight = height * 0.5f;
            Vector3 center = baseCenter + new Vector3(0f, halfHeight, 0f);
            int first = _vertices.Count;
            for (int ring = 0; ring <= rings; ring++)
            {
                float theta = Mathf.PI * ring / rings;
                float sinTheta = Mathf.Sin(theta);
                float cosTheta = Mathf.Cos(theta);
                for (int i = 0; i < segments; i++)
                {
                    float phi = 2f * Mathf.PI * i / segments;
                    float cosPhi = Mathf.Cos(phi);
                    float sinPhi = Mathf.Sin(phi);
                    Vector3 position = center + new Vector3(radius * sinTheta * cosPhi, halfHeight * cosTheta, radius * sinTheta * sinPhi);
                    Vector3 normal = new Vector3(sinTheta * cosPhi / radius, cosTheta / halfHeight, sinTheta * sinPhi / radius);
                    AddVertex(position, normal, color);
                }
            }
            for (int ring = 0; ring < rings; ring++)
            {
                for (int i = 0; i < segments; i++)
                {
                    int next = (i + 1) % segments;
                    int upper = first + ring * segments + i;
                    int upperNext = first + ring * segments + next;
                    int lower = first + (ring + 1) * segments + i;
                    int lowerNext = first + (ring + 1) * segments + next;
                    AddTriangle(upper, lower, upperNext);
                    AddTriangle(upperNext, lower, lowerNext);
                }
            }
        }
    }
}
