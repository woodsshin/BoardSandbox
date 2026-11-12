using BoardSandbox.Core.Json;
using UnityEngine;

namespace BoardSandbox.Client.Geometry
{
    // A rotation as a 3x3 matrix that turns column vectors.
    public readonly struct Rot3
    {
        public static readonly Rot3 Identity = new Rot3(new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f));

        // The rows of the matrix.
        public readonly Vector3 Row0;
        public readonly Vector3 Row1;
        public readonly Vector3 Row2;

        public Rot3(Vector3 row0, Vector3 row1, Vector3 row2)
        {
            Row0 = row0;
            Row1 = row1;
            Row2 = row2;
        }

        // The images of the three axes.
        public Vector3 AxisX => new Vector3(Row0.x, Row1.x, Row2.x);
        public Vector3 AxisY => new Vector3(Row0.y, Row1.y, Row2.y);
        public Vector3 AxisZ => new Vector3(Row0.z, Row1.z, Row2.z);

        public Rot3 Transposed => new Rot3(AxisX, AxisY, AxisZ);

        public Vector3 Rotate(Vector3 v)
        {
            return new Vector3(Vector3.Dot(Row0, v), Vector3.Dot(Row1, v), Vector3.Dot(Row2, v));
        }

        // The vector that Rotate turns into v.
        public Vector3 Unrotate(Vector3 v)
        {
            return new Vector3(Vector3.Dot(AxisX, v), Vector3.Dot(AxisY, v), Vector3.Dot(AxisZ, v));
        }

        // First b, then a.
        public static Rot3 operator *(Rot3 a, Rot3 b)
        {
            Vector3 x = a.Rotate(b.AxisX);
            Vector3 y = a.Rotate(b.AxisY);
            Vector3 z = a.Rotate(b.AxisZ);
            return new Rot3(new Vector3(x.x, y.x, z.x), new Vector3(x.y, y.y, z.y), new Vector3(x.z, y.z, z.z));
        }

        // Counter-clockwise by the right hand rule when the coordinates are right-handed.
        public static Rot3 AxisAngle(Vector3 axis, float degrees)
        {
            if (axis.sqrMagnitude < 1e-12f)
                return Identity;
            Vector3 a = axis.normalized;
            float radians = degrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(radians);
            float s = Mathf.Sin(radians);
            float t = 1f - c;
            return new Rot3(
                new Vector3(t * a.x * a.x + c, t * a.x * a.y - s * a.z, t * a.x * a.z + s * a.y),
                new Vector3(t * a.x * a.y + s * a.z, t * a.y * a.y + c, t * a.y * a.z - s * a.x),
                new Vector3(t * a.x * a.z - s * a.y, t * a.y * a.z + s * a.x, t * a.z * a.z + c));
        }

        // Turns around x, then around y, then around z, all fixed axes.
        public static Rot3 Euler(float xDegrees, float yDegrees, float zDegrees)
        {
            return AxisAngle(new Vector3(0f, 0f, 1f), zDegrees) * AxisAngle(new Vector3(0f, 1f, 0f), yDegrees) * AxisAngle(new Vector3(1f, 0f, 0f), xDegrees);
        }

        // Three numbers are angles in degrees, nine are the matrix row by row.
        public static bool TryRead(JsonValue value, out Rot3 rotation)
        {
            rotation = Identity;
            if (value == null || !value.IsArray)
                return false;
            for (int i = 0; i < value.Count; i++)
            {
                if (!value[i].IsNumber || !IsFinite(value[i].AsDouble()))
                    return false;
            }
            if (value.Count == 3)
            {
                rotation = Euler((float)value[0].AsDouble(), (float)value[1].AsDouble(), (float)value[2].AsDouble());
                return true;
            }
            if (value.Count != 9)
                return false;
            Rot3 raw = new Rot3(Read(value, 0), Read(value, 3), Read(value, 6));
            return raw.TryOrthonormalize(out rotation);
        }

        // The matrix of the same rotation after the z axis has been flipped.
        public Rot3 Mirrored => new Rot3(
            new Vector3(Row0.x, Row0.y, -Row0.z),
            new Vector3(Row1.x, Row1.y, -Row1.z),
            new Vector3(-Row2.x, -Row2.y, Row2.z));

        public Quaternion ToQuaternion()
        {
            float m00 = Row0.x, m11 = Row1.y, m22 = Row2.z;
            float trace = m00 + m11 + m22;
            float x, y, z, w;
            if (trace > 0f)
            {
                float s = Mathf.Sqrt(trace + 1f) * 2f;
                w = 0.25f * s;
                x = (Row2.y - Row1.z) / s;
                y = (Row0.z - Row2.x) / s;
                z = (Row1.x - Row0.y) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
                w = (Row2.y - Row1.z) / s;
                x = 0.25f * s;
                y = (Row0.y + Row1.x) / s;
                z = (Row0.z + Row2.x) / s;
            }
            else if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
                w = (Row0.z - Row2.x) / s;
                x = (Row0.y + Row1.x) / s;
                y = 0.25f * s;
                z = (Row1.z + Row2.y) / s;
            }
            else
            {
                float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
                w = (Row1.x - Row0.y) / s;
                x = (Row0.z + Row2.x) / s;
                y = (Row1.z + Row2.y) / s;
                z = 0.25f * s;
            }
            float length = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            return length < 1e-6f ? new Quaternion(0f, 0f, 0f, 1f) : new Quaternion(x / length, y / length, z / length, w / length);
        }

        private bool TryOrthonormalize(out Rot3 result)
        {
            result = Identity;
            Vector3 x = AxisX;
            Vector3 y = AxisY;
            if (x.sqrMagnitude < 1e-8f)
                return false;
            x = x.normalized;
            y -= x * Vector3.Dot(x, y);
            if (y.sqrMagnitude < 1e-8f)
                return false;
            y = y.normalized;
            Vector3 z = Vector3.Cross(x, y);
            if (Vector3.Dot(z, AxisZ) <= 0f)
                return false;
            result = new Rot3(new Vector3(x.x, y.x, z.x), new Vector3(x.y, y.y, z.y), new Vector3(x.z, y.z, z.z));
            return true;
        }

        private static Vector3 Read(JsonValue array, int offset)
        {
            return new Vector3((float)array[offset].AsDouble(), (float)array[offset + 1].AsDouble(), (float)array[offset + 2].AsDouble());
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    // Mods describe space with x to the right, y up and z towards the viewer; the scene has z pointing away.
    public static class Frame
    {
        public static Vector3 Flip(Vector3 v)
        {
            return new Vector3(v.x, v.y, -v.z);
        }

        public static Rot3 ToScene(Rot3 rotation)
        {
            return rotation.Mirrored;
        }

        // A turn around an axis as mods give it, as a rotation of the scene.
        public static Rot3 TurnToScene(Vector3 axis, float degrees)
        {
            return Rot3.AxisAngle(Flip(axis), -degrees);
        }

        public static bool TryReadVector(JsonValue value, out Vector3 vector)
        {
            vector = Vector3.zero;
            if (value == null || !value.IsArray || value.Count != 3)
                return false;
            float[] parts = new float[3];
            for (int i = 0; i < 3; i++)
            {
                double part = value[i].AsDouble(double.NaN);
                if (!value[i].IsNumber || double.IsNaN(part) || double.IsInfinity(part))
                    return false;
                parts[i] = Mathf.Clamp((float)part, -Limit, Limit);
            }
            vector = new Vector3(parts[0], parts[1], parts[2]);
            return true;
        }

        public const float Limit = 100000f;
    }
}
