using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Tinyverse.World;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Building.Tests
{
    /// <summary>
    /// Drives a real <see cref="BuildController"/> and <see cref="ObjectRegistry"/> with scripted input. A small
    /// fake server answers the intents the way the glue and the real server would.
    /// </summary>
    public class BuildControllerPlayModeTests
    {
        sealed class FakeServer
        {
            readonly ObjectRegistry reg;
            readonly BuildController c;
            public ulong NextId = 1000;
            public bool AutoCreate = true, EchoUpdates = true, AutoDelete = true;
            public readonly List<(PrimData data, int token)> Creates = new List<(PrimData, int)>();
            public readonly List<(ulong id, PrimData data, float time, bool dragging)> Updates = new List<(ulong, PrimData, float, bool)>();
            public readonly List<ulong> Deletes = new List<ulong>();
            public readonly List<string> Refusals = new List<string>();
            public readonly List<PrimData?> Selections = new List<PrimData?>();

            public FakeServer(ObjectRegistry reg, BuildController c)
            {
                this.reg = reg;
                this.c = c;
                c.CreateRequested += (d, token) =>
                {
                    Creates.Add((d, token));
                    if (!AutoCreate) return;
                    d.Id = NextId++;
                    d.OwnerId = "me";
                    reg.Upsert(d);
                    c.NotifyCreated(token, d.Id);
                };
                c.UpdateRequested += (id, d) =>
                {
                    Updates.Add((id, d, Time.unscaledTime, c.IsDragging));
                    if (EchoUpdates) reg.Upsert(d, smooth: true);
                };
                c.DeleteRequested += id =>
                {
                    Deletes.Add(id);
                    if (AutoDelete) reg.Remove(id);
                };
                c.Refused += m => Refusals.Add(m);
                c.SelectionChanged += s => Selections.Add(s);
            }
        }

        GameObject root, ground, camGo;
        Camera cam;
        ObjectRegistry reg;
        BuildController c;
        ScriptedBuildInput input;
        FakeServer server;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("BuildTestWorld");
            reg = new ObjectRegistry(root.transform);

            ground = new GameObject("Ground");
            var box = ground.AddComponent<BoxCollider>();
            box.size = new Vector3(200, 1, 200);
            ground.transform.position = new Vector3(0, -0.5f, 0);

            camGo = new GameObject("BuildTestCamera");
            cam = camGo.AddComponent<Camera>();
            Debug.Log($"[BuildTests] camera pixelRect {cam.pixelRect} screen {UnityEngine.Screen.width}x{UnityEngine.Screen.height}");
            cam.fieldOfView = 60f;
            camGo.transform.position = new Vector3(0, 6, -9);
            camGo.transform.LookAt(new Vector3(0, 0.5f, 0));

            input = new ScriptedBuildInput { HasPointer = true };
            c = BuildController.Create(reg, cam, input);
            c.IsPlacementSurface = col => col.gameObject == ground;
            c.SetLimits(new Vector3(-50, -1, -50), new Vector3(50, 40, 50), 0.01f, 64f);
            c.Enabled = true;
            server = new FakeServer(reg, c);
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            Object.Destroy(c.gameObject);
            reg.Clear();
            Object.Destroy(root);
            Object.Destroy(ground);
            Object.Destroy(camGo);
        }

        static PrimData Box(ulong id, Vector3 pos) => new PrimData
        {
            Id = id,
            OwnerId = "me",
            Shape = PrimShape.Box,
            Material = PrimMaterial.Matte,
            Position = pos,
            Rotation = Quaternion.identity,
            Scale = Vector3.one,
            Color = Color.white,
            Solid = true,
        };

        Vector2 Screen(Vector3 world) => cam.WorldToScreenPoint(world);

        float GizmoSize(Vector3 p) => BuildMath.ScreenConstantSize(false, cam.fieldOfView, cam.orthographicSize,
            cam.transform.position, cam.transform.forward, p, BuildController.GizmoScreenFraction);

        IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        IEnumerator Click(Vector3 world)
        {
            input.Pointer = Screen(world);
            yield return null;
            input.Press();
            yield return null;
            input.Release();
            yield return null;
        }

        // Presses at `from`, moves the pointer over `seconds` to `to` along a world-space path, releases.
        IEnumerator Drag(System.Func<float, Vector3> path, float seconds, bool release = true)
        {
            input.Pointer = Screen(path(0));
            yield return null;
            input.Press();
            yield return null;
            float start = Time.unscaledTime;
            while (Time.unscaledTime - start < seconds)
            {
                float t = Mathf.Clamp01((Time.unscaledTime - start) / seconds);
                input.Pointer = Screen(path(t));
                yield return null;
            }
            input.Pointer = Screen(path(1));
            yield return null;
            if (release)
            {
                input.Release();
                yield return null;
            }
        }

        // ------------------------------------------------------------------ assets

        [Test]
        public void ResourcesAssetsLoad()
        {
            var g = Resources.Load<Material>(BuildAssets.GizmoMaterialPath);
            var p = Resources.Load<Material>(BuildAssets.PreviewMaterialPath);
            Assert.That(g, Is.Not.Null);
            Assert.That(p, Is.Not.Null);
            Assert.That(g.shader.name, Is.EqualTo(BuildAssets.ShaderName));
            Assert.That(p.shader.name, Is.EqualTo(BuildAssets.ShaderName));
            Assert.That(g.GetFloat("_ZTest"), Is.EqualTo((float)UnityEngine.Rendering.CompareFunction.Always));
            Assert.That(g.renderQueue, Is.GreaterThanOrEqualTo(4000));
            Assert.That(BuildAssets.GizmoMaterial, Is.SameAs(g));
            Assert.That(BuildAssets.PreviewMaterial, Is.SameAs(p));
        }

        // ------------------------------------------------------------------ place

        [UnityTest]
        public IEnumerator PlaceRaisesCreateAndNotifyCreatedSelects()
        {
            c.Tool = BuildTool.Place;
            c.CurrentShape = PrimShape.Cylinder;
            c.CurrentColor = new Color(0.2f, 0.4f, 0.6f, 1f);
            c.CurrentMaterial = PrimMaterial.Metal;
            c.SnapEnabled = true;
            c.MoveSnap = 0.5f;
            c.PlaceScale = new Vector3(1, 2, 1);

            input.Pointer = Screen(new Vector3(1.2f, 0, 2.1f));
            yield return Frames(2);
            Assert.That(c.PreviewVisible, Is.True);
            Assert.That(c.PreviewValid, Is.True);
            var p = c.PreviewData.Position;
            Assert.That(p.x, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(p.z, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(p.y, Is.EqualTo(1f + BuildMath.RestGap).Within(1e-3f), "rests on the ground");

            input.Press();
            yield return null;
            input.Release();
            yield return null;

            Assert.That(server.Creates.Count, Is.EqualTo(1));
            var d = server.Creates[0].data;
            Assert.That(d.Id, Is.EqualTo(0UL));
            Assert.That(d.Shape, Is.EqualTo(PrimShape.Cylinder));
            Assert.That(d.Material, Is.EqualTo(PrimMaterial.Metal));
            Assert.That(d.Color, Is.EqualTo(new Color(0.2f, 0.4f, 0.6f, 1f)));
            Assert.That(d.Scale, Is.EqualTo(new Vector3(1, 2, 1)));
            Assert.That((d.Position - p).magnitude, Is.LessThan(1e-4f));

            ulong id = server.NextId - 1;
            Assert.That(c.SelectedId, Is.EqualTo(id));
            Assert.That(reg.IsHighlighted(id), Is.True);
            Assert.That(c.CanUndo, Is.True);
            Assert.That(c.Tool, Is.EqualTo(BuildTool.Place), "stays in place");
        }

        [UnityTest]
        public IEnumerator PlaceOnTopOfAPrimAndFloatingWithoutSurface()
        {
            reg.Upsert(Box(1, new Vector3(0, 0.5f, 0)));
            c.Tool = BuildTool.Place;
            c.SnapEnabled = false;
            input.Pointer = Screen(new Vector3(0.1f, 1f, -0.1f));
            yield return Frames(2);
            Assert.That(c.PreviewData.Position.y, Is.EqualTo(1.5f + BuildMath.RestGap).Within(1e-3f));

            // Pointing at the sky: floats in front of the camera.
            var ct = cam.transform;
            input.Pointer = Screen(ct.position + ct.forward * 10f + ct.up * 8f); // above the horizon
            Assert.That(cam.ScreenPointToRay(input.Pointer).direction.y, Is.GreaterThan(0f));
            yield return Frames(2);
            Assert.That(c.PreviewVisible, Is.True);
            var ray = cam.ScreenPointToRay(input.Pointer);
            Assert.That((c.PreviewData.Position - ray.GetPoint(BuildController.FloatDistance)).magnitude, Is.LessThan(1e-3f));
        }

        [UnityTest]
        public IEnumerator PlaceOutOfBoundsIsRefusedAndTintedInvalid()
        {
            c.SetLimits(new Vector3(-2, -1, -2), new Vector3(2, 10, 2), 0.01f, 64f);
            c.Tool = BuildTool.Place;
            input.Pointer = Screen(new Vector3(4, 0, 4));
            yield return Frames(2);
            Assert.That(c.PreviewVisible, Is.True);
            Assert.That(c.PreviewValid, Is.False);
            input.Press();
            yield return null;
            input.Release();
            yield return null;
            Assert.That(server.Creates, Is.Empty);
            Assert.That(server.Refusals.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CanBuildFalseBlocksPlacement()
        {
            c.CanBuild = false;
            c.Tool = BuildTool.Place;
            yield return Click(new Vector3(1, 0, 1));
            Assert.That(server.Creates, Is.Empty);
            Assert.That(server.Refusals.Count, Is.EqualTo(1));
            Assert.That(c.PreviewValid, Is.False);
        }

        // ------------------------------------------------------------------ select

        [UnityTest]
        public IEnumerator ClickSelectsAndEmptyClickDeselects()
        {
            reg.Upsert(Box(1, new Vector3(0, 0.5f, 0)));
            var plane = Box(2, new Vector3(3, 0.5f, 0));
            plane.Solid = false; // trigger collider must still be pickable
            reg.Upsert(plane);
            c.Tool = BuildTool.Select;

            yield return Click(new Vector3(0, 0.5f, -0.5f));
            Assert.That(c.SelectedId, Is.EqualTo(1UL));
            Assert.That(reg.IsHighlighted(1), Is.True);
            Assert.That(server.Selections[server.Selections.Count - 1]?.Id, Is.EqualTo(1UL));

            yield return Click(new Vector3(3, 0.5f, -0.5f));
            Assert.That(c.SelectedId, Is.EqualTo(2UL));
            Assert.That(reg.IsHighlighted(1), Is.False);
            Assert.That(reg.IsHighlighted(2), Is.True);

            yield return Click(new Vector3(-4, 0, 3));
            Assert.That(c.SelectedId, Is.Null);
            Assert.That(reg.IsHighlighted(2), Is.False);
            Assert.That(server.Selections[server.Selections.Count - 1].HasValue, Is.False);
        }

        [UnityTest]
        public IEnumerator AvatarCollidersAreIgnoredByPicking()
        {
            reg.Upsert(Box(1, new Vector3(0, 0.5f, 0)));
            var avatar = new GameObject("Avatar");
            avatar.AddComponent<CapsuleCollider>().height = 2f;
            avatar.transform.position = new Vector3(0, 1f, -2f); // between the camera and the box
            Physics.SyncTransforms();
            c.IsPlacementSurface = null; // default heuristic
            c.Tool = BuildTool.Select;
            yield return Click(new Vector3(0, 0.5f, -0.5f));
            Object.Destroy(avatar);
            Assert.That(c.SelectedId, Is.EqualTo(1UL));
        }

        [UnityTest]
        public IEnumerator SelectionClearedWhenPrimRemoved()
        {
            reg.Upsert(Box(1, new Vector3(0, 0.5f, 0)));
            c.Tool = BuildTool.Move;
            c.Select(1);
            yield return null;
            Assert.That(c.GizmoVisible, Is.True);
            reg.Remove(1);
            Assert.That(c.SelectedId, Is.Null);
            Assert.That(server.Selections[server.Selections.Count - 1].HasValue, Is.False);
            yield return null;
            Assert.That(c.GizmoVisible, Is.False);
        }

        [UnityTest]
        public IEnumerator RemoteChangeMovesGizmoAndReportsData()
        {
            reg.Upsert(Box(1, new Vector3(0, 0.5f, 0)));
            c.Tool = BuildTool.Move;
            c.Select(1);
            PrimData? seen = null;
            c.SelectedDataChanged += d => seen = d;
            reg.Upsert(Box(1, new Vector3(2, 0.5f, 1)));
            Assert.That(seen?.Position, Is.EqualTo(new Vector3(2, 0.5f, 1)));
            yield return null;
            input.Pointer = Screen(new Vector3(2, 0.5f, 1) + Vector3.right * (0.7f * GizmoSize(new Vector3(2, 0.5f, 1))));
            yield return Frames(2);
            Assert.That(c.HoveredHandle, Is.EqualTo(BuildHandle.MoveX));
        }

        // ------------------------------------------------------------------ move

        [UnityTest]
        public IEnumerator MoveDragIsThrottledHoldsControlAndReleases()
        {
            var start = new Vector3(0, 0.5f, 0);
            reg.Upsert(Box(1, start));
            server.EchoUpdates = false; // the server is silent: release must come from the timeout
            c.Tool = BuildTool.Move;
            c.SnapEnabled = true;
            c.MoveSnap = 0.5f;
            c.Select(1);
            yield return null;

            float size = GizmoSize(start);
            float grab = 0.6f * size;
            input.Pointer = Screen(start + Vector3.right * grab);
            yield return Frames(2);
            Assert.That(c.HoveredHandle, Is.EqualTo(BuildHandle.MoveX));

            bool heldThroughout = true;
            c.SelectedDataChanged += _ => heldThroughout &= reg.IsLocallyControlled(1);
            float t0 = Time.unscaledTime;
            yield return Drag(t => start + Vector3.right * (grab + 3.1f * t), 1.0f);
            float duration = Time.unscaledTime - t0;

            Assert.That(heldThroughout, Is.True);
            var during = server.Updates.FindAll(u => u.dragging);
            var after = server.Updates.FindAll(u => !u.dragging);
            Assert.That(during.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(during.Count, Is.LessThanOrEqualTo(Mathf.CeilToInt(duration * BuildController.UpdateRate) + 1));
            for (int i = 1; i < during.Count; i++)
                Assert.That(during[i].time - during[i - 1].time, Is.GreaterThanOrEqualTo(0.099f), $"gap {i}");
            Assert.That(after.Count, Is.EqualTo(1), "one final update");
            var final = after[0].data;
            Assert.That(final.Position.x, Is.EqualTo(3f).Within(1e-4f), "snapped to 0.5");
            Assert.That(final.Position.y, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(final.Position.z, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(reg.GetGameObject(1).transform.position.x, Is.EqualTo(3f).Within(1e-4f));

            // Still held while waiting for the echo, then released by the timeout.
            Assert.That(reg.IsLocallyControlled(1), Is.True);
            Assert.That(c.IsAwaitingEcho(1), Is.True);
            float wait = Time.unscaledTime;
            while (reg.IsLocallyControlled(1) && Time.unscaledTime - wait < 2f) yield return null;
            Assert.That(reg.IsLocallyControlled(1), Is.False);
            Assert.That(Time.unscaledTime - wait, Is.GreaterThan(0.3f));
            Assert.That(c.CanUndo, Is.True);
        }

        [UnityTest]
        public IEnumerator EchoReleasesControlImmediately()
        {
            var start = new Vector3(0, 0.5f, 0);
            reg.Upsert(Box(1, start));
            c.Tool = BuildTool.Move;
            c.Select(1);
            yield return null;
            float size = GizmoSize(start);
            yield return Drag(t => start + Vector3.up * (0.6f * size + 2f * t), 0.3f);
            Assert.That(reg.IsLocallyControlled(1), Is.False);
            reg.TryGet(1, out var d);
            Assert.That(d.Position.y, Is.EqualTo(2.5f).Within(1e-4f));
        }

        [UnityTest]
        public IEnumerator PlaneHandleMovesInPlane()
        {
            var start = new Vector3(0, 0.5f, 0);
            reg.Upsert(Box(1, start));
            c.Tool = BuildTool.Move;
            c.SnapEnabled = false;
            c.Select(1);
            yield return null;
            float size = GizmoSize(start);
            var grab = start + new Vector3(0.37f, 0, 0.37f) * size;
            input.Pointer = Screen(grab);
            yield return Frames(2);
            Assert.That(c.HoveredHandle, Is.EqualTo(BuildHandle.MoveXZ));
            yield return Drag(t => grab + new Vector3(1.5f, 0, -2f) * t, 0.3f);
            reg.TryGet(1, out var d);
            Assert.That((d.Position - new Vector3(1.5f, 0.5f, -2f)).magnitude, Is.LessThan(0.01f));
        }

        [UnityTest]
        public IEnumerator MoveIsClampedToBounds()
        {
            c.SetLimits(new Vector3(-2, -1, -2), new Vector3(2, 10, 2), 0.01f, 64f);
            var start = new Vector3(0, 0.5f, 0);
            reg.Upsert(Box(1, start));
            c.Tool = BuildTool.Move;
            c.Select(1);
            yield return null;
            float size = GizmoSize(start);
            yield return Drag(t => start + Vector3.right * (0.6f * size + 6f * t), 0.3f);
            reg.TryGet(1, out var d);
            Assert.That(d.Position.x, Is.EqualTo(2f).Within(1e-4f));
        }

        [UnityTest]
        public IEnumerator EscapeCancelsDragAndRestores()
        {
            var start = new Vector3(0, 0.5f, 0);
            reg.Upsert(Box(1, start));
            server.EchoUpdates = false;
            c.Tool = BuildTool.Move;
            c.Select(1);
            yield return null;
            float size = GizmoSize(start);
            yield return Drag(t => start + Vector3.right * (0.6f * size + 2f * t), 0.35f, release: false);
            Assert.That(c.IsDragging, Is.True);
            Assert.That(reg.GetGameObject(1).transform.position.x, Is.GreaterThan(1f));
            int before = server.Updates.Count;
            Assert.That(before, Is.GreaterThan(0));

            input.Tap(BuildKeys.Escape);
            yield return null;
            Assert.That(c.IsDragging, Is.False);
            Assert.That(reg.GetGameObject(1).transform.position, Is.EqualTo(start));
            // Intermediate states had been sent: the original is asked back.
            Assert.That(server.Updates.Count, Is.EqualTo(before + 1));
            Assert.That(server.Updates[server.Updates.Count - 1].data.Position, Is.EqualTo(start));
            Assert.That(c.CanUndo, Is.False, "a cancelled drag records nothing");
            input.Release();
            yield return null;
            Assert.That(server.Updates.Count, Is.EqualTo(before + 1), "release after cancel sends nothing");
            Assert.That(c.SelectedId, Is.EqualTo(1UL), "first Escape only cancels");

            input.Tap(BuildKeys.Escape);
            yield return null;
            Assert.That(c.SelectedId, Is.Null, "second Escape deselects");
        }

        // ------------------------------------------------------------------ rotate and scale

        [UnityTest]
        public IEnumerator RotateDragAboutY()
        {
            var start = new Vector3(0, 0.5f, 0);
            reg.Upsert(Box(1, start));
            c.Tool = BuildTool.Rotate;
            c.SnapEnabled = true;
            c.RotateSnap = 15f;
            c.Select(1);
            yield return null;
            float size = GizmoSize(start);
            Vector3 Ring(float deg) => start + new Vector3(Mathf.Cos(deg * Mathf.Deg2Rad), 0, Mathf.Sin(deg * Mathf.Deg2Rad)) * size;
            input.Pointer = Screen(Ring(-45));
            yield return Frames(2);
            Assert.That(c.HoveredHandle, Is.EqualTo(BuildHandle.RotateY));
            yield return Drag(t => Ring(-45 + 90 * t), 0.4f);
            reg.TryGet(1, out var d);
            float expected = Vector3.SignedAngle(Ring(-45) - start, Ring(45) - start, Vector3.up);
            Assert.That(Mathf.Abs(expected), Is.EqualTo(90f).Within(0.1f));
            Assert.That(BuildMath.AngleBetween(d.Rotation, Quaternion.AngleAxis(expected, Vector3.up)), Is.LessThan(0.5f));
            // The grabbed point follows the pointer.
            var carried = d.Rotation * (Ring(-45) - start);
            Assert.That(Vector3.Angle(carried, Ring(45) - start), Is.LessThan(1f));
        }

        [UnityTest]
        public IEnumerator ScaleAxisAndUniformDrags()
        {
            var start = new Vector3(0, 0.5f, 0);
            reg.Upsert(Box(1, start));
            c.Tool = BuildTool.Scale;
            c.SnapEnabled = true;
            c.ScaleSnap = 0.25f;
            c.Select(1);
            yield return null;
            float size = GizmoSize(start);
            float h = GizmoGeometry.ScaleHandleDistance * size;
            input.Pointer = Screen(start + Vector3.right * h);
            yield return Frames(2);
            Assert.That(c.HoveredHandle, Is.EqualTo(BuildHandle.ScaleX));
            yield return Drag(t => start + Vector3.right * (h * (1f + t)), 0.3f);
            reg.TryGet(1, out var d);
            Assert.That(d.Scale.x, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(d.Scale.y, Is.EqualTo(1f).Within(1e-4f));

            // Uniform: 100 px right and 100 px up is a factor of e^(200/200).
            yield return null;
            var c0 = Screen(start);
            input.Pointer = c0;
            yield return Frames(2);
            Assert.That(c.HoveredHandle, Is.EqualTo(BuildHandle.ScaleUniform));
            input.Press();
            yield return null;
            input.Pointer = c0 + new Vector2(100, 100);
            yield return null;
            input.Release();
            yield return null;
            reg.TryGet(1, out d);
            float largest = 2f * Mathf.Exp(1f);
            float snappedLargest = BuildMath.Snap(largest, 0.25f);
            Assert.That(d.Scale.x, Is.EqualTo(snappedLargest).Within(1e-3f));
            Assert.That(d.Scale.y, Is.EqualTo(snappedLargest / 2f).Within(1e-3f));

            // Scale limit.
            c.SetLimits(new Vector3(-50, -1, -50), new Vector3(50, 40, 50), 0.01f, 3f);
            yield return null;
            size = GizmoSize(start);
            yield return null;
            input.Pointer = c0;
            yield return Frames(2);
            input.Press();
            yield return null;
            input.Pointer = c0 + new Vector2(400, 400);
            yield return null;
            input.Release();
            yield return null;
            reg.TryGet(1, out d);
            Assert.That(Mathf.Max(d.Scale.x, Mathf.Max(d.Scale.y, d.Scale.z)), Is.LessThanOrEqualTo(3f + 1e-4f));
        }

        // ------------------------------------------------------------------ permissions and suppression

        [UnityTest]
        public IEnumerator CanEditFalseAllowsSelectionButBlocksEdits()
        {
            var start = new Vector3(0, 0.5f, 0);
            reg.Upsert(Box(1, start));
            c.CanEdit = d => false;
            c.Tool = BuildTool.Move;
            yield return Click(start + new Vector3(0, 0, -0.5f));
            Assert.That(c.SelectedId, Is.EqualTo(1UL));
            Assert.That(c.SelectionEditable, Is.False);
            Assert.That(c.GizmoVisible, Is.False);

            float size = GizmoSize(start);
            yield return Drag(t => start + Vector3.right * (0.6f * size + 2f * t), 0.2f);
            Assert.That(server.Updates, Is.Empty);
            Assert.That(reg.IsLocallyControlled(1), Is.False);

            c.Select(1);
            input.Tap(BuildKeys.Delete);
            yield return null;
            Assert.That(server.Deletes, Is.Empty);
            Assert.That(server.Refusals.Count, Is.EqualTo(1));
            c.CurrentColor = Color.red;
            c.ApplyColorToSelection();
            Assert.That(server.Updates, Is.Empty);
        }

        [UnityTest]
        public IEnumerator PointerOverUIAndTextFocusSuppressInput()
        {
            reg.Upsert(Box(1, new Vector3(0, 0.5f, 0)));
            c.Tool = BuildTool.Select;
            bool overUI = true, typing = true;
            c.IsPointerOverUI = () => overUI;
            c.IsTextInputFocused = () => typing;

            yield return Click(new Vector3(0, 0.5f, -0.5f));
            Assert.That(c.SelectedId, Is.Null);
            input.Tap(BuildKeys.Digit3);
            yield return null;
            Assert.That(c.Tool, Is.EqualTo(BuildTool.Select));

            c.Tool = BuildTool.Place;
            yield return Frames(2);
            Assert.That(c.PreviewVisible, Is.False);
            yield return Click(new Vector3(1, 0, 1));
            Assert.That(server.Creates, Is.Empty);

            overUI = false;
            typing = false;
            c.Tool = BuildTool.Select;
            yield return Click(new Vector3(0, 0.5f, -0.5f));
            Assert.That(c.SelectedId, Is.EqualTo(1UL));
            input.Tap(BuildKeys.Digit3);
            yield return null;
            Assert.That(c.Tool, Is.EqualTo(BuildTool.Move));
        }

        [UnityTest]
        public IEnumerator DisabledHidesEverythingAndIgnoresInput()
        {
            reg.Upsert(Box(1, new Vector3(0, 0.5f, 0)));
            c.Tool = BuildTool.Move;
            c.Select(1);
            yield return null;
            Assert.That(c.GizmoVisible, Is.True);
            c.Enabled = false;
            yield return null;
            Assert.That(c.GizmoVisible, Is.False);
            Assert.That(c.SelectedId, Is.Null);
            Assert.That(reg.IsHighlighted(1), Is.False);
            yield return Click(new Vector3(0, 0.5f, -0.5f));
            Assert.That(c.SelectedId, Is.Null);
            input.Tap(BuildKeys.Digit1);
            yield return null;
            Assert.That(c.Tool, Is.EqualTo(BuildTool.Move));
        }

        [UnityTest]
        public IEnumerator KeysSwitchToolsAndSnap()
        {
            var tools = new List<BuildTool>();
            c.ToolChanged += t => tools.Add(t);
            bool? snapSeen = null;
            c.SnapChanged += s => snapSeen = s;
            input.Tap(BuildKeys.Digit1); yield return null;
            input.Tap(BuildKeys.Digit3); yield return null;
            input.Tap(BuildKeys.Digit4); yield return null;
            input.Tap(BuildKeys.Digit5); yield return null;
            input.Tap(BuildKeys.Digit2); yield return null;
            Assert.That(tools, Is.EqualTo(new[] { BuildTool.Place, BuildTool.Move, BuildTool.Rotate, BuildTool.Scale, BuildTool.Select }));
            bool snap = c.SnapEnabled;
            input.Tap(BuildKeys.G); yield return null;
            Assert.That(c.SnapEnabled, Is.EqualTo(!snap));
            Assert.That(snapSeen, Is.EqualTo(!snap));
            // Ctrl+digit does not switch tools.
            input.Ctrl = true;
            input.Tap(BuildKeys.Digit1); yield return null;
            Assert.That(c.Tool, Is.EqualTo(BuildTool.Select));
            Assert.That(c.IsChordModifierHeld, Is.True);
            input.Ctrl = false;
            yield return null;
            Assert.That(c.IsChordModifierHeld, Is.False);
        }

        // ------------------------------------------------------------------ duplicate, delete, undo

        [UnityTest]
        public IEnumerator DuplicateOffsetsAndSelectsTheCopy()
        {
            var b = Box(1, new Vector3(0, 0.5f, 0));
            b.Name = "pillar";
            b.Color = Color.green;
            reg.Upsert(b);
            c.MoveSnap = 0.5f;
            c.Select(1);
            input.Ctrl = true;
            input.Tap(BuildKeys.D);
            yield return null;
            input.Ctrl = false;
            Assert.That(server.Creates.Count, Is.EqualTo(1));
            var d = server.Creates[0].data;
            Assert.That(d.Id, Is.EqualTo(0UL));
            Assert.That(d.Name, Is.EqualTo("pillar"));
            Assert.That(d.Color, Is.EqualTo(Color.green));
            Assert.That(d.Position, Is.EqualTo(new Vector3(0.5f, 0.5f, 0)));
            Assert.That(c.SelectedId, Is.EqualTo(server.NextId - 1));
        }

        [UnityTest]
        public IEnumerator DeleteKeyDeletesAndClearsSelection()
        {
            reg.Upsert(Box(1, new Vector3(0, 0.5f, 0)));
            c.Select(1);
            input.Tap(BuildKeys.Backspace);
            yield return null;
            Assert.That(server.Deletes, Is.EqualTo(new[] { 1UL }));
            Assert.That(reg.Contains(1), Is.False);
            Assert.That(c.SelectedId, Is.Null);
            Assert.That(c.CanUndo, Is.True);
        }

        [UnityTest]
        public IEnumerator UndoRedoAcrossCreateMoveAndDelete()
        {
            var undoStates = new List<(bool, bool)>();
            c.UndoStateChanged += (u, r) => undoStates.Add((u, r));

            // Create.
            c.Tool = BuildTool.Place;
            c.SnapEnabled = true;
            yield return Click(new Vector3(1, 0, 1));
            ulong id = server.NextId - 1;
            Assert.That(reg.Contains(id), Is.True);
            reg.TryGet(id, out var created);

            // Move with the gizmo.
            c.Tool = BuildTool.Move;
            yield return null;
            float size = GizmoSize(created.Position);
            yield return Drag(t => created.Position + Vector3.right * (0.6f * size + 2f * t), 0.3f);
            reg.TryGet(id, out var moved);
            Assert.That(moved.Position.x, Is.EqualTo(created.Position.x + 2f).Within(1e-4f));

            // Delete.
            input.Tap(BuildKeys.Delete);
            yield return null;
            Assert.That(reg.Contains(id), Is.False);
            Assert.That(c.History.UndoCount, Is.EqualTo(3));

            // Undo delete: recreated with a new id at the moved position.
            input.Ctrl = true;
            input.Tap(BuildKeys.Z);
            yield return null;
            ulong id2 = server.NextId - 1;
            Assert.That(id2, Is.Not.EqualTo(id));
            reg.TryGet(id2, out var back);
            Assert.That(back.Position, Is.EqualTo(moved.Position));

            // Undo move.
            input.Tap(BuildKeys.Z);
            yield return null;
            reg.TryGet(id2, out back);
            Assert.That(back.Position, Is.EqualTo(created.Position));

            // Undo create.
            input.Tap(BuildKeys.Z);
            yield return null;
            Assert.That(reg.Contains(id2), Is.False);
            Assert.That(c.CanUndo, Is.False);
            Assert.That(c.CanRedo, Is.True);

            // Redo create (Ctrl+Y), move (Ctrl+Shift+Z), delete (Ctrl+Y).
            input.Tap(BuildKeys.Y);
            yield return null;
            ulong id3 = server.NextId - 1;
            Assert.That(reg.Contains(id3), Is.True);
            input.Shift = true;
            input.Tap(BuildKeys.Z);
            yield return null;
            input.Shift = false;
            reg.TryGet(id3, out var again);
            Assert.That(again.Position, Is.EqualTo(moved.Position));
            input.Tap(BuildKeys.Y);
            yield return null;
            input.Ctrl = false;
            Assert.That(reg.Contains(id3), Is.False);
            Assert.That(c.CanRedo, Is.False);
            Assert.That(undoStates[undoStates.Count - 1], Is.EqualTo((true, false)));
        }

        [UnityTest]
        public IEnumerator UndoDroppedWhenSomeoneElseChangedIt()
        {
            reg.Upsert(Box(1, new Vector3(0, 0.5f, 0)));
            c.Select(1);
            c.CurrentColor = Color.red;
            c.ApplyColorToSelection();
            reg.TryGet(1, out var d);
            Assert.That(d.Color, Is.EqualTo(Color.red));
            // Someone else moves it.
            var other = d;
            other.Position = new Vector3(4, 0.5f, 0);
            reg.Upsert(other);
            int updates = server.Updates.Count;
            c.Undo();
            yield return null;
            Assert.That(server.Updates.Count, Is.EqualTo(updates));
            Assert.That(server.Refusals.Count, Is.EqualTo(1));
            Assert.That(c.CanUndo, Is.False);
        }

        [UnityTest]
        public IEnumerator InspectorEditsValidateApplyAndUndo()
        {
            reg.Upsert(Box(1, new Vector3(0, 0.5f, 0)));
            c.Select(1);
            reg.TryGet(1, out var d);

            var e = d;
            e.Position = new Vector3(0, 0.5f, 999); // out of bounds
            c.SetSelectedData(e);
            Assert.That(server.Updates, Is.Empty);
            Assert.That(server.Refusals.Count, Is.EqualTo(1));

            e = d;
            e.Scale = new Vector3(1, 100, 1);
            c.SetSelectedData(e);
            Assert.That(server.Updates, Is.Empty);
            Assert.That(server.Refusals.Count, Is.EqualTo(2));

            e = d;
            e.Name = "gate";
            e.Locked = true;
            e.Position = new Vector3(1, 2, 3);
            c.SetSelectedData(e);
            Assert.That(server.Updates.Count, Is.EqualTo(1));
            reg.TryGet(1, out var now);
            Assert.That(now.Name, Is.EqualTo("gate"));
            Assert.That(now.Position, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(reg.GetGameObject(1).transform.position, Is.EqualTo(new Vector3(1, 2, 3)));

            c.Undo();
            yield return null;
            reg.TryGet(1, out now);
            Assert.That(now.Name, Is.Null.Or.Empty);
            Assert.That(now.Locked, Is.False);
            Assert.That(now.Position, Is.EqualTo(new Vector3(0, 0.5f, 0)));
            c.Redo();
            reg.TryGet(1, out now);
            Assert.That(now.Name, Is.EqualTo("gate"));
        }

        [UnityTest]
        public IEnumerator RefusedEditEasesBackAndForgetsUndo()
        {
            reg.Upsert(Box(1, new Vector3(0, 0.5f, 0)));
            server.EchoUpdates = false;
            c.Select(1);
            reg.TryGet(1, out var d);
            var e = d;
            e.Position = new Vector3(2, 0.5f, 0);
            c.SetSelectedData(e);
            Assert.That(reg.IsLocallyControlled(1), Is.True);
            Assert.That(c.CanUndo, Is.True);
            c.NotifyRefused(1, "locked");
            Assert.That(reg.IsLocallyControlled(1), Is.False);
            Assert.That(c.CanUndo, Is.False);
            for (int i = 0; i < 30 && reg.SmoothingCount > 0; i++)
            {
                reg.Tick(0.02f);
                yield return null;
            }
            Assert.That(reg.GetGameObject(1).transform.position, Is.EqualTo(new Vector3(0, 0.5f, 0)));
        }

        [UnityTest]
        public IEnumerator CreateFailureAndLateSelection()
        {
            server.AutoCreate = false;
            c.Tool = BuildTool.Place;
            yield return Click(new Vector3(1, 0, 1));
            Assert.That(server.Creates.Count, Is.EqualTo(1));
            c.NotifyCreateFailed(server.Creates[0].token, "rate limited");
            Assert.That(c.CanUndo, Is.False);

            // Notified before the registry has it: selected when it appears.
            yield return Click(new Vector3(2, 0, 1));
            var (data, token) = server.Creates[1];
            c.NotifyCreated(token, 77);
            Assert.That(c.SelectedId, Is.Null);
            data.Id = 77;
            reg.Upsert(data);
            Assert.That(c.SelectedId, Is.EqualTo(77UL));
            Assert.That(c.CanUndo, Is.True);
        }

        [UnityTest]
        public IEnumerator PlaceInFrontOfAvatar()
        {
            c.AvatarPosition = () => new Vector3(0, 0, 0);
            c.AvatarForward = () => Vector3.forward;
            c.SnapEnabled = false;
            int token = c.PlaceInFrontOfAvatar();
            yield return null;
            Assert.That(token, Is.Not.EqualTo(0));
            var d = server.Creates[0].data;
            Assert.That(d.Position.z, Is.GreaterThan(1.5f));
            Assert.That(d.Position.y, Is.EqualTo(0.5f + BuildMath.RestGap).Within(1e-3f));
        }
    }
}
