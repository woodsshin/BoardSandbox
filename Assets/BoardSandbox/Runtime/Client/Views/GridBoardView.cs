using System;
using System.Collections.Generic;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Client.Rendering;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Mods;
using UnityEngine;
using UnityEngine.Rendering;

namespace BoardSandbox.Client.Views
{
    // Draws a game that lives on a grid: the board, the pieces with their animations, the last move,
    // the winning line and a preview where a click would place a piece. It reads a ViewModel and never writes one.
    public sealed class GridBoardView : IDisposable
    {
        public const int MaxPieces = 6000;

        private const float SpawnHeight = 1.8f;
        private const float SpawnSeconds = 0.3f;
        private const float RemoveSeconds = 0.18f;
        private const float MoveArc = 0.5f;
        private const float TableHalfExtent = 400f;
        private const float MarkerRise = 0.008f;
        private const float RingRise = 0.012f;
        private const int MaxWinningRings = 64;

        private static readonly Color32 MarkerColor = new Color32(0xe5, 0x3d, 0x2f, 255);
        private static readonly Color32 WinColor = new Color32(0xf2, 0xc2, 0x3a, 235);
        private static readonly Color32 ShadowColor = new Color32(0, 0, 0, 80);
        private static readonly Color32 CursorColor = new Color32(255, 255, 255, 190);

        private sealed class Visual
        {
            public int Id;
            public double X;
            public double Y;
            public PieceStyle Style;
            public GameObject Body;
            public GameObject Shade;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Vector3 Position;
            public Vector3 MoveFrom;
            public float MoveElapsed;
            public float MoveSeconds;
            public float Age;
            public float Lift;
            public float Scale = 1f;
            public bool Spawning;
            public bool Removing;
            public bool NeedsPlace;

            public bool Moving => MoveSeconds > 0f;
        }

        private readonly MaterialSet _materials;
        private readonly GameObject _root;
        private readonly Transform _pieces;
        private readonly Dictionary<int, Visual> _byId = new Dictionary<int, Visual>();
        private readonly List<Visual> _visuals = new List<Visual>();
        private readonly Dictionary<PieceShape, Mesh> _pieceMeshes = new Dictionary<PieceShape, Mesh>();
        private readonly List<GameObject> _rings = new List<GameObject>();
        private readonly List<Mesh> _ownedMeshes = new List<Mesh>();

        private Mesh _shadowMesh;
        private Mesh _markerMesh;
        private Mesh _ringMesh;
        private GameObject _boardObject;
        private GameObject _tableObject;
        private GameObject _lastMarker;
        private GameObject _ghost;
        private MeshFilter _ghostFilter;
        private MeshRenderer _ghostRenderer;
        private GameObject _cursor;

        private BoardLayout _layout;
        private BoardState _state;
        private ViewHints _hints = new ViewHints(null);
        private ModManifest _manifest;
        private long _syncedVersion = -1;
        private float _time;
        private bool _relayout;
        private bool _disposed;

        public GridBoardView(MaterialSet materials)
        {
            _materials = materials ?? throw new ArgumentNullException(nameof(materials));
            _root = new GameObject("Board Sandbox Board");
            _pieces = new GameObject("Pieces").transform;
            _pieces.SetParent(_root.transform, false);

            _tableObject = CreateSolid("Table", BoardShapes.Table(TableHalfExtent), "Table", _materials.Vertex);
            _lastMarker = CreateOverlay("Last Move", MarkerMesh(), MarkerColor);
            _cursor = CreateOverlay("Cursor", RingMesh(), CursorColor);
            _ghost = CreateOverlay("Preview", null, new Color32(255, 255, 255, 110));
            _ghostFilter = _ghost.GetComponent<MeshFilter>();
            _ghostRenderer = _ghost.GetComponent<MeshRenderer>();
            HideMarkers();
        }

        public BoardLayout Layout => _layout;

        // Grows whenever the board is rebuilt, so a camera knows when to frame it again.
        public int LayoutVersion { get; private set; }

        // Set when a game has more pieces than the view is willing to draw.
        public bool Overflowed { get; private set; }

        public bool IsAnimating
        {
            get
            {
                foreach (Visual v in _visuals)
                {
                    if (v.Spawning || v.Moving || v.Removing)
                        return true;
                }
                return false;
            }
        }

        // Starts over for another game; every piece disappears at once.
        public void Bind(ViewHints hints, ModManifest manifest)
        {
            _hints = hints ?? new ViewHints(null);
            _manifest = manifest;
            _syncedVersion = -1;
            _state = null;
            Overflowed = false;
            ClearPieces();
            HideMarkers();
        }

