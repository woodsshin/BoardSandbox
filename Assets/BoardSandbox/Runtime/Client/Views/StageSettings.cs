using System;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using UnityEngine;

namespace BoardSandbox.Client.Views
{
    public enum DragMode
    {
        None,
        Pan,
        Orbit
    }

    public sealed class TableSettings
    {
        public bool Visible = true;
        public Color32 Color = new Color32(0x27, 0x31, 0x3b, 255);
        public float Size = 800f;

        // Height of the cloth; NaN puts it right under the board.
        public float Y = float.NaN;
    }

    public sealed class BoardSettings
    {
        public bool Visible = true;
        public BoardStyle Style = BoardStyle.Lines;

        // 0 when the mod does not say.
        public int Columns;
        public int Rows;
        public float Cell = 1f;

        // In cells; negative for the usual value.
        public float Margin = -1f;
        public float Thickness = -1f;
        public BoardPalette Palette = new BoardPalette();
    }

    public sealed class LightSettings
    {
        // Points from the scene towards the light, in the coordinates of mods.
        public Vector3 Direction = new Vector3(-0.35f, 0.85f, 0.4f);
        public Color32 Color = new Color32(255, 255, 255, 255);
        public float Ambient = 0.45f;
        public float Intensity = 0.65f;
    }

    public sealed class CameraSettings
    {
        public float Yaw;
        public float Pitch = 55f;
        public float Fov = 40f;
        public Vector3 Target;

        // 0 lets the camera find the distance itself.
        public float Distance;

        // The size of what has to fit on screen; 0 fits the board.
        public float Radius;
        public float MinDistance;
        public float MaxDistance;
        public float MinPitch = CameraMath.MinPitch;
        public float MaxPitch = CameraMath.MaxPitch;

        // The camera may go all the way around over the top and under the scene.
        public bool FreePitch;
        public DragMode LeftDrag = DragMode.Pan;
        public DragMode RightDrag = DragMode.Orbit;
        public DragMode MiddleDrag = DragMode.Pan;
        public bool Zoom = true;
    }

    public sealed class MarkerSettings
    {
        public bool Visible = true;
        public Color32 Color;
        public float Size = 1f;
    }

    // How the table of a game looks. A mod sets it in the "stage" part of its client hints and changes it while
    // the game runs with "stage" events; whatever neither of them mentions keeps its usual value.
    public sealed class StageSettings
    {
        public static readonly Color32 DefaultBackground = new Color32(0x13, 0x16, 0x1a, 255);

        public Color32 Background = DefaultBackground;
        public TableSettings Table = new TableSettings();
        public BoardSettings Board = new BoardSettings();
        public LightSettings Light = new LightSettings();
        public CameraSettings Camera = new CameraSettings();
        public MarkerSettings LastMove = new MarkerSettings { Color = new Color32(0xe5, 0x3d, 0x2f, 255) };
        public MarkerSettings Win = new MarkerSettings { Color = new Color32(0xf2, 0xc2, 0x3a, 235) };
        public MarkerSettings Cursor = new MarkerSettings { Color = new Color32(255, 255, 255, 190) };
        public MarkerSettings Preview = new MarkerSettings { Color = new Color32(255, 255, 255, 120) };
        public MarkerSettings Shadow = new MarkerSettings { Color = new Color32(0, 0, 0, 80) };

        // The merged description this was read from; two stages with equal sources are equal.
        public JsonValue Source { get; private set; } = JsonValue.NewObject();

        public static StageSettings Resolve(JsonValue client, ViewEvent stageEvent)
        {
            JsonValue merged = JsonValue.NewObject();
            if (client != null && client.IsObject)
            {
                if (client["board"].IsObject)
                    merged.Set("board", client["board"].DeepClone());
                if (client["stage"].IsObject)
                    merged = JsonMerge.Deep(merged, client["stage"]);
            }
            if (stageEvent != null && stageEvent.Raw.IsObject)
            {
                merged = JsonMerge.Deep(merged, stageEvent.Raw);
                merged.Remove("t");
            }
            return Parse(merged);
        }

