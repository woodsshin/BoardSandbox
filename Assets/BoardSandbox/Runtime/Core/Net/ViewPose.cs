using System;
using BoardSandbox.Core.Json;

namespace BoardSandbox.Core.Net
{
    // How a player looks at their table: from which side and how close. In a game where everybody has a table of their own, the
    // players see each other's tables the way their owners look at them, so that one can follow what the other does to the cube.
    // Nothing in it depends on the screen it was made on: the zoom is the distance of the camera in units of the distance that
    // frames the table on that screen, and the turn is the same wherever it is shown.
    public sealed class ViewPose
    {
        public const float MinZoom = 0.02f;
        public const float MaxZoom = 50f;
        public const float MaxAngle = 36000f;

        // How far two views may be apart in degrees, and as a part of the zoom, to count as one.
        public const float AngleTolerance = 0.25f;
        public const float ZoomTolerance = 0.002f;

        private ViewPose(bool hasTurn, float x, float y, float z, float w, float yaw, float pitch, float zoom)
        {
            HasTurn = hasTurn;
            TurnX = x;
            TurnY = y;
            TurnZ = z;
            TurnW = w;
            Yaw = yaw;
            Pitch = pitch;
            Zoom = zoom;
        }

        // True for a camera that turns like a ball in the hand: TurnX to TurnW, a unit quaternion, say where it is turned to.
        public bool HasTurn { get; }
        public float TurnX { get; }
        public float TurnY { get; }
        public float TurnZ { get; }
        public float TurnW { get; }

        // The angles of a camera that circles the table, in degrees. A camera that turns freely keeps the ones it started with.
        public float Yaw { get; }
        public float Pitch { get; }

        // The distance of the camera as a multiple of the distance that frames the table.
        public float Zoom { get; }

        // A camera that turns like a ball in the hand. A turn that is not a quaternion (all zero, or not a number) is left out.
        public static ViewPose Trackball(float x, float y, float z, float w, float yaw, float pitch, float zoom)
        {
            double length = Math.Sqrt((double)x * x + (double)y * y + (double)z * z + (double)w * w);
            if (!Number(x) || !Number(y) || !Number(z) || !Number(w) || length < 1e-6)
                return Orbit(yaw, pitch, zoom);
            return new ViewPose(true, (float)(x / length), (float)(y / length), (float)(z / length), (float)(w / length), Angle(yaw), Angle(pitch), Scale(zoom));
        }

        // A camera that circles the table at the given angles.
        public static ViewPose Orbit(float yaw, float pitch, float zoom)
        {
            return new ViewPose(false, 0f, 0f, 0f, 1f, Angle(yaw), Angle(pitch), Scale(zoom));
        }

        // The angle in degrees between the two turns; 0 where one of the views has none.
        public float TurnAngleTo(ViewPose other)
        {
            if (other == null || !HasTurn || !other.HasTurn)
                return 0f;
            double dot = Math.Abs((double)TurnX * other.TurnX + (double)TurnY * other.TurnY + (double)TurnZ * other.TurnZ + (double)TurnW * other.TurnW);
            return (float)(2.0 * Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI);
        }

        // Whether the other view is so close to this one that nobody could tell them apart.
        public bool Near(ViewPose other)
        {
            if (other == null || HasTurn != other.HasTurn)
                return false;
            if (HasTurn && TurnAngleTo(other) > AngleTolerance)
                return false;
            if (Math.Abs(Between(Yaw, other.Yaw)) > AngleTolerance || Math.Abs(Between(Pitch, other.Pitch)) > AngleTolerance)
                return false;
            return Math.Abs(Zoom - other.Zoom) <= ZoomTolerance * Math.Max(Zoom, other.Zoom);
        }

        // The members of a message that carries this view.
        public void WriteTo(JsonValue message)
        {
            message.Set("yaw", Math.Round(Yaw, 2)).Set("pitch", Math.Round(Pitch, 2)).Set("zoom", Math.Round(Zoom, 4));
            if (HasTurn)
                message.Set("turn", JsonValue.NewArray().Add(Math.Round(TurnX, 5)).Add(Math.Round(TurnY, 5)).Add(Math.Round(TurnZ, 5)).Add(Math.Round(TurnW, 5)));
        }

        // Reads a view from the message of somebody else. Whatever is wrong with it is not trusted: numbers are put in their range and a
        // message without a zoom is no view at all.
        public static bool TryRead(JsonValue message, out ViewPose pose)
        {
            pose = null;
            if (message == null || !message.IsObject || !message["zoom"].IsNumber)
                return false;
            float zoom = (float)message["zoom"].AsDouble(1);
            float yaw = (float)message["yaw"].AsDouble(0);
            float pitch = (float)message["pitch"].AsDouble(0);
            JsonValue turn = message["turn"];
            if (turn.IsArray && turn.Count == 4 && turn[0].IsNumber && turn[1].IsNumber && turn[2].IsNumber && turn[3].IsNumber)
            {
                pose = Trackball((float)turn[0].AsDouble(), (float)turn[1].AsDouble(), (float)turn[2].AsDouble(), (float)turn[3].AsDouble(), yaw, pitch, zoom);
                return true;
            }
            pose = Orbit(yaw, pitch, zoom);
            return true;
        }

        private static bool Number(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float Angle(float degrees)
        {
            return Number(degrees) ? Math.Max(-MaxAngle, Math.Min(MaxAngle, degrees)) : 0f;
        }

        private static float Scale(float zoom)
        {
            return Number(zoom) ? Math.Max(MinZoom, Math.Min(MaxZoom, zoom)) : 1f;
        }

        // The way from one angle to the other, in -180 to 180.
        private static float Between(float from, float to)
        {
            float turn = (to - from) % 360f;
            return turn > 180f ? turn - 360f : turn < -180f ? turn + 360f : turn;
        }
    }
}