        // Brings the drawing up to date with the model; cheap when nothing has changed.
        public void Sync(ViewModel model)
        {
            if (_disposed || model == null || model.Version == _syncedVersion)
                return;
            _syncedVersion = model.Version;

            BoardState state = BoardState.Read(model, _hints);
            if (_layout == null || _layout.Columns != state.Columns || _layout.Rows != state.Rows || _layout.Style != _hints.Style)
                RebuildBoard(state);
            _state = state;

            HashSet<int> present = new HashSet<int>();
            int drawn = 0;
            foreach (ViewObject piece in model.Objects)
            {
                if (drawn >= MaxPieces)
                {
                    Overflowed = true;
                    break;
                }
                drawn++;
                present.Add(piece.Id);
                PieceStyle style = PieceStyle.For(piece, _hints, _manifest);
                if (_byId.TryGetValue(piece.Id, out Visual visual) && !visual.Removing)
                    Update(visual, piece, style);
                else
                    Add(piece, style);
            }
            for (int i = 0; i < _visuals.Count; i++)
            {
                Visual visual = _visuals[i];
                if (!present.Contains(visual.Id) && !visual.Removing)
                {
                    visual.Removing = true;
                    visual.MoveSeconds = 0f;
                }
            }
            UpdateRings();
        }

        public void Tick(float deltaTime)
        {
            if (_disposed)
                return;
            _time += deltaTime;
            bool relayout = _relayout;
            _relayout = false;
            for (int i = _visuals.Count - 1; i >= 0; i--)
            {
                Visual visual = _visuals[i];
                bool busy = visual.Spawning || visual.Moving || visual.Removing;
                if (!Advance(visual, deltaTime))
                {
                    Remove(visual);
                    _visuals.RemoveAt(i);
                    continue;
                }
                if (busy || relayout || visual.NeedsPlace)
                {
                    Place(visual);
                    visual.NeedsPlace = false;
                }
            }
            UpdateLastMarker();
        }

        // Shows where a click would place a piece; pass visible = false to hide it.
        public void SetPreview(bool visible, int x, int y, Color32 color)
        {
            if (_disposed || !visible || _layout == null || !_layout.Contains(x, y))
            {
                _cursor.SetActive(false);
                _ghost.SetActive(false);
                return;
            }

            Vector3 at = _layout.CellToWorld(x, y, BoardShapes.RestHeight);
            _cursor.transform.position = at + new Vector3(0f, RingRise, 0f);
            _cursor.SetActive(true);

            if (_hints.PreviewKind == null || (_state != null && _state.IsOccupied(x, y)))
            {
                _ghost.SetActive(false);
                return;
            }
            _ghostFilter.sharedMesh = PieceMesh(_hints.ShapeFor(_hints.PreviewKind));
            _ghostRenderer.sharedMaterial = _materials.Overlay(new Color32(color.r, color.g, color.b, 120));
            _ghost.transform.position = at;
            _ghost.SetActive(true);
        }

        public bool TryPick(Ray ray, out int x, out int y)
        {
            x = 0;
            y = 0;
            return _layout != null && _layout.TryPick(ray, out x, out y);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ObjectUtil.Destroy(_root);
            foreach (Mesh mesh in _ownedMeshes)
                ObjectUtil.Destroy(mesh);
            _ownedMeshes.Clear();
            _pieceMeshes.Clear();
            _visuals.Clear();
            _byId.Clear();
            _rings.Clear();
        }

        private void Add(ViewObject piece, PieceStyle style)
        {
            if (_byId.TryGetValue(piece.Id, out Visual old))
            {
                Remove(old);
                _visuals.Remove(old);
            }

            GameObject body = new GameObject("Piece");
            body.transform.SetParent(_pieces, false);
            MeshFilter filter = body.AddComponent<MeshFilter>();
            MeshRenderer renderer = body.AddComponent<MeshRenderer>();
            Unlit(renderer);

            GameObject shade = new GameObject("Shade");
            shade.transform.SetParent(_pieces, false);
            shade.AddComponent<MeshFilter>().sharedMesh = ShadowMesh();
            MeshRenderer shadeRenderer = shade.AddComponent<MeshRenderer>();
            shadeRenderer.sharedMaterial = _materials.Overlay(ShadowColor);
            Unlit(shadeRenderer);

            Visual visual = new Visual
            {
                Id = piece.Id,
                X = piece.X,
                Y = piece.Y,
                Body = body,
                Shade = shade,
                Filter = filter,
                Renderer = renderer,
                Spawning = true,
                Lift = SpawnHeight,
                Scale = 0.7f
            };
            visual.Position = Target(visual);
            Restyle(visual, style);
            _byId[piece.Id] = visual;
            _visuals.Add(visual);
            Place(visual);
        }

