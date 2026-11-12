using UnityEngine;

namespace BoardSandbox.Client.Geometry
{
    public struct PickHit
    {
        public int Id;
        public float Distance;
        public Vector3 Point;
        public Vector3 Normal;
    }

    // Where a ray meets the shapes of a scene.
    public static class ScenePicker
    {
        private const float Epsilon = 1e-6f;

        // A box around center with the given half extents along its own, rotated axes.
        public static bool RayBox(Vector3 origin, Vector3 direction, Vector3 center, Rot3 rotation, Vector3 half, out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = Vector3.zero;
            Vector3 o = rotation.Unrotate(origin - center);
            Vector3 d = rotation.Unrotate(direction);
            float near = float.NegativeInfinity;
            float far = float.PositiveInfinity;
            int nearAxis = -1;
            float nearSign = 0f;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(d[axis]) < Epsilon)
                {
                    if (Mathf.Abs(o[axis]) > half[axis])
                        return false;
                    continue;
                }
                float t1 = (-half[axis] - o[axis]) / d[axis];
                float t2 = (half[axis] - o[axis]) / d[axis];
                float sign = -1f;
                if (t1 > t2)
                {
                    float swap = t1;
                    t1 = t2;
                    t2 = swap;
                    sign = 1f;
                }
                if (t1 > near)
                {
                    near = t1;
                    nearAxis = axis;
                    nearSign = sign;
                }
                far = Mathf.Min(far, t2);
                if (near > far)
                    return false;
            }
            if (nearAxis < 0 || near < 0f)
                return false;
            Vector3 local = Vector3.zero;
            local[nearAxis] = nearSign;
            distance = near;
            normal = rotation.Rotate(local);
            return true;
        }

        // An ellipsoid around center with the given radii along its own, rotated axes.
        public static bool RayEllipsoid(Vector3 origin, Vector3 direction, Vector3 center, Rot3 rotation, Vector3 radii, out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = Vector3.zero;
            if (radii.x < Epsilon || radii.y < Epsilon || radii.z < Epsilon)
                return false;
            Vector3 o = Divide(rotation.Unrotate(origin - center), radii);
            Vector3 d = Divide(rotation.Unrotate(direction), radii);
            float a = Vector3.Dot(d, d);
            float b = 2f * Vector3.Dot(o, d);
            float c = Vector3.Dot(o, o) - 1f;
            float discriminant = b * b - 4f * a * c;
            if (a < Epsilon || discriminant < 0f)
                return false;
            float t = (-b - Mathf.Sqrt(discriminant)) / (2f * a);
            if (t < 0f)
                return false;
            distance = t;
            normal = rotation.Rotate(Divide(o + d * t, radii)).normalized;
            return true;
        }

        // Where a ray meets the plane through a point; false when it runs alongside or away from it.
        public static bool RayPlane(Vector3 origin, Vector3 direction, Vector3 point, Vector3 normal, out Vector3 hit)
        {
            hit = Vector3.zero;
            float along = Vector3.Dot(direction, normal);
            if (Mathf.Abs(along) < Epsilon)
                return false;
            float t = Vector3.Dot(point - origin, normal) / along;
            if (t < 0f)
                return false;
            hit = origin + direction * t;
            return true;
        }

        private static Vector3 Divide(Vector3 v, Vector3 by)
        {
            return new Vector3(v.x / by.x, v.y / by.y, v.z / by.z);
        }
    }
}
