using System.Text;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using UnityEngine;

namespace BoardSandbox.Client.Views
{
    // How one object of a game looks and where it is. Every value comes from the properties of the object, then
    // from what the mod says about its kind, then from a default. Positions and rotations are those of the scene.
    public sealed class ObjectLook
    {
        public const float MinScale = 0.25f;
        public const float MaxScale = 3f;
        public const float MinSize = 0.01f;
        public const float MaxSize = 100f;
        public const float MaxTween = 5f;

        private static readonly Color32 Neutral = new Color32(0x8a, 0x8a, 0x8a, 255);
        private static readonly string[] SideNames = { "px", "nx", "py", "ny", "nz", "pz" };

        private ObjectLook()
        {
        }

        public PieceShape Shape { get; private set; }
        public Color32 Color { get; private set; }

        // Multiplies the natural size of the shape along its own axes.
        public Vector3 Size { get; private set; }

        // True when the object has a position of its own instead of standing on the board.
        public bool Free { get; private set; }
        public Vector3 Position { get; private set; }
        public Rot3 Rotation { get; private set; }

        // The position is the middle of the object instead of its foot.
        public bool Centered { get; private set; }

        // Colors of the stickers on +x, -x, +y, -y, +z, -z of the scene; null without stickers.
        public Color32?[] Stickers { get; private set; }
        public float Inset { get; private set; }
        public float Gloss { get; private set; }
        public bool Shadow { get; private set; }
        public bool Visible { get; private set; }
        public bool Pick { get; private set; }

        // Seconds a free object takes to glide to a new position.
        public float Tween { get; private set; }

        // Names the mesh; objects with equal keys share one.
        public string MeshKey { get; private set; }

        public bool Transparent => Color.a < 255;
        public Vector3 HalfExtents => Vector3.Scale(BoardShapes.NaturalSize(Shape), Size) * 0.5f;

        // The middle of a free object.
        public Vector3 Center => Centered ? Position : Position + new Vector3(0f, HalfExtents.y, 0f);

        public bool SameAppearance(ObjectLook other)
        {
            return other != null && MeshKey == other.MeshKey && Gloss == other.Gloss && Shadow == other.Shadow && Visible == other.Visible
                && Color.r == other.Color.r && Color.g == other.Color.g && Color.b == other.Color.b && Color.a == other.Color.a;
        }

        public static ObjectLook For(ViewObject piece, ViewHints hints, ModManifest manifest)
        {
            JsonValue props = piece.Props;
            JsonValue defaults = hints.PieceDefaults(piece.Kind);
            ObjectLook look = new ObjectLook();

            if (!BoardShapes.TryParseShape(Pick2(props, defaults, "shape").AsString(), out PieceShape shape))
                shape = piece.Kind == "stone" ? PieceShape.Stone : PieceShape.Disc;
            look.Shape = shape;

            Color32 color = StageSettings.ReadColor(Pick2(props, defaults, "color"), SeatColor(manifest, piece.Owner));
            float alpha = StageSettings.ReadFloat(Pick2(props, defaults, "alpha"), 1f, 0f, 1f);
            look.Color = new Color32(color.r, color.g, color.b, (byte)Mathf.RoundToInt(color.a * alpha));

            float scale = StageSettings.ReadFloat(Pick2(props, defaults, "scale"), 1f, MinScale, MaxScale);
            look.Size = ReadSize(Pick2(props, defaults, "size")) * scale;

            look.Free = Frame.TryReadVector(props["pos"], out Vector3 position);
            look.Position = Frame.Flip(position);
            look.Rotation = Rot3.TryRead(Pick2(props, defaults, "rot"), out Rot3 rotation) ? Frame.ToScene(rotation) : Rot3.Identity;
            string anchor = Pick2(props, defaults, "anchor").AsString();
            look.Centered = anchor == "center" || (anchor != "bottom" && look.Free);

            look.Inset = StageSettings.ReadFloat(Pick2(props, defaults, "face_inset"), 0.1f, 0f, 0.45f);
            look.Stickers = ReadStickers(Pick2(props, defaults, "faces"), shape);
            look.Gloss = StageSettings.ReadFloat(Pick2(props, defaults, "gloss"), 0.55f, 0f, 1f);
            look.Shadow = Pick2(props, defaults, "shadow").AsBool(!look.Free);
            look.Visible = Pick2(props, defaults, "visible").AsBool(true);
            look.Pick = Pick2(props, defaults, "pick").AsBool(true);
            look.Tween = StageSettings.ReadFloat(Pick2(props, defaults, "tween"), 0f, 0f, MaxTween);
            look.MeshKey = BuildMeshKey(look);
            return look;
        }

        public static Color32 SeatColor(ModManifest manifest, int seat)
        {
            SeatInfo info = manifest?.GetSeat(seat);
            return info == null ? Neutral : ColorParser.Parse(info.Color, Neutral);
        }

        public MeshBuilder BuildMesh()
        {
            return Stickers == null
                ? BoardShapes.Piece(Shape, Centered)
                : BoardShapes.Piece(Shape, Centered, new Color32(Color.r, Color.g, Color.b, 255), Stickers, Inset);
        }

        private static JsonValue Pick2(JsonValue props, JsonValue defaults, string name)
        {
            JsonValue own = props[name];
            return own.IsNull ? defaults[name] : own;
        }

        private static Vector3 ReadSize(JsonValue value)
        {
            if (value.IsNumber)
            {
                float uniform = StageSettings.ReadFloat(value, 1f, MinSize, MaxSize);
                return new Vector3(uniform, uniform, uniform);
            }
            if (Frame.TryReadVector(value, out Vector3 size))
                return new Vector3(Mathf.Clamp(size.x, MinSize, MaxSize), Mathf.Clamp(size.y, MinSize, MaxSize), Mathf.Clamp(size.z, MinSize, MaxSize));
            return Vector3.one;
        }

        private static Color32?[] ReadStickers(JsonValue faces, PieceShape shape)
        {
            if (!faces.IsObject || (shape != PieceShape.Box && shape != PieceShape.Cube))
                return null;
            Color32?[] stickers = new Color32?[6];
            bool any = false;
            for (int side = 0; side < 6; side++)
            {
                JsonValue value = faces[SideNames[side]];
                if (value.IsString && ColorParser.TryParse(value.AsString(), out Color32 color))
                {
                    stickers[side] = new Color32(color.r, color.g, color.b, 255);
                    any = true;
                }
            }
            return any ? stickers : null;
        }

        private static string BuildMeshKey(ObjectLook look)
        {
            StringBuilder key = new StringBuilder();
            key.Append(look.Shape).Append(look.Centered ? ":c" : ":f");
            if (look.Stickers == null)
                return key.ToString();
            key.Append(':').Append(look.Inset.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            key.Append(':').Append(Hex(look.Color));
            foreach (Color32? sticker in look.Stickers)
                key.Append(':').Append(sticker == null ? "-" : Hex(sticker.Value));
            return key.ToString();
        }

        private static string Hex(Color32 color)
        {
            return color.r.ToString("x2") + color.g.ToString("x2") + color.b.ToString("x2");
        }
    }
}
