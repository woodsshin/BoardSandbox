using BoardSandbox.Core.Game;
using UnityEngine;

namespace BoardSandbox.Client.Views
{
    // Writes the "label" property of an object on top of it, such as the digit of a sudoku. The objects are meshes
    // and have no text, so the labels are drawn by the GUI at the place on the screen where the top of each object
    // is. Call Draw from OnGUI before the interface is drawn, so that the panels cover the labels and not the other
    // way round.
    public sealed class BoardLabels
    {
        // The height of the font as a share of one cell of the board.
        private const float TextShare = 0.55f;
        private const int MinFontSize = 7;
        private const int MaxFontSize = 160;

        private static readonly Color DarkText = new Color(0.08f, 0.09f, 0.11f, 1f);
        private static readonly Color LightText = new Color(0.97f, 0.97f, 0.95f, 1f);

        // area is the part of the screen the scene is shown in, in the coordinates of the GUI; labels do not leave it.
        public void Draw(ViewModel view, SceneView scene, Camera camera, Rect area, GUIStyle style)
        {
            if (Event.current.type != EventType.Repaint || view == null || scene == null || camera == null || style == null)
                return;

            float tanHalfFov = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float viewHeight = camera.pixelHeight;
            bool grouped = false;
            foreach (ViewObject piece in view.Objects)
            {
                string label = piece.Props["label"].AsString();
                if (label.Length == 0 || !scene.TryGetTop(piece.Id, out Vector3 top, out float size, out ObjectLook look))
                    continue;

                Vector3 screen = camera.WorldToScreenPoint(top);
                if (screen.z <= 0f)
                    continue;

                // How many pixels one unit of the board covers at the depth of the object.
                float pixelsPerUnit = viewHeight * 0.5f / (screen.z * tanHalfFov);
                int fontSize = Mathf.RoundToInt(pixelsPerUnit * TextShare * size);
                if (fontSize < MinFontSize)
                    continue;

                if (!grouped)
                {
                    GUI.BeginGroup(area);
                    grouped = true;
                }
                style.fontSize = Mathf.Min(fontSize, MaxFontSize);
                style.normal.textColor = look.IsLight ? DarkText : LightText;
                float half = style.fontSize;
                GUI.Label(new Rect(screen.x - area.x - half, Screen.height - screen.y - area.y - half, half * 2f, half * 2f), label, style);
            }
            if (grouped)
                GUI.EndGroup();
        }
    }
}
