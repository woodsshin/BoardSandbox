using BoardSandbox.Client.Geometry;
using BoardSandbox.Client.Views;
using UnityEngine;

namespace BoardSandbox.Client.Cameras
{
    // An orbiting camera that looks at a point of the scene and eases towards what the player asks for.
    public sealed class CameraRig
    {
        private const float DegreesPerPixel = 0.25f;
        private const float Stiffness = 12f;
        private const float AspectTolerance = 0.01f;

        private readonly Camera _camera;
        private CameraSettings _settings;
        private BoardLayout _layout;
        private CameraPlan _plan;
        private float _framedAspect;
        private bool _userMoved;

        private Vector3 _target;
        private float _yaw;
        private float _pitch = 55f;
        private float _distance = 20f;

        private Vector3 _goalTarget;
        private float _goalYaw;
        private float _goalPitch = 55f;
        private float _goalDistance = 20f;

        public CameraRig(Camera camera)
        {
            _camera = camera;
            _camera.orthographic = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            Background = StageSettings.DefaultBackground;
            Frame(new CameraSettings(), null, true);
        }

        public Camera Camera => _camera;

        public Color32 Background
        {
            set => _camera.backgroundColor = value;
        }

        // Looks at the scene the way the stage asks for; the layout is null when no board is shown. The camera
        // glides there unless it is told to jump.
        public void Frame(CameraSettings settings, BoardLayout layout, bool jump)
        {
            _settings = settings;
            _layout = layout;
            _userMoved = false;
            _framedAspect = _camera.aspect;
            _plan = CameraPlan.Resolve(settings, layout, _camera.aspect);
            _camera.fieldOfView = _plan.Fov;
            _goalYaw = CameraMath.WrapDegrees(_plan.Yaw);
            _goalPitch = _plan.FreePitch ? CameraMath.WrapDegrees(_plan.Pitch) : _plan.Pitch;
            _goalTarget = _plan.Target;
            _goalDistance = _plan.Distance;
            if (jump)
            {
                _target = _goalTarget;
                _yaw = _goalYaw;
                _pitch = _goalPitch;
                _distance = _goalDistance;
                Apply();
            }
        }

        public void Reframe()
        {
            Frame(_settings, _layout, false);
        }

        public void Drag(DragMode mode, Vector2 mouseDelta, float screenHeight)
        {
            if (mode == DragMode.Orbit)
                Orbit(mouseDelta);
            else if (mode == DragMode.Pan)
                Pan(mouseDelta, screenHeight);
        }

        public void Orbit(Vector2 mouseDelta)
        {
            _userMoved = true;
            CameraMath.Orbit(ref _goalYaw, ref _goalPitch, mouseDelta * DegreesPerPixel, _plan.FreePitch, _plan.MinPitch, _plan.MaxPitch);
        }

        public void Pan(Vector2 mouseDelta, float screenHeight)
        {
            _userMoved = true;
            Vector3 moved = _goalTarget + CameraMath.PanOffset(_goalYaw, _goalPitch, mouseDelta, _goalDistance, _plan.Fov, screenHeight);
            Vector3 home = _plan.Target;
            _goalTarget = new Vector3(
                Mathf.Clamp(moved.x, home.x - _plan.Reach.x, home.x + _plan.Reach.x),
                Mathf.Clamp(moved.y, home.y - _plan.Reach.y, home.y + _plan.Reach.y),
                Mathf.Clamp(moved.z, home.z - _plan.Reach.z, home.z + _plan.Reach.z));
        }

        public void Zoom(float wheelNotches)
        {
            if (!_settings.Zoom)
                return;
            _userMoved = true;
            _goalDistance = CameraMath.Zoom(_goalDistance, wheelNotches, _plan.MinDistance, _plan.MaxDistance);
        }

        public void Tick(float deltaTime)
        {
            if (!_userMoved && Mathf.Abs(_camera.aspect - _framedAspect) > AspectTolerance)
                Frame(_settings, _layout, false);

            _yaw = CameraMath.WrapDegrees(CameraMath.DampAngle(_yaw, _goalYaw, Stiffness, deltaTime));
            _pitch = _plan.FreePitch
                ? CameraMath.WrapDegrees(CameraMath.DampAngle(_pitch, _goalPitch, Stiffness, deltaTime))
                : CameraMath.Damp(_pitch, _goalPitch, Stiffness, deltaTime);
            _distance = CameraMath.Damp(_distance, _goalDistance, Stiffness, deltaTime);
            _target = CameraMath.Damp(_target, _goalTarget, Stiffness, deltaTime);
            Apply();
        }

        // The ray through a point given in pixels, with the origin in the lower left corner as the screen has it.
        public Ray RayFromScreen(Vector2 pixels)
        {
            return _camera.ScreenPointToRay(new Vector3(pixels.x, pixels.y, 0f));
        }

        private void Apply()
        {
            Vector3 position = CameraMath.Position(_target, _yaw, _pitch, _distance);
            Transform t = _camera.transform;
            t.position = position;
            CameraMath.Basis(_yaw, _pitch, out _, out Vector3 up, out Vector3 forward);
            t.rotation = Quaternion.LookRotation(forward, up);
            _camera.nearClipPlane = Mathf.Max(0.05f, _distance * 0.02f);
            _camera.farClipPlane = _distance * 8f + 800f;
        }
    }
}
