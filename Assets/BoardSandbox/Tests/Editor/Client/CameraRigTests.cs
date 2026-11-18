using System.Collections.Generic;
using BoardSandbox.Client.Cameras;
using BoardSandbox.Client.Views;
using BoardSandbox.Core.Net;
using NUnit.Framework;
using UnityEngine;

namespace BoardSandbox.Tests
{
    // The camera that shows the table of another player the way that player looks at it.
    [TestFixture]
    public class CameraRigTests
    {
        private const float Landscape = 16f / 9f;
        private const float Portrait = 9f / 16f;

        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject holder in _objects)
                Object.DestroyImmediate(holder);
            _objects.Clear();
        }

        // The camera of the cube: it turns like a ball in the hand around a cube of radius 3 at the origin.
        private static CameraSettings Cube()
        {
            return new CameraSettings { Trackball = true, Radius = 3f, Fov = 40f, LeftDrag = DragMode.Orbit, RightDrag = DragMode.Orbit, MiddleDrag = DragMode.None };
        }

        // A camera that circles the table at fixed angles.
        private static CameraSettings Table()
        {
            return new CameraSettings { Yaw = 20f, Pitch = 55f, Radius = 3f, Fov = 40f };
        }

        private CameraRig Rig(float aspect, CameraSettings settings)
        {
            GameObject holder = new GameObject("camera rig test");
            _objects.Add(holder);
            Camera camera = holder.AddComponent<Camera>();
            camera.aspect = aspect;
            CameraRig rig = new CameraRig(camera);
            rig.Frame(settings, null, true);
            return rig;
        }

        private static void Settle(params CameraRig[] rigs)
        {
            for (int i = 0; i < 300; i++)
            {
                foreach (CameraRig rig in rigs)
                    rig.Tick(0.05f);
            }
        }

        // The table is at the origin, so how far the camera is from it is where it is.
        private static float Distance(CameraRig rig)
        {
            return rig.Camera.transform.position.magnitude;
        }

        private static float Apart(CameraRig a, CameraRig b)
        {
            return Quaternion.Angle(a.Camera.transform.rotation, b.Camera.transform.rotation);
        }

        [Test]
        public void ATableTurnedAndZoomedOnOneScreenLooksTheSameOnAnotherScreenOfAnotherShape()
        {
            CameraRig owner = Rig(Landscape, Cube());
            CameraRig watcher = Rig(Portrait, Cube());
            float ownerStart = Distance(owner);
            float watcherStart = Distance(watcher);
            Assert.Greater(watcherStart, ownerStart * 1.2f, "a tall screen needs the camera further away to see the same cube");

            owner.Drag(DragMode.Orbit, new Vector2(120f, 40f), 800f);
            owner.Zoom(-3f);
            ViewPose pose = owner.GetPose();
            Assert.IsTrue(pose.HasTurn);
            Assert.Less(pose.Zoom, 0.9f);

            watcher.Mirror(pose, true);
            Settle(owner, watcher);
            Assert.Less(Apart(owner, watcher), 0.5f, "the same side of the cube is seen");
            Assert.AreEqual(Distance(owner) / ownerStart, Distance(watcher) / watcherStart, 0.01f, "and as close, for the screen it is on");
            Assert.AreEqual(owner.GetPose().Zoom, watcher.GetPose().Zoom, 1e-3f);
            Assert.Less(watcher.GetPose().TurnAngleTo(owner.GetPose()), 0.5f);
        }

        [Test]
        public void ACameraThatFollowsAViewLeavesAloneWhatIsDoneToIt()
        {
            CameraRig owner = Rig(Landscape, Cube());
            CameraRig watcher = Rig(Landscape, Cube());
            owner.Drag(DragMode.Orbit, new Vector2(-90f, 60f), 800f);
            owner.Zoom(2f);
            watcher.Mirror(owner.GetPose(), true);
            Settle(owner, watcher);
            Assert.IsTrue(watcher.Mirrored);

            Quaternion before = watcher.Camera.transform.rotation;
            float distance = Distance(watcher);
            watcher.Drag(DragMode.Orbit, new Vector2(300f, -200f), 800f);
            watcher.Drag(DragMode.Pan, new Vector2(50f, 50f), 800f);
            watcher.Zoom(6f);
            watcher.Reframe();
            Settle(owner, watcher);
            Assert.Less(Quaternion.Angle(before, watcher.Camera.transform.rotation), 0.5f);
            Assert.AreEqual(distance, Distance(watcher), distance * 0.01f);
            Assert.Less(Apart(owner, watcher), 0.5f);
        }

        [Test]
        public void TheStageFramingTheTableAgainDoesNotTakeTheSharedViewAway()
        {
            CameraRig owner = Rig(Landscape, Cube());
            CameraRig watcher = Rig(Landscape, Cube());
            owner.Drag(DragMode.Orbit, new Vector2(100f, 100f), 800f);
            owner.Zoom(-2f);
            watcher.Mirror(owner.GetPose(), true);

            // A new game, or a new stage of the same game, frames the table afresh; the next call to Mirror brings the view back at once.
            watcher.Frame(Cube(), null, false);
            Settle(owner, watcher);
            Assert.Less(Apart(owner, watcher), 0.5f);
            watcher.Frame(Cube(), null, true);
            Assert.Less(Apart(owner, watcher), 0.5f, "even where the framing jumps");
        }

        [Test]
        public void ACameraGoesBackToThePlayerHereOnceItIsReleased()
        {
            CameraRig owner = Rig(Landscape, Cube());
            CameraRig watcher = Rig(Landscape, Cube());
            owner.Drag(DragMode.Orbit, new Vector2(200f, 0f), 800f);
            watcher.Mirror(owner.GetPose(), true);
            Settle(owner, watcher);

            watcher.Release();
            Assert.IsFalse(watcher.Mirrored);
            Quaternion before = watcher.Camera.transform.rotation;
            watcher.Drag(DragMode.Orbit, new Vector2(-300f, 100f), 800f);
            Settle(watcher);
            Assert.Greater(Quaternion.Angle(before, watcher.Camera.transform.rotation), 5f, "the player here turns it again");

            watcher.Mirror(owner.GetPose(), true);
            Assert.IsTrue(watcher.Mirrored);
            watcher.Mirror(null, false);
            Assert.IsFalse(watcher.Mirrored, "no view to follow is none to follow");
        }

        [Test]
        public void AViewIsTakenAtOnceWhereTheCameraIsToJumpAndGlidedToWhereItIsNot()
        {
            CameraRig owner = Rig(Landscape, Cube());
            owner.Drag(DragMode.Orbit, new Vector2(400f, 100f), 800f);
            Settle(owner);

            CameraRig jumping = Rig(Landscape, Cube());
            jumping.Mirror(owner.GetPose(), true);
            Assert.Less(Apart(owner, jumping), 0.5f, "there at once");

            CameraRig gliding = Rig(Landscape, Cube());
            gliding.Mirror(owner.GetPose(), false);
            Assert.Greater(Apart(owner, gliding), 5f, "not before it has had time to go there");
            gliding.Tick(0.05f);
            float soon = Apart(owner, gliding);
            Assert.Greater(soon, 0.5f);
            Settle(gliding);
            Assert.Less(Apart(owner, gliding), 0.5f, "and then it is");
        }

        [Test]
        public void ACameraThatCirclesTheTableSharesItsAnglesAndItsZoom()
        {
            CameraRig owner = Rig(Landscape, Table());
            CameraRig watcher = Rig(Portrait, Table());
            float ownerStart = Distance(owner);
            float watcherStart = Distance(watcher);

            owner.Drag(DragMode.Orbit, new Vector2(-150f, 60f), 800f);
            owner.Zoom(3f);
            ViewPose pose = owner.GetPose();
            Assert.IsFalse(pose.HasTurn, "it has angles, not a turn");
            Assert.Greater(pose.Zoom, 1.1f);

            watcher.Mirror(pose, true);
            Settle(owner, watcher);
            Assert.Less(Apart(owner, watcher), 0.5f);
            Assert.AreEqual(Distance(owner) / ownerStart, Distance(watcher) / watcherStart, 0.01f);
        }

        [Test]
        public void AViewIsKeptWithinWhatTheStageAllows()
        {
            CameraRig watcher = Rig(Landscape, Cube());
            watcher.Mirror(ViewPose.Trackball(0f, 0f, 0f, 1f, 0f, 0f, ViewPose.MaxZoom), true);
            Assert.AreEqual(3f, watcher.GetPose().Zoom, 0.01f, "as far as the camera may go, which is three times the distance that frames the table");
            watcher.Mirror(ViewPose.Trackball(0f, 0f, 0f, 1f, 0f, 0f, ViewPose.MinZoom), true);
            Assert.Greater(watcher.GetPose().Zoom, ViewPose.MinZoom, "and as close as it may come");

            // A table that does not let the camera zoom has a zoom of one, and it stays that way.
            CameraSettings fixedDistance = Cube();
            fixedDistance.Zoom = false;
            CameraRig owner = Rig(Landscape, fixedDistance);
            owner.Zoom(5f);
            Assert.AreEqual(1f, owner.GetPose().Zoom, 1e-4f);
        }
    }
}
