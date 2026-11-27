using System.Collections;
using System.Linq;
using NUnit.Framework;
using Tinyverse.Building;
using Tinyverse.World;
using UnityEngine;
using UnityEngine.TestTools;
using P = Tinyverse.Proto;
using UIBuildTool = Tinyverse.UI.BuildTool;

namespace Tinyverse.Bootstrap.Tests
{
    /// <summary>BuildController driven by ScriptedBuildInput through the real session and server.</summary>
    public sealed class BuildTests
    {
        ServerFixture server;
        SessionHarness h;
        Observer owner;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            server = new ServerFixture();
            yield return server.Start();
            h = new SessionHarness();
            owner = new Observer();
            yield return owner.Join(server.Url, "Owner", server.OwnerToken);
        }

        [TearDown]
        public void TearDown()
        {
            owner?.Dispose();
            h?.Dispose();
            server?.Dispose();
            server?.AssertNothingLeft();
        }

        Vector2 Screen(Vector3 world) => h.Camera.WorldToScreenPoint(world);

        float GizmoSize(Vector3 p)
        {
            var t = h.Camera.transform;
            return BuildMath.ScreenConstantSize(false, h.Camera.fieldOfView, 0f, t.position, t.forward, p, BuildController.GizmoScreenFraction);
        }

        IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        IEnumerator Tap(BuildKeys key, bool ctrl = false, bool shift = false)
        {
            h.BuildInput.Ctrl = ctrl;
            h.BuildInput.Shift = shift;
            yield return null;
            h.BuildInput.Tap(key);
            yield return Frames(2);
            h.BuildInput.Ctrl = false;
            h.BuildInput.Shift = false;
            yield return null;
        }

        P.ObjectInfo Only() => owner.Objects.Values.Single();

        [UnityTest]
        public IEnumerator PlaceMoveRecolourDuplicateDeleteUndoRedo()
        {
            yield return h.Join(server.Url, "Mason");
            var build = h.Session.Build;
            Assert.IsNotNull(build);
            h.Session.SetBuildMode(true);
            Assert.IsTrue(h.UI.IsBuildMode, "the panel is shown");
            Assert.IsTrue(h.UI.Build.BuildAllowed);
            yield return Wait.Seconds(0.5f); // let the camera settle behind the avatar

            // Place: a click on the ground in front of the avatar.
            h.UI.Build.ChooseTool(UIBuildTool.Place);
            Assert.AreEqual(BuildTool.Place, build.Tool, "panel → controller");
            build.CurrentShape = PrimShape.Box;
            build.CurrentColor = new Color(1f, 128f / 255f, 0f);
            h.BuildInput.HasPointer = true;
            h.BuildInput.Pointer = Screen(new Vector3(0, 0, 4));
            yield return Frames(3);
            Assert.IsTrue(build.PreviewVisible, "the placement preview shows");
            h.BuildInput.Press();
            yield return null;
            h.BuildInput.Release();
            yield return Wait.For(() => owner.Objects.Count == 1, 5f, "the server to create the box");
            var created = Only();
            Assert.AreEqual(P.Shape.Box, created.Spec.Shape);
            Assert.AreEqual(4f, created.Spec.Position.Z, 0.3f);
            Assert.AreEqual(0.5f, created.Spec.Position.Y, 0.05f, "resting on the ground");
            Assert.AreEqual(h.Session.UserId, created.OwnerId);
            yield return Wait.For(() => build.SelectedId == created.Id, 5f, "the new box to be selected");
            Assert.IsTrue(h.UI.Build.HasSelection, "the inspector shows it");
            Assert.AreEqual("you", h.Session.OwnerName(created.OwnerId));

            // Move: drag the X arrow of the gizmo 2 m to the right.
            h.UI.Build.ChooseTool(UIBuildTool.Move);
            Assert.AreEqual(BuildTool.Move, build.Tool);
            var start = ProtoConvert.ToVector3(created.Spec.Position);
            float size = GizmoSize(start);
            float grab = 0.6f * size;
            h.BuildInput.Pointer = Screen(start + Vector3.right * grab);
            yield return Frames(3);
            Assert.AreEqual(BuildHandle.MoveX, build.HoveredHandle, "pointer on the X arrow");
            h.BuildInput.Press();
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 0.6f)
            {
                float t = (Time.realtimeSinceStartup - t0) / 0.6f;
                h.BuildInput.Pointer = Screen(start + Vector3.right * (grab + 2f * t));
                yield return null;
            }
            h.BuildInput.Pointer = Screen(start + Vector3.right * (grab + 2f));
            yield return null;
            h.BuildInput.Release();
            yield return Wait.For(() => Mathf.Abs(Only().Spec.Position.X - (start.x + 2f)) < 0.3f, 5f, "the server to have the moved box");
            yield return Wait.For(() => !h.Session.Objects.IsLocallyControlled(created.Id), 2f, "local control to end");
            Assert.AreEqual(Only().Spec.Position.X, h.Session.Objects.GetGameObject(created.Id).transform.position.x, 0.01f);
            Assert.AreEqual(0, h.Session.CorrectionCount);