        public static StageSettings Parse(JsonValue root)
        {
            StageSettings stage = new StageSettings();
            if (root == null || !root.IsObject)
                return stage;
            stage.Source = root;
            stage.Background = Opaque(ReadColor(root["background"], stage.Background));

            JsonValue table = root["table"];
            stage.Table.Visible = table["visible"].AsBool(true);
            stage.Table.Color = Opaque(ReadColor(table["color"], stage.Table.Color));
            stage.Table.Size = ReadFloat(table["size"], stage.Table.Size, 1f, 4000f);
            if (table["y"].IsNumber)
                stage.Table.Y = ReadFloat(table["y"], 0f, -Frame.Limit, Frame.Limit);

            JsonValue board = root["board"];
            BoardSettings b = stage.Board;
            b.Visible = board["visible"].AsBool(true);
            b.Style = string.Equals(board["style"].AsString(), "checker", StringComparison.OrdinalIgnoreCase) ? BoardStyle.Checker : BoardStyle.Lines;
            b.Columns = Math.Max(0, board["columns"].AsInt(0));
            b.Rows = Math.Max(0, board["rows"].AsInt(0));
            b.Cell = ReadFloat(board["cell"], 1f, 0.05f, 20f);
            b.Margin = board["margin"].IsNumber ? ReadFloat(board["margin"], 0f, 0f, 20f) : -1f;
            b.Thickness = board["thickness"].IsNumber ? ReadFloat(board["thickness"], 0.4f, 0.01f, 20f) : -1f;
            JsonValue colors = board["colors"];
            b.Palette.Slab = Opaque(ReadColor(colors["slab"], b.Palette.Slab));
            b.Palette.Line = Opaque(ReadColor(colors["line"], b.Palette.Line));
            b.Palette.Light = Opaque(ReadColor(colors["light"], b.Palette.Light));
            b.Palette.Dark = Opaque(ReadColor(colors["dark"], b.Palette.Dark));
            b.Palette.ThinLine = ReadFloat(board["lines"]["thin"], b.Palette.ThinLine, 0f, 0.5f);
            b.Palette.ThickLine = ReadFloat(board["lines"]["thick"], b.Palette.ThickLine, 0f, 0.5f);
            b.Palette.Stars = board["stars"].AsBool(true);

            JsonValue light = root["light"];
            if (Frame.TryReadVector(light["direction"], out Vector3 direction) && direction.sqrMagnitude > 1e-6f)
                stage.Light.Direction = direction.normalized;
            stage.Light.Color = Opaque(ReadColor(light["color"], stage.Light.Color));
            stage.Light.Ambient = ReadFloat(light["ambient"], stage.Light.Ambient, 0f, 2f);
            stage.Light.Intensity = ReadFloat(light["intensity"], stage.Light.Intensity, 0f, 4f);

            JsonValue camera = root["camera"];
            CameraSettings c = stage.Camera;
            c.Yaw = ReadFloat(camera["yaw"], 0f, -36000f, 36000f);
            c.MinPitch = ReadFloat(camera["min_pitch"], c.MinPitch, -CameraMath.PitchLimit, CameraMath.PitchLimit);
            c.MaxPitch = ReadFloat(camera["max_pitch"], c.MaxPitch, c.MinPitch, CameraMath.PitchLimit);
            c.FreePitch = camera["free_pitch"].AsBool(false);
            c.Pitch = c.FreePitch
                ? ReadFloat(camera["pitch"], c.Pitch, -36000f, 36000f)
                : ReadFloat(camera["pitch"], Mathf.Clamp(c.Pitch, c.MinPitch, c.MaxPitch), c.MinPitch, c.MaxPitch);
            c.Fov = ReadFloat(camera["fov"], c.Fov, 10f, 100f);
            if (Frame.TryReadVector(camera["target"], out Vector3 target))
                c.Target = target;
            c.Distance = ReadFloat(camera["distance"], 0f, 0f, 5000f);
            c.Radius = ReadFloat(camera["radius"], 0f, 0f, 2000f);
            c.MinDistance = ReadFloat(camera["min_distance"], 0f, 0f, 5000f);
            c.MaxDistance = ReadFloat(camera["max_distance"], 0f, 0f, 20000f);
            c.LeftDrag = ReadDrag(camera["left_drag"], c.LeftDrag);
            c.RightDrag = ReadDrag(camera["right_drag"], c.RightDrag);
            c.MiddleDrag = ReadDrag(camera["middle_drag"], c.MiddleDrag);
            c.Zoom = camera["zoom"].AsBool(true);

            JsonValue markers = root["markers"];
            ReadMarker(markers["last_move"], stage.LastMove);
            ReadMarker(markers["win"], stage.Win);
            ReadMarker(markers["cursor"], stage.Cursor);
            ReadMarker(markers["preview"], stage.Preview);
            ReadMarker(markers["shadow"], stage.Shadow);
            return stage;
        }

