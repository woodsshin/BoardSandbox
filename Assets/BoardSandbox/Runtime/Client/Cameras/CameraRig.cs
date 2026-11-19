using BoardSandbox.Client.Geometry;
using BoardSandbox.Client.Views;
using BoardSandbox.Core.Net;
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

        // What a trackball camera is turned to; yaw and pitch only say where it starts.
        private Quaternion _orientation = Quaternion.identity;
        private Quaternion _goalOrientation = Quaternion.identity;

        // How the player whose table this is looks at it, while the camera shows it that way; null while the player here moves the camera.
        private ViewPose _shared;

        public CameraRig(Camera camera)
        {
            _camera = camera;
            _camera.orthographic = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            Background = StageSettings.DefaultBackground;
            Frame(new CameraSettings(), null, true);
        }

        public Camera Camera => _camera;

        // True while the camera follows the view of somebody else: what the player does with it here is left alone.
        public bool Mirrored => _shared != null;

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
            _goalOrientation = CameraMath.Orientation(_goalYaw, _goalPitch);
            if (_shared != null)
                TakeShared();
            if (jump)
                Snap();
        }

        public void Reframe()
        {
            if (_shared == null)
                Frame(_settings, _layout, false);
        }

        // Shows the table the way the player whose table it is looks at it: the camera glides there, or jumps. Whatever the player here
        // does with the camera is left alone from then on, and so is the frame of the stage, until Release. Call it every frame with what
        // is known now: a new view is taken at once, and a framing of the stage does not take it away.
        public void Mirror(ViewPose pose, bool jump)
        {
            if (pose == null)
            {
                Release();
                return;
            }
            _shared = pose;
            TakeShared();
            if (jump)
                Snap();
        }

        // Gives the camera back to the player here; it stays where it is.
        public void Release()
        {
            _shared = null;
        }

        // How the camera is turned and how far it is, in terms that hold on any screen, for the other players to see the table that way.
        public ViewPose GetPose()
        {
            float zoom = _plan.Distance > 0f ? _goalDistance / _plan.Distance : 1f;
            return _plan.Trackball
                ? ViewPose.Trackball(_goalOrientation.x, _goalOrientation.y, _goalOrientation.z, _goalOrientation.w, _goalYaw, _goalPitch, zoom)
                : ViewPose.Orbit(_goalYaw, _goalPitch, zoom);
        }

        // What the view of somebody else asks for on this screen: the zoom is a multiple of the distance that frames the table here.
        private void TakeShared()
        {
            ViewPose pose = _shared;
            _goalDistance = Mathf.Clamp(pose.Zoom * _plan.Distance, _plan.MinDistance, _plan.MaxDistance);
            if (_plan.Trackball)
            {
                if (pose.HasTurn)
                    _goalOrientation = Quaternion.Normalize(new Quaternion(pose.TurnX, pose.TurnY, pose.TurnZ, pose.TurnW));
            }
            else if (!pose.HasTurn)
            {
                _goalYaw = CameraMath.WrapDegrees(pose.Yaw);
                _goalPitch = _plan.FreePitch ? CameraMath.WrapDegrees(pose.Pitch) : CameraMath.ClampPitch(pose.Pitch, _plan.MinPitch, _plan.MaxPitch);
            }
        }

        // Puts the camera where it wants to go at once.
        private void Snap()
        {
            _orientation = _goalOrientation;
            _target = _goalTarget;
            _yaw = _goalYaw;
            _pitch = _goalPitch;
            _distance = _goalDistance;
            Apply();
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
            if (_shared != null)
                return;
            _userMoved = true;
            if (_plan.Trackball)
            {
                _goalOrientation = CameraMath.Trackball(_goalOrientation, mouseDelta * DegreesPerPixel);
                return;
            }
            CameraMath.Orbit(ref _goalYaw, ref _goalPitch, mouseDelta * DegreesPerPixel, _plan.FreePitch, _plan.MinPitch, _plan.MaxPitch);
        }

        public void Pan(Vector2 mouseDelta, float screenHeight)
        {
            if (_shared != null)
                return;
            _userMoved = true;
            Vector3 moved = _goalTarget + (_plan.Trackball
                ? CameraMath.ScreenPanOffset(_goalOrientation, mouseDelta, _goalDistance, _plan.Fov, screenHeight)
                : CameraMath.PanOffset(_goalYaw, _goalPitch, mouseDelta, _goalDistance, _plan.Fov, screenHeight));
            Vector3 home = _plan.Target;
            _goalTarget = new Vector3(
                Mathf.Clamp(moved.x, home.x - _plan.Reach.x, home.x + _plan.Reach.x),
                Mathf.Clamp(moved.y, home.y - _plan.Reach.y, home.y + _plan.Reach.y),
                Mathf.Clamp(moved.z, home.z - _plan.Reach.z, home.z + _plan.Reach.z));
        }

        public void Zoom(float wheelNotches)
        {
            if (_shared != null || !_settings.Zoom)
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
            _orientation = Quaternion.Slerp(_orientation, _goalOrientation, 1f - Mathf.Exp(-Stiffness * deltaTime));
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
            Transform t = _camera.transform;
            if (_plan.Trackball)
            {
                t.position = _target - _orientation * Vector3.forward * _distance;
                t.rotation = _orientation;
            }
            else
            {
                t.position = CameraMath.Position(_target, _yaw, _pitch, _distance);
                CameraMath.Basis(_yaw, _pitch, out _, out Vector3 up, out Vector3 forward);
                t.rotation = Quaternion.LookRotation(forward, up);
            }
            _camera.nearClipPlane = Mathf.Max(0.05f, _distance * 0.02f);
            _camera.farClipPlane = _distance * 8f + 800f;
        }
    }
}
