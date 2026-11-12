using System;
using System.Collections.Generic;
using BoardSandbox.Client.Geometry;
using BoardSandbox.Client.Rendering;
using BoardSandbox.Core.Game;
using BoardSandbox.Core.Json;
using BoardSandbox.Core.Mods;
using UnityEngine;
using UnityEngine.Rendering;

namespace BoardSandbox.Client.Views
{
    // Draws a game: the table and the board as the stage describes them, the objects with their animations,
    // the last move, the winning line and a preview where a click would place a piece. It reads a ViewModel
    // and never writes one.
    public sealed class SceneView : IDisposable
    {
        public const int MaxPieces = 6000;

        private const float SpawnHeight = 1.8f;
        private const float SpawnSeconds = 0.3f;
        private const float RemoveSeconds = 0.18f;
        private const float MoveArc = 0.5f;
        private const float MarkerRise = 0.008f;
        private const float RingRise = 0.012f;
        private const int MaxWinningRings = 64;

        private static readonly Color32 White = new Color32(255, 255, 255, 255);

        private sealed class Visual
        {
            public int Id;
            public double X;
            public double Y;
            public ObjectLook Look;
            public GameObject Body;
            public GameObject Shade;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Vector3 Position;
            public Vector3 MoveFrom;
            public float MoveElapsed;
            public float MoveSeconds;
            public bool Arc;
            public float Age;
            public float Lift;
            public float Scale = 1f;
            public bool Spawning;
            public bool Removing;
            public bool NeedsPlace;

            public bool Moving => MoveSeconds > 0f;
        }

        private readonly MaterialSet _materials;
        private readonly int _layer;
        private readonly GameObject _root;
        private readonly Transform _pieces;
        private readonly Dictionary<int, Visual> _byId = new Dictionary<int, Visual>();
        private readonly List<Visual> _visuals = new List<Visual>();
        private readonly Dictionary<string, Mesh> _meshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        private readonly List<GameObject> _rings = new List<GameObject>();
        private readonly List<Mesh> _ownedMeshes = new List<Mesh>();
        private readonly HashSet<int> _effectIds = new HashSet<int>();

        private GameObject _boardObject;
        private GameObject _tableObject;
        private readonly GameObject _lastMarker;
        private readonly GameObject _ghost;
        private readonly MeshFilter _ghostFilter;
        private readonly MeshRenderer _ghostRenderer;
        private readonly GameObject _cursor;

        private BoardLayout _layout;
        private BoardState _state;
        private StageSettings _stage;
        private ViewEvent _stageEvent;
        private ViewHints _hints = new ViewHints(null);
        private ModManifest _manifest;
        private long _syncedVersion = -1;
        private float _time;
        private bool _relayout;
        private bool _boardDirty;
        private bool _disposed;

        private ViewEvent _effect;
        private bool _effectTurns;
        private Vector3 _effectAxis;
        private Vector3 _effectPivot;
        private float _effectDegrees;
        private Rot3 _effectRotation = Rot3.Identity;

        // Everything the view makes is put on the given layer, so that a camera can be told to show this view only.
        public SceneView(MaterialSet materials, int layer = 0)
        {
            _materials = materials ?? throw new ArgumentNullException(nameof(materials));
            _layer = layer;
            _root = new GameObject("Board Sandbox Scene");
            _pieces = new GameObject("Pieces").transform;
            _pieces.SetParent(_root.transform, false);

            _lastMarker = CreateOverlay("Last Move", SharedMesh("marker", BoardShapes.Marker), White);
            _cursor = CreateOverlay("Cursor", SharedMesh("ring", BoardShapes.Highlight), White);
            _ghost = CreateOverlay("Preview", null, White);
            _ghostFilter = _ghost.GetComponent<MeshFilter>();
            _ghostRenderer = _ghost.GetComponent<MeshRenderer>();
            HideMarkers();
            ApplyStage(new StageSettings());
        }

        public StageSettings Stage => _stage;
        public ModManifest Manifest => _manifest;

        // The board the camera should frame; null while the stage shows none.
        public BoardLayout Layout => _stage.Board.Visible ? _layout : null;

        // Grows whenever the board or the camera of the stage changes, so a camera knows when to frame again.
        public int FrameVersion { get; private set; }

        // Set when a game has more pieces than the view is willing to draw.
        public bool Overflowed { get; private set; }

