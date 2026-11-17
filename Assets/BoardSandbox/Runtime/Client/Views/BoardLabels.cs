using BoardSandbox.Client.Geometry;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using UnityEngine;

namespace BoardSandbox.Client.Views
{
    // Writes text on top of objects. The objects are meshes and have no text, so it is drawn by the GUI at the place
    // on the screen where the top of each object is. Call Draw from OnGUI before the interface is drawn, so that
    // the panels cover the text and not the other way round.
    //
    // An object has one "label" in its middle, such as the digit of a sudoku ("label_size", "label_color" and
    // "label_bold" say how it is written), and any number of "labels": a list of { text, dx, dy, size, color, bold }
    // that are written smaller and thinner at an offset from the middle, such as the notes in a cell. Sizes and
    // offsets are shares of the width of the object; dx goes to the right and dy towards the player.
    public sealed class BoardLabels
    {
        public const int MaxLabelsPerObject = 32;

        // Text smaller than this cannot be read and is left out.
        private const int MinFontSize = 6;
        private const int MaxFontSize = 160;
        private const float SmallLabelSize = 0.25f;
        private const float MaxOffset = 4f;

        private static readonly Color32 DarkText = new Color32(20, 23, 28, 255);
        private static readonly Color32 LightText = new Color32(247, 247, 242, 255);

        private Rect _area;
        private bool _grouped;

        // area is the part of the screen the scene is shown in, in the coordinates of the GUI; text does not leave it.
        public void Draw(ViewModel view, SceneView scene, Camera camera, Rect area, GUIStyle style)
        {
            if (Event.current.type != EventType.Repaint || view == null || scene == null || camera == null || style == null)
                return;

            _area = area;
            _grouped = false;
            float tanHalfFov = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float viewHeight = camera.pixelHeight;
            foreach (ViewObject piece in view.Objects)
            {
                JsonValue props = piece.PropsFor(scene.ViewerSeat);
                string label = props["label"].AsString();
                JsonValue list = props["labels"];
                bool many = list.IsArray && list.Count > 0;
                if (label.Length == 0 && !many)
                    continue;
                if (!scene.TryGetTop(piece.Id, out Vector3 top, out float size, out ObjectLook look))
                    continue;

                Color32 contrast = look.IsLight ? DarkText : LightText;
                Color32 color = look.LabelColor ?? contrast;
                if (label.Length > 0)
                    Write(camera, style, viewHeight, tanHalfFov, top, label, look.LabelSize * size, color, look.LabelBold);
                if (!many)
                    continue;

                int count = Mathf.Min(list.Count, MaxLabelsPerObject);
                for (int i = 0; i < count; i++)
                {
                    JsonValue item = list[i];
                    string text = item["text"].AsString();
                    if (text.Length == 0)
                        continue;
                    float dx = StageSettings.ReadFloat(item["dx"], 0f, -MaxOffset, MaxOffset);
                    float dy = StageSettings.ReadFloat(item["dy"], 0f, -MaxOffset, MaxOffset);
                    float share = StageSettings.ReadFloat(item["size"], SmallLabelSize, ObjectLook.MinLabelSize, ObjectLook.MaxLabelSize);
                    // Towards the player is -z in the scene.
                    Vector3 at = top + new Vector3(dx, 0f, -dy) * size;
                    Write(camera, style, viewHeight, tanHalfFov, at, text, share * size, StageSettings.ReadColor(item["color"], color),
                        item["bold"].AsBool(false));
                }
            }
            if (_grouped)
                GUI.EndGroup();
        }

        private void Write(Camera camera, GUIStyle style, float viewHeight, float tanHalfFov, Vector3 world, string text, float height,
            Color32 color, bool bold)
        {
            Vector3 screen = camera.WorldToScreenPoint(world);
            if (screen.z <= 0f)
                return;

            // How many pixels one unit of the scene covers at the depth of the text.
            float pixelsPerUnit = viewHeight * 0.5f / (screen.z * tanHalfFov);
            int fontSize = Mathf.RoundToInt(pixelsPerUnit * height);
            if (fontSize < MinFontSize)
                return;

            if (!_grouped)
            {
                GUI.BeginGroup(_area);
                _grouped = true;
            }
            style.fontSize = Mathf.Min(fontSize, MaxFontSize);
            style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            style.normal.textColor = color;
            float half = style.fontSize * Mathf.Max(1, text.Length);
            GUI.Label(new Rect(screen.x - _area.x - half, Screen.height - screen.y - _area.y - style.fontSize, half * 2f, style.fontSize * 2f), text, style);
        }
    }
}
