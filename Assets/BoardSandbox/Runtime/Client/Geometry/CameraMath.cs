using UnityEngine;

namespace BoardSandbox.Client.Geometry
{
    // The maths of an orbiting camera. Yaw turns the camera around the vertical axis (0 looks along +z),
    // pitch is the elevation above the table in degrees (90 looks straight down).
    public static class CameraMath
    {
        public const float MinPitch = 12f;
        public const float MaxPitch = 89f;

        // Unit vector from the target towards the camera.
        public static Vector3 Direction(float yawDegrees, float pitchDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float pitch = pitchDegrees * Mathf.Deg2Rad;
            float flat = Mathf.Cos(pitch);
            return new Vector3(Mathf.Sin(yaw) * flat, Mathf.Sin(pitch), -Mathf.Cos(yaw) * flat);
        }

        public static Vector3 Position(Vector3 target, float yawDegrees, float pitchDegrees, float distance)
        {
            return target + Direction(yawDegrees, pitchDegrees) * distance;
        }

        // Horizontal unit vectors that point to the right and away from the camera.
        public static void GroundAxes(float yawDegrees, out Vector3 right, out Vector3 forward)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            forward = new Vector3(-Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            right = new Vector3(Mathf.Cos(yaw), 0f, Mathf.Sin(yaw));
        }

        // The axes of a camera that looks at its target from the given angles.
        public static void Basis(float yawDegrees, float pitchDegrees, out Vector3 right, out Vector3 up, out Vector3 forward)
        {
            float pitch = pitchDegrees * Mathf.Deg2Rad;
            GroundAxes(yawDegrees, out right, out Vector3 ground);
            forward = -Direction(yawDegrees, pitchDegrees);
            up = new Vector3(0f, Mathf.Cos(pitch), 0f) + ground * Mathf.Sin(pitch);
        }

        public const float PitchLimit = 89f;

        public static float ClampPitch(float pitchDegrees)
        {
            return Mathf.Clamp(pitchDegrees, MinPitch, MaxPitch);
        }

        public static float ClampPitch(float pitchDegrees, float min, float max)
        {
            min = Mathf.Clamp(min, -PitchLimit, PitchLimit);
            max = Mathf.Clamp(max, min, PitchLimit);
            return Mathf.Clamp(pitchDegrees, min, max);
        }

        // How far a camera has to be for a ball around its target to fit on screen.
        public static float FitSphere(float radius, float verticalFovDegrees, float aspect, float padding = 1.15f)
        {
            float halfY = Mathf.Clamp(verticalFovDegrees, 5f, 170f) * 0.5f * Mathf.Deg2Rad;
            float halfX = Mathf.Atan(Mathf.Tan(halfY) * Mathf.Max(0.1f, aspect));
            return Mathf.Max(0.01f, radius) * padding / Mathf.Sin(Mathf.Min(halfX, halfY));
        }

        // The angles after a drag. The scene follows the cursor like a ball held in the hand: a drag to the right turns
        // it to the right (the camera goes round to the left) and a drag down tips its top towards the viewer. A camera
        // that is free to go around is not held back, and while it hangs upside down a sideways drag turns the camera
        // the other way so that the scene still follows the cursor.
        public static void Orbit(ref float yawDegrees, ref float pitchDegrees, Vector2 deltaDegrees, bool freePitch, float minPitch, float maxPitch)
        {
            if (!freePitch)
            {
                yawDegrees = WrapDegrees(yawDegrees - deltaDegrees.x);
                pitchDegrees = ClampPitch(pitchDegrees + deltaDegrees.y, minPitch, maxPitch);
                return;
            }
            bool upsideDown = Mathf.Cos(pitchDegrees * Mathf.Deg2Rad) < 0f;
            yawDegrees = WrapDegrees(yawDegrees + (upsideDown ? deltaDegrees.x : -deltaDegrees.x));
            pitchDegrees = WrapDegrees(pitchDegrees + deltaDegrees.y);
        }

        // The rotation of a camera that looks at its target from the given angles.
        public static Quaternion Orientation(float yawDegrees, float pitchDegrees)
        {
            Basis(yawDegrees, pitchDegrees, out _, out Vector3 up, out Vector3 forward);
            return Quaternion.LookRotation(forward, up);
        }

        // The rotation of a camera after a drag that turns the scene like a ball in the hand: a sideways drag turns
        // the camera around its own up, a drag up or down around its own right, wherever it has been turned to
        // before. The scene follows the cursor.
        public static Quaternion Trackball(Quaternion orientation, Vector2 deltaDegrees)
        {
            Quaternion turned = orientation * Quaternion.AngleAxis(deltaDegrees.x, Vector3.up) * Quaternion.AngleAxis(deltaDegrees.y, Vector3.right);
            return Quaternion.Normalize(turned);
        }

        // How far the target of a camera with that rotation moves so that the scene follows the cursor.
        public static Vector3 ScreenPanOffset(Quaternion orientation, Vector2 mouseDelta, float distance, float verticalFovDegrees, float screenHeight)
        {
            float unitsPerPixel = 2f * distance * Mathf.Tan(verticalFovDegrees * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, screenHeight);
            return (orientation * new Vector3(-mouseDelta.x, mouseDelta.y, 0f)) * unitsPerPixel;
        }

        public static float WrapDegrees(float degrees)
        {
            degrees %= 360f;
            return degrees < 0f ? degrees + 360f : degrees;
        }

        // How far the target moves when the mouse is dragged by the given pixels (y grows downwards)
        // so that the table follows the cursor.
        public static Vector3 PanOffset(float yawDegrees, float pitchDegrees, Vector2 mouseDelta, float distance, float verticalFovDegrees, float screenHeight)
        {
            float unitsPerPixel = 2f * distance * Mathf.Tan(verticalFovDegrees * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, screenHeight);
            float sinPitch = Mathf.Sin(Mathf.Clamp(Mathf.Abs(pitchDegrees), MinPitch, 90f) * Mathf.Deg2Rad);
            GroundAxes(yawDegrees, out Vector3 right, out Vector3 forward);
            return (-right * mouseDelta.x + forward * (mouseDelta.y / sinPitch)) * unitsPerPixel;
        }

        // Each wheel notch changes the distance by a tenth.
        public static float Zoom(float distance, float wheelNotches, float minDistance, float maxDistance)
        {
            return Mathf.Clamp(distance * Mathf.Pow(1.1f, wheelNotches), minDistance, maxDistance);
        }

        // Moves a value towards its goal; the larger the speed the faster it settles, independent of the frame rate.
        public static float Damp(float current, float goal, float speed, float deltaTime)
        {
            return Mathf.Lerp(current, goal, 1f - Mathf.Exp(-speed * deltaTime));
        }

        public static Vector3 Damp(Vector3 current, Vector3 goal, float speed, float deltaTime)
        {
            return Vector3.Lerp(current, goal, 1f - Mathf.Exp(-speed * deltaTime));
        }

        public static float DampAngle(float current, float goal, float speed, float deltaTime)
        {
            return Mathf.LerpAngle(current, goal, 1f - Mathf.Exp(-speed * deltaTime));
        }
    }
}