        public bool SameAs(StageSettings other)
        {
            return other != null && Source.StructurallyEquals(other.Source);
        }

        public BoardLayout Layout(int columns, int rows)
        {
            return new BoardLayout(columns, rows, Board.Style, Board.Cell, Board.Margin, Board.Thickness);
        }

        private static void ReadMarker(JsonValue value, MarkerSettings marker)
        {
            marker.Visible = value["visible"].AsBool(true);
            marker.Color = ReadColor(value["color"], marker.Color);
            marker.Size = ReadFloat(value["size"], 1f, 0.05f, 20f);
        }

        private static DragMode ReadDrag(JsonValue value, DragMode fallback)
        {
            switch (value.AsString().ToLowerInvariant())
            {
                case "pan":
                    return DragMode.Pan;
                case "orbit":
                    return DragMode.Orbit;
                case "none":
                    return DragMode.None;
                default:
                    return fallback;
            }
        }

        public static Color32 ReadColor(JsonValue value, Color32 fallback)
        {
            return value != null && value.IsString ? ColorParser.Parse(value.AsString(), fallback) : fallback;
        }

        public static float ReadFloat(JsonValue value, float fallback, float min, float max)
        {
            if (value == null || !value.IsNumber)
                return fallback;
            double number = value.AsDouble();
            if (double.IsNaN(number) || double.IsInfinity(number))
                return fallback;
            return Mathf.Clamp((float)number, min, max);
        }

        private static Color32 Opaque(Color32 color)
        {
            return new Color32(color.r, color.g, color.b, 255);
        }
    }

    // Where the camera starts and how far it may go, worked out for one screen shape.
    public sealed class CameraPlan
    {
        public float Yaw;
        public float Pitch;
        public float Fov;
        public Vector3 Target;
        public float Distance;
        public float MinDistance;
        public float MaxDistance;
        public float MinPitch;
        public float MaxPitch;
        public bool FreePitch;

        // How far the target may be moved away from where it starts.
        public Vector3 Reach;

        // The layout is null when no board is shown.
        public static CameraPlan Resolve(CameraSettings settings, BoardLayout layout, float aspect)
        {
            CameraPlan plan = new CameraPlan
            {
                Yaw = settings.Yaw,
                Pitch = settings.Pitch,
                Fov = settings.Fov,
                Target = Frame.Flip(settings.Target),
                MinPitch = settings.MinPitch,
                MaxPitch = settings.MaxPitch,
                FreePitch = settings.FreePitch
            };
            float extent;
            if (settings.Distance > 0f)
            {
                plan.Distance = settings.Distance;
                extent = settings.Radius > 0f ? settings.Radius : layout != null ? Mathf.Max(layout.SlabHalfWidth, layout.SlabHalfDepth) : settings.Distance * 0.35f;
            }
            else if (settings.Radius > 0f || layout == null)
            {
                extent = settings.Radius > 0f ? settings.Radius : 6f;
                plan.Distance = CameraMath.FitSphere(extent, settings.Fov, aspect);
            }
            else
            {
                extent = Mathf.Max(layout.SlabHalfWidth, layout.SlabHalfDepth);
                plan.Distance = layout.FitDistance(settings.Fov, aspect, settings.Yaw, Mathf.Max(1f, Mathf.Abs(settings.Pitch)));
            }
            plan.MinDistance = settings.MinDistance > 0f ? settings.MinDistance : Mathf.Max(0.5f, layout != null && settings.Radius <= 0f ? layout.CellSize * 2f : extent * 0.6f);
            plan.MaxDistance = settings.MaxDistance > 0f ? settings.MaxDistance : plan.Distance * 3f;
            plan.MinDistance = Mathf.Min(plan.MinDistance, plan.Distance);
            plan.MaxDistance = Mathf.Max(plan.MaxDistance, plan.Distance);
            plan.Reach = layout != null && settings.Radius <= 0f
                ? new Vector3(layout.SlabHalfWidth, 0f, layout.SlabHalfDepth)
                : new Vector3(extent, extent, extent);
            return plan;
        }
    }
}