        private void Update(Visual visual, ViewObject piece, PieceStyle style)
        {
            if (!style.Equals(visual.Style))
                Restyle(visual, style);
            if (visual.X == piece.X && visual.Y == piece.Y)
                return;

            visual.MoveFrom = visual.Position;
            visual.X = piece.X;
            visual.Y = piece.Y;
            float distance = (Target(visual) - visual.MoveFrom).magnitude;
            visual.MoveElapsed = 0f;
            visual.MoveSeconds = Mathf.Min(0.5f, 0.15f + 0.04f * distance);
            visual.Spawning = false;
            visual.Lift = 0f;
            visual.Scale = 1f;
        }

        private void Restyle(Visual visual, PieceStyle style)
        {
            visual.Style = style;
            visual.NeedsPlace = true;
            visual.Filter.sharedMesh = PieceMesh(style.Shape);
            visual.Renderer.sharedMaterial = _materials.Piece(style.Color);
        }

        // Returns false once the piece has finished shrinking away.
        private bool Advance(Visual visual, float deltaTime)
        {
            if (visual.Removing)
            {
                visual.Scale -= deltaTime / RemoveSeconds;
                return visual.Scale > 0f;
            }

            if (visual.Spawning)
            {
                visual.Age += deltaTime;
                float t = Mathf.Clamp01(visual.Age / SpawnSeconds);
                float eased = EaseOut(t);
                visual.Lift = (1f - eased) * SpawnHeight;
                visual.Scale = Mathf.Lerp(0.7f, 1f, EaseOut(Mathf.Clamp01(t * 2.5f)));
                visual.Position = Target(visual);
                if (t >= 1f)
                {
                    visual.Spawning = false;
                    visual.Lift = 0f;
                    visual.Scale = 1f;
                }
                return true;
            }

            Vector3 target = Target(visual);
            if (visual.Moving)
            {
                visual.MoveElapsed += deltaTime;
                float t = Mathf.Clamp01(visual.MoveElapsed / visual.MoveSeconds);
                visual.Position = Vector3.Lerp(visual.MoveFrom, target, EaseInOut(t));
                visual.Lift = Mathf.Sin(Mathf.PI * t) * MoveArc;
                if (t >= 1f)
                {
                    visual.MoveSeconds = 0f;
                    visual.Lift = 0f;
                }
            }
            else
            {
                visual.Position = target;
            }
            return true;
        }

        private void Place(Visual visual)
        {
            float size = visual.Style.Scale * Mathf.Max(0f, visual.Scale);
            visual.Body.transform.position = visual.Position + new Vector3(0f, visual.Lift, 0f);
            visual.Body.transform.localScale = new Vector3(size, size, size);

            float spread = Mathf.Lerp(0.6f, 1f, 1f / (1f + visual.Lift * 2f));
            float shade = visual.Style.Scale * Mathf.Max(0f, visual.Scale) * spread;
            visual.Shade.transform.position = new Vector3(visual.Position.x + 0.05f * visual.Style.Scale, 0.006f, visual.Position.z + 0.06f * visual.Style.Scale);
            visual.Shade.transform.localScale = new Vector3(shade, 1f, shade);
        }

        private Vector3 Target(Visual visual)
        {
            return _layout.CellToWorld(visual.X, visual.Y, BoardShapes.RestHeight);
        }

        private void Remove(Visual visual)
        {
            if (_byId.TryGetValue(visual.Id, out Visual current) && current == visual)
                _byId.Remove(visual.Id);
            ObjectUtil.Destroy(visual.Body);
            ObjectUtil.Destroy(visual.Shade);
        }

        private void ClearPieces()
        {
            foreach (Visual visual in _visuals)
            {
                ObjectUtil.Destroy(visual.Body);
                ObjectUtil.Destroy(visual.Shade);
            }
            _visuals.Clear();
            _byId.Clear();
        }