            // Recolour the selection (what the panel's ColorCommitted does).
            build.CurrentColor = Color.green;
            build.ApplyColorToSelection();
            yield return Wait.For(() => Only().Spec.Color.G == 1f && Only().Spec.Color.R == 0f, 5f, "the colour on the server");

            // Duplicate with Ctrl+D: the avatar must not strafe meanwhile.
            var avatarBefore = h.Session.LocalAvatar.transform.position;
            yield return Tap(BuildKeys.D, ctrl: true);
            yield return Wait.For(() => owner.Objects.Count == 2, 5f, "the duplicate");
            Assert.Less(Vector3.Distance(avatarBefore, h.Session.LocalAvatar.transform.position), 0.01f, "Ctrl+D did not move the avatar");
            var copy = owner.Objects.Values.First(o => o.Id != created.Id);
            Assert.AreEqual(1f, copy.Spec.Color.G);
            yield return Wait.For(() => build.SelectedId == copy.Id, 5f, "the copy to be selected");

            // Delete it, undo, redo.
            yield return Tap(BuildKeys.Delete);
            yield return Wait.For(() => owner.Objects.Count == 1, 5f, "the deletion");
            yield return Wait.For(() => build.CanUndo, 5f, "undo to be possible");
            Assert.IsTrue(h.UI.Build.CanUndo, "controller → panel undo state");
            yield return Tap(BuildKeys.Z, ctrl: true);
            yield return Wait.For(() => owner.Objects.Count == 2, 5f, "undo to recreate the copy");
            yield return Wait.For(() => build.CanRedo, 5f, "redo to be possible");
            yield return Tap(BuildKeys.Y, ctrl: true);
            yield return Wait.For(() => owner.Objects.Count == 1, 5f, "redo to delete it again");
            Assert.AreEqual(created.Id, Only().Id);

            // The panel's tool and the controller agree after a number key.
            h.UI.HandleKey(UnityEngine.InputSystem.Key.Digit4);
            Assert.AreEqual(BuildTool.Rotate, build.Tool);
            Assert.AreEqual(UIBuildTool.Rotate, h.UI.Build.Tool);
            Assert.IsFalse(h.UI.Toasts.Messages.Any(m => m.Contains("not allowed")), string.Join("; ", h.UI.Toasts.Messages));
        }

        [UnityTest]
        public IEnumerator Visitor_IsRefusedWithAReadableToast()
        {
            yield return h.Join(server.Url, "Guest");
            var build = h.Session.Build;
            h.Session.SetBuildMode(true);

            // The owner makes the guest a visitor.
            var set = owner.Conn.RequestAsync(new P.ClientMessage
            {
                Admin = new P.Admin { SetRole = new P.Admin.Types.SetRole { UserId = h.Session.UserId, Role = P.Role.Visitor } },
            });
            yield return Wait.Task(set, 5f, "set-role");
            yield return Wait.For(() => h.Session.Role == P.Role.Visitor, 5f, "the role change");
            Assert.IsFalse(build.CanBuild);
            Assert.IsFalse(h.UI.Build.BuildAllowed, "the panel says visitors cannot build");
            Assert.IsTrue(h.UI.Toasts.Messages.Any(m => m.Contains("visitor")), "told about the new role");

            // Locally refused…
            Assert.AreEqual(0, build.PlaceInFrontOfAvatar());
            Assert.IsTrue(h.UI.Toasts.Messages.Any(m => m.Contains("permission to build")), string.Join("; ", h.UI.Toasts.Messages));

            // …and refused by the server when sent anyway.
            h.Session.Connection.CreateObject(ProtoConvert.ToSpec(new PrimData { Shape = PrimShape.Box, Scale = Vector3.one, Rotation = Quaternion.identity, Color = Color.white }));
            string denied = ErrorText.Describe(P.ErrorCode.PermissionDenied);
            yield return Wait.For(() => h.UI.Toasts.Messages.Any(m => m.StartsWith(denied)), 5f, "the permission toast");
            Assert.AreEqual(0, owner.Objects.Count);
        }

        [UnityTest]
        public IEnumerator LockedObjectOfSomeoneElse_IsReadOnly()
        {
            var create = server.CliRun("--name", "Other", "create", "--shape", "box", "--pos", "0,0.5,4", "--locked", "true");
            yield return Wait.Task(create, 20f, "tv-cli create");
            ulong id = ServerFixture.ParseObjectId(create.Result.output);
            yield return h.Join(server.Url, "Neighbour");
            h.Session.SetBuildMode(true);
            h.Session.Build.Select(id);
            yield return null;
            Assert.IsFalse(h.Session.Build.SelectionEditable);
            Assert.IsTrue(h.UI.Build.IsInspectorReadOnly);
            // "Other" has left, so the inspector names the owner by a short user id.
            Assert.That(h.UI.Build.Selection.OwnerName, Does.StartWith("user "));
            // Edits are refused locally with a toast.
            var d = h.Session.Objects.All.Single();
            d.Position += Vector3.right;
            h.Session.Build.SetSelectedData(d);
            Assert.IsTrue(h.UI.Toasts.Messages.Any(m => m.Contains("can't edit")), string.Join("; ", h.UI.Toasts.Messages));
        }
    }
}
