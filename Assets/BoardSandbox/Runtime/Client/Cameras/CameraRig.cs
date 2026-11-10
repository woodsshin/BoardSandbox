using BoardSandbox.Client.Geometry;
using UnityEngine;

namespace BoardSandbox.Client.Cameras
{
    // An orbiting camera that looks at a point of the table and eases towards what the player asks for.
    public sealed class CameraRig
    {
        public const float DefaultYaw = 0f;
        public const float DefaultPitch = 55f;
        public const float FieldOfView = 40f;

        private const float DegreesPerPixel = 0.25f;
        private const float Stiffness = 12f;
        private const float AspectTolerance = 0.01f;

        private readonly Camera _camera;
        private BoardLayout _layout;
        private float _framedAspect;
        private bool _userMoved;

        private Vector3 _target;
        private float _yaw;
        private float _pitch = DefaultPitch;
        private float _distance = 20f;

        private Vector3 _goalTarget;
        private float _goalYaw;
        private float _goalPitch = DefaultPitch;
        private float _goalDistance = 20f;

        private float _minDistance = 2f;
        private float _maxDistance = 200f;
        private Vector2 _reach = new Vector2(10f, 10f);

        public CameraRig(Camera camera)
        {
            _camera = camera;
            _camera.orthographic = false;
            _camera.fieldOfView = FieldOfView;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.075f, 0.085f, 0.1f, 1f);
            Apply();
        }

        public Camera Camera => _camera;

        // Looks at the whole board from the default angle; the camera glides there unless it is told to jump.
        public void Frame(BoardLayout layout, bool jump)
        {
            _layout = layout;
            _userMoved = false;
            _framedAspect = _camera.aspect;
            _goalYaw = DefaultYaw;
            _goalPitch = DefaultPitch;
            _goalTarget = Vector3.zero;
            _goalDistance = layout.FitDistance(FieldOfView, _camera.aspect, _goalYaw, _goalPitch);
            _minDistance = Mathf.Max(1.5f, layout.CellSize * 2f);
            _maxDistance = _goalDistance * 3f;
            _reach = new Vector2(layout.SlabHalfWidth, layout.SlabHalfDepth);
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
            if (_layout != null)
                Frame(_layout, false);
        }

        public void Orbit(Vector2 mouseDelta)
        {
            _userMoved = true;
            _goalYaw = CameraMath.WrapDegrees(_goalYaw + mouseDelta.x * DegreesPerPixel);
            _goalPitch = CameraMath.ClampPitch(_goalPitch + mouseDelta.y * DegreesPerPixel);
        }

        public void Pan(Vector2 mouseDelta, float screenHeight)
        {
            _userMoved = true;
            _goalTarget += CameraMath.PanOffset(_goalYaw, _goalPitch, mouseDelta, _goalDistance, FieldOfView, screenHeight);
            _goalTarget = new Vector3(Mathf.Clamp(_goalTarget.x, -_reach.x, _reach.x), 0f, Mathf.Clamp(_goalTarget.z, -_reach.y, _reach.y));
        }

        public void Zoom(float wheelNotches)
        {
            _userMoved = true;
            _goalDistance = CameraMath.Zoom(_goalDistance, wheelNotches, _minDistance, _maxDistance);
        }

        public void Tick(float deltaTime)
        {
            if (_layout != null && !_userMoved && Mathf.Abs(_camera.aspect - _framedAspect) > AspectTolerance)
                Frame(_layout, false);

            _yaw = CameraMath.WrapDegrees(CameraMath.DampAngle(_yaw, _goalYaw, Stiffness, deltaTime));
            _pitch = CameraMath.Damp(_pitch, _goalPitch, Stiffness, deltaTime);
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
            t.rotation = Quaternion.LookRotation(_target - position, Vector3.up);
            _camera.nearClipPlane = Mathf.Max(0.05f, _distance * 0.02f);
            _camera.farClipPlane = _distance * 8f + 800f;
        }
    }
}
