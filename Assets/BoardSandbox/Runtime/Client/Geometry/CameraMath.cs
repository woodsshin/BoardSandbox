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