        private void RebuildBoard(BoardState state)
        {
            _layout = new BoardLayout(state.Columns, state.Rows, _hints.Style);
            if (_boardObject != null)
            {
                MeshFilter old = _boardObject.GetComponent<MeshFilter>();
                if (old != null && old.sharedMesh != null)
                {
                    _ownedMeshes.Remove(old.sharedMesh);
                    ObjectUtil.Destroy(old.sharedMesh);
                }
                ObjectUtil.Destroy(_boardObject);
            }
            _boardObject = CreateSolid("Board", BoardShapes.Board(_layout), "Board", _materials.Vertex);
            _tableObject.transform.position = new Vector3(0f, -_layout.Thickness - 0.01f, 0f);
            _relayout = true;
            LayoutVersion++;
        }

        private void UpdateRings()
        {
            int wanted = 0;
            if (_state != null && _layout != null)
            {
                wanted = Mathf.Min(_state.WinningCells.Count, MaxWinningRings);
                while (_rings.Count < wanted)
                    _rings.Add(CreateOverlay("Winning Cell", RingMesh(), WinColor));
                for (int i = 0; i < wanted; i++)
                {
                    Point2 cell = _state.WinningCells[i];
                    _rings[i].transform.position = _layout.CellToWorld(cell.X, cell.Y, BoardShapes.RestHeight + RingRise);
                    _rings[i].SetActive(true);
                }
            }
            for (int i = wanted; i < _rings.Count; i++)
                _rings[i].SetActive(false);
        }

        private void UpdateLastMarker()
        {
            bool show = false;
            if (_state != null && _state.HasLastMove)
            {
                ViewObject occupant = _state.ObjectAt(_state.LastX, _state.LastY);
                if (occupant != null && _byId.TryGetValue(occupant.Id, out Visual visual) && !visual.Removing)
                {
                    float top = BoardShapes.PieceHeight(visual.Style.Shape) * visual.Style.Scale * visual.Scale;
                    _lastMarker.transform.position = visual.Position + new Vector3(0f, visual.Lift + top + MarkerRise, 0f);
                    float pulse = visual.Style.Scale * (1f + 0.12f * Mathf.Sin(_time * 4f));
                    _lastMarker.transform.localScale = new Vector3(pulse, 1f, pulse);
                    show = true;
                }
            }
            if (_lastMarker.activeSelf != show)
                _lastMarker.SetActive(show);
        }

        private void HideMarkers()
        {
            _lastMarker.SetActive(false);
            _cursor.SetActive(false);
            _ghost.SetActive(false);
            foreach (GameObject ring in _rings)
                ring.SetActive(false);
        }

        private GameObject CreateSolid(string name, MeshBuilder shape, string meshName, Material material)
        {
            GameObject item = new GameObject(name);
            item.transform.SetParent(_root.transform, false);
            item.AddComponent<MeshFilter>().sharedMesh = Own(MeshFactory.Create(shape, meshName));
            MeshRenderer renderer = item.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            Unlit(renderer);
            return item;
        }

        private GameObject CreateOverlay(string name, Mesh mesh, Color32 color)
        {
            GameObject item = new GameObject(name);
            item.transform.SetParent(_root.transform, false);
            item.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = item.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _materials.Overlay(color);
            Unlit(renderer);
            return item;
        }

        private static void Unlit(MeshRenderer renderer)
        {
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        private Mesh PieceMesh(PieceShape shape)
        {
            if (!_pieceMeshes.TryGetValue(shape, out Mesh mesh))
            {
                mesh = Own(MeshFactory.Create(BoardShapes.Piece(shape), "Piece " + shape));
                _pieceMeshes[shape] = mesh;
            }
            return mesh;
        }

        private Mesh ShadowMesh()
        {
            return _shadowMesh != null ? _shadowMesh : (_shadowMesh = Own(MeshFactory.Create(BoardShapes.Shadow(), "Shadow")));
        }

        private Mesh MarkerMesh()
        {
            return _markerMesh != null ? _markerMesh : (_markerMesh = Own(MeshFactory.Create(BoardShapes.Marker(), "Marker")));
        }

        private Mesh RingMesh()
        {
            return _ringMesh != null ? _ringMesh : (_ringMesh = Own(MeshFactory.Create(BoardShapes.Highlight(), "Ring")));
        }

        private Mesh Own(Mesh mesh)
        {
            _ownedMeshes.Add(mesh);
            return mesh;
        }

        private static float EaseOut(float t)
        {
            float inverse = 1f - t;
            return 1f - inverse * inverse * inverse;
        }

        private static float EaseInOut(float t)
        {
            return t * t * (3f - 2f * t);
        }
    }
}