        public bool IsAnimating
        {
            get
            {
                if (_effect != null)
                    return true;
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
            _stageEvent = null;
            Overflowed = false;
            ClearPieces();
            HideMarkers();
            SetEffect(null, 0f);
            ApplyStage(StageSettings.Resolve(_hints.Client, null));
        }

        // Brings the drawing up to date with the model; cheap when nothing has changed.
        public void Sync(ViewModel model)
        {
            if (_disposed || model == null || model.Version == _syncedVersion)
                return;
            _syncedVersion = model.Version;

            ViewEvent stageEvent = model.GetCustom(ViewEvent.StageType);
            if (!ReferenceEquals(stageEvent, _stageEvent))
            {
                _stageEvent = stageEvent;
                StageSettings next = StageSettings.Resolve(_hints.Client, stageEvent);
                if (!next.SameAs(_stage))
                    ApplyStage(next);
            }

            BoardState state = BoardState.Read(model, _stage);
            if (_layout == null || _boardDirty || _layout.Columns != state.Columns || _layout.Rows != state.Rows)
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
                ObjectLook look = ObjectLook.For(piece, _hints, _manifest);
                if (_byId.TryGetValue(piece.Id, out Visual visual) && !visual.Removing)
                    Update(visual, piece, look);
                else
                    Add(piece, look);
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

        // Shows an effect of the presentation at the given point between 0 and 1; null ends it.
        public void SetEffect(ViewEvent effect, float progress)
        {
            if (_disposed)
                return;
            if (!ReferenceEquals(effect, _effect))
            {
                _effect = effect;
                _relayout = true;
                _effectIds.Clear();
                _effectTurns = false;
                if (effect != null && effect.Raw["kind"].AsString() == "rotate" && Frame.TryReadVector(effect.Raw["axis"], out _effectAxis))
                {
                    _effectTurns = true;
                    Frame.TryReadVector(effect.Raw["pivot"], out Vector3 pivot);
                    _effectPivot = Frame.Flip(pivot);
                    _effectDegrees = StageSettings.ReadFloat(effect.Raw["degrees"], 0f, -3600f, 3600f);
                    foreach (JsonValue id in effect.Raw["ids"].Items)
                        _effectIds.Add(id.AsInt());
                }
            }
            if (_effectTurns)
                _effectRotation = Frame.TurnToScene(_effectAxis, _effectDegrees * EaseInOut(Mathf.Clamp01(progress)));
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
                if (busy || relayout || visual.NeedsPlace || (_effectTurns && _effectIds.Contains(visual.Id)))
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
            _cursor.SetActive(_stage.Cursor.Visible);

            if (_hints.PreviewKind == null || !_stage.Preview.Visible || (_state != null && _state.IsOccupied(x, y)))
            {
                _ghost.SetActive(false);
                return;
            }
            PieceShape shape = _hints.ShapeFor(_hints.PreviewKind);
            _ghostFilter.sharedMesh = SharedMesh(shape + ":f", () => BoardShapes.Piece(shape));
            _ghostRenderer.sharedMaterial = _materials.Overlay(new Color32(color.r, color.g, color.b, _stage.Preview.Color.a));
            _ghost.transform.position = at;
            _ghost.SetActive(true);
        }

        public bool TryPickCell(Ray ray, out int x, out int y)
        {
            x = 0;
            y = 0;
            return _layout != null && _layout.TryPick(ray, out x, out y);
        }

        // The nearest object under the ray that lets itself be picked.
        public bool TryPickObject(Ray ray, out PickHit hit)
        {
            hit = default(PickHit);
            bool found = false;
            foreach (Visual visual in _visuals)
            {
                ObjectLook look = visual.Look;
                if (visual.Removing || !look.Visible || !look.Pick)
                    continue;
                Vector3 half = look.HalfExtents;
                Vector3 center = visual.Position + (look.Centered ? Vector3.zero : look.Rotation.Rotate(new Vector3(0f, half.y, 0f)));
                bool met = BoardShapes.IsRound(look.Shape)
                    ? ScenePicker.RayEllipsoid(ray.origin, ray.direction, center, look.Rotation, half, out float distance, out Vector3 normal)
                    : ScenePicker.RayBox(ray.origin, ray.direction, center, look.Rotation, half, out distance, out normal);
                if (!met || (found && distance >= hit.Distance))
                    continue;
                found = true;
                hit = new PickHit { Id = visual.Id, Distance = distance, Point = ray.origin + ray.direction * distance, Normal = normal };
            }
            return found;
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
            _meshes.Clear();
            _visuals.Clear();
            _byId.Clear();
            _rings.Clear();
        }

        private void ApplyStage(StageSettings next)
        {
            StageSettings old = _stage;
            _stage = next;
            _boardDirty = true;
            _relayout = true;
            _syncedVersion = -1;
            if (old == null || !old.Source["camera"].StructurallyEquals(next.Source["camera"]) || old.Board.Visible != next.Board.Visible)
                FrameVersion++;

            MaterialSet.SetLight(Frame.Flip(next.Light.Direction), (Color)next.Light.Color, next.Light.Ambient, next.Light.Intensity);
            _lastMarker.GetComponent<MeshRenderer>().sharedMaterial = _materials.Overlay(next.LastMove.Color);
            _cursor.GetComponent<MeshRenderer>().sharedMaterial = _materials.Overlay(next.Cursor.Color);
            _cursor.transform.localScale = new Vector3(next.Cursor.Size, 1f, next.Cursor.Size);
            foreach (GameObject ring in _rings)
                StyleRing(ring);
            foreach (Visual visual in _visuals)
                visual.Shade.GetComponent<MeshRenderer>().sharedMaterial = _materials.Overlay(next.Shadow.Color);

            DestroyWithMesh(ref _tableObject);
            if (next.Table.Visible)
                _tableObject = CreateSolid("Table", BoardShapes.Table(next.Table.Size * 0.5f, next.Table.Color), "Table", _materials.Vertex);
            PlaceTable();
        }

        private void PlaceTable()
        {
            if (_tableObject == null)
                return;
            float y = !float.IsNaN(_stage.Table.Y) ? _stage.Table.Y : _stage.Board.Visible && _layout != null ? -_layout.Thickness - 0.01f : -0.01f;
            _tableObject.transform.position = new Vector3(0f, y, 0f);
        }

        private void Add(ViewObject piece, ObjectLook look)
        {
            if (_byId.TryGetValue(piece.Id, out Visual old))
            {
                Remove(old);
                _visuals.Remove(old);
            }

            GameObject body = new GameObject("Piece") { layer = _layer };
            body.transform.SetParent(_pieces, false);
            MeshFilter filter = body.AddComponent<MeshFilter>();
            MeshRenderer renderer = body.AddComponent<MeshRenderer>();
            Unlit(renderer);

            GameObject shade = new GameObject("Shade") { layer = _layer };
            shade.transform.SetParent(_pieces, false);
            shade.AddComponent<MeshFilter>().sharedMesh = SharedMesh("shadow", BoardShapes.Shadow);
            MeshRenderer shadeRenderer = shade.AddComponent<MeshRenderer>();
            shadeRenderer.sharedMaterial = _materials.Overlay(_stage.Shadow.Color);
            Unlit(shadeRenderer);

            Visual visual = new Visual
            {
                Id = piece.Id,
                X = piece.X,
                Y = piece.Y,
                Look = look,
                Body = body,
                Shade = shade,
                Filter = filter,
                Renderer = renderer,
                Spawning = !look.Free,
                Lift = look.Free ? 0f : SpawnHeight,
                Scale = look.Free ? 1f : 0.7f
            };
            visual.Position = Target(visual);
            Restyle(visual);
            _byId[piece.Id] = visual;
            _visuals.Add(visual);
            Place(visual);
        }

        private void Update(Visual visual, ViewObject piece, ObjectLook look)
        {
            Vector3 before = Target(visual);
            bool restyle = !look.SameAppearance(visual.Look);
            visual.Look = look;
            visual.X = piece.X;
            visual.Y = piece.Y;
            visual.NeedsPlace = true;
            if (restyle)
                Restyle(visual);

            Vector3 target = Target(visual);
            if (target == before)
                return;
            visual.Spawning = false;
            visual.Lift = 0f;
            visual.Scale = 1f;
            visual.MoveFrom = visual.Position;
            visual.MoveElapsed = 0f;
            visual.Arc = !look.Free;
            visual.MoveSeconds = look.Free ? look.Tween : Mathf.Min(0.5f, 0.15f + 0.04f * (target - visual.MoveFrom).magnitude);
            if (!visual.Moving)
                visual.Position = target;
        }

        private void Restyle(Visual visual)
        {
            ObjectLook look = visual.Look;
            visual.NeedsPlace = true;
            visual.Filter.sharedMesh = SharedMesh(look.MeshKey, look.BuildMesh);
            Color32 tint = look.Stickers == null ? look.Color : new Color32(255, 255, 255, look.Color.a);
            visual.Renderer.sharedMaterial = look.Transparent ? _materials.Overlay(tint) : _materials.Piece(tint, look.Gloss);
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
                visual.Lift = visual.Arc ? Mathf.Sin(Mathf.PI * t) * MoveArc : 0f;
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
            ObjectLook look = visual.Look;
            float grow = Mathf.Max(0f, visual.Scale);
            Vector3 position = visual.Position + new Vector3(0f, visual.Lift, 0f);
            Rot3 rotation = look.Rotation;
            if (_effectTurns && _effectIds.Contains(visual.Id))
            {
                position = _effectPivot + _effectRotation.Rotate(position - _effectPivot);
                rotation = _effectRotation * rotation;
            }
            Transform body = visual.Body.transform;
            body.position = position;
            body.rotation = rotation.ToQuaternion();
            body.localScale = look.Size * grow;
            if (visual.Body.activeSelf != look.Visible)
                visual.Body.SetActive(look.Visible);

            bool shaded = look.Visible && look.Shadow && _stage.Shadow.Visible;
            if (visual.Shade.activeSelf != shaded)
                visual.Shade.SetActive(shaded);
            if (!shaded)
                return;
            float spread = Mathf.Lerp(0.6f, 1f, 1f / (1f + visual.Lift * 2f)) * grow * _stage.Shadow.Size;
            visual.Shade.transform.position = new Vector3(visual.Position.x + 0.05f * look.Size.x, 0.006f, visual.Position.z + 0.06f * look.Size.z);
            visual.Shade.transform.localScale = new Vector3(look.Size.x * spread, 1f, look.Size.z * spread);
        }

        private Vector3 Target(Visual visual)
        {
            if (visual.Look.Free || _layout == null)
                return visual.Look.Position;
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
            _boardDirty = false;
            BoardLayout old = _layout;
            _layout = _stage.Layout(state.Columns, state.Rows);
            if (old == null || old.Columns != _layout.Columns || old.Rows != _layout.Rows || old.CellSize != _layout.CellSize || old.Margin != _layout.Margin)
                FrameVersion++;
            DestroyWithMesh(ref _boardObject);
            if (_stage.Board.Visible)
                _boardObject = CreateSolid("Board", BoardShapes.Board(_layout, _stage.Board.Palette), "Board", _materials.Vertex);
            PlaceTable();
            _relayout = true;
        }

        private void DestroyWithMesh(ref GameObject item)
        {
            if (item == null)
                return;
            MeshFilter filter = item.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
            {
                _ownedMeshes.Remove(filter.sharedMesh);
                ObjectUtil.Destroy(filter.sharedMesh);
            }
            ObjectUtil.Destroy(item);
            item = null;
        }

        private void UpdateRings()
        {
            int wanted = 0;
            if (_state != null && _layout != null && _stage.Win.Visible)
            {
                wanted = Mathf.Min(_state.WinningCells.Count, MaxWinningRings);
                while (_rings.Count < wanted)
                {
                    GameObject ring = CreateOverlay("Winning Cell", SharedMesh("ring", BoardShapes.Highlight), White);
                    StyleRing(ring);
                    _rings.Add(ring);
                }
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

        private void StyleRing(GameObject ring)
        {
            ring.GetComponent<MeshRenderer>().sharedMaterial = _materials.Overlay(_stage.Win.Color);
            ring.transform.localScale = new Vector3(_stage.Win.Size, 1f, _stage.Win.Size);
        }

        private void UpdateLastMarker()
        {
            bool show = false;
            if (_state != null && _state.HasLastMove && _stage.LastMove.Visible)
            {
                ViewObject occupant = _state.ObjectAt(_state.LastX, _state.LastY);
                if (occupant != null && _byId.TryGetValue(occupant.Id, out Visual visual) && !visual.Removing)
                {
                    float top = BoardShapes.PieceHeight(visual.Look.Shape) * visual.Look.Size.y * visual.Scale;
                    _lastMarker.transform.position = visual.Position + new Vector3(0f, visual.Lift + top + MarkerRise, 0f);
                    float pulse = visual.Look.Size.x * _stage.LastMove.Size * (1f + 0.12f * Mathf.Sin(_time * 4f));
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
            GameObject item = new GameObject(name) { layer = _layer };
            item.transform.SetParent(_root.transform, false);
            item.AddComponent<MeshFilter>().sharedMesh = Own(MeshFactory.Create(shape, meshName));
            MeshRenderer renderer = item.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            Unlit(renderer);
            return item;
        }

        private GameObject CreateOverlay(string name, Mesh mesh, Color32 color)
        {
            GameObject item = new GameObject(name) { layer = _layer };
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

        private Mesh SharedMesh(string key, Func<MeshBuilder> build)
        {
            if (!_meshes.TryGetValue(key, out Mesh mesh))
            {
                mesh = Own(MeshFactory.Create(build(), key));
                _meshes[key] = mesh;
            }
            return mesh;
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
