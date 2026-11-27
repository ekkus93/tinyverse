using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Tinyverse.Building
{
    /// <summary>
    /// Mouse and keyboard through the Input System, with actions created in code. While the cursor is locked
    /// (the orbit camera's right-button mouse-look) the pointer is reported as absent.
    /// </summary>
    public sealed class InputSystemBuildInput : IBuildInput, IDisposable
    {
        readonly InputAction point;
        readonly InputAction primary;
        readonly InputAction ctrl;
        readonly InputAction shift;
        readonly InputAction[] keys;
        readonly BuildKeys[] keyFlags;

        public InputSystemBuildInput()
        {
            point = new InputAction("BuildPoint", InputActionType.Value, "<Pointer>/position", expectedControlType: "Vector2");
            primary = new InputAction("BuildPrimary", InputActionType.Button, "<Mouse>/leftButton");
            ctrl = Button("BuildCtrl", "<Keyboard>/ctrl");
            shift = Button("BuildShift", "<Keyboard>/shift");

            keyFlags = new[]
            {
                BuildKeys.Digit1, BuildKeys.Digit2, BuildKeys.Digit3, BuildKeys.Digit4, BuildKeys.Digit5,
                BuildKeys.G, BuildKeys.Delete, BuildKeys.Backspace, BuildKeys.D, BuildKeys.Z, BuildKeys.Y, BuildKeys.Escape,
            };
            keys = new[]
            {
                Button("Build1", "<Keyboard>/1", "<Keyboard>/numpad1"),
                Button("Build2", "<Keyboard>/2", "<Keyboard>/numpad2"),
                Button("Build3", "<Keyboard>/3", "<Keyboard>/numpad3"),
                Button("Build4", "<Keyboard>/4", "<Keyboard>/numpad4"),
                Button("Build5", "<Keyboard>/5", "<Keyboard>/numpad5"),
                Button("BuildSnap", "<Keyboard>/g"),
                Button("BuildDelete", "<Keyboard>/delete"),
                Button("BuildBackspace", "<Keyboard>/backspace"),
                Button("BuildDuplicate", "<Keyboard>/d"),
                Button("BuildUndo", "<Keyboard>/z"),
                Button("BuildRedo", "<Keyboard>/y"),
                Button("BuildEscape", "<Keyboard>/escape"),
            };

            point.Enable();
            primary.Enable();
            ctrl.Enable();
            shift.Enable();
            foreach (var k in keys) k.Enable();
        }

        static InputAction Button(string name, params string[] paths)
        {
            var a = new InputAction(name, InputActionType.Button);
            foreach (var p in paths) a.AddBinding(p);
            return a;
        }

        public BuildInputFrame Poll()
        {
            var f = new BuildInputFrame
            {
                HasPointer = Pointer.current != null && Cursor.lockState != CursorLockMode.Locked,
                Pointer = point.ReadValue<Vector2>(),
                PrimaryHeld = primary.IsPressed(),
                PrimaryPressed = primary.WasPressedThisFrame(),
                PrimaryReleased = primary.WasReleasedThisFrame(),
                Ctrl = ctrl.IsPressed(),
                Shift = shift.IsPressed(),
            };
            for (int i = 0; i < keys.Length; i++)
                if (keys[i].WasPressedThisFrame()) f.KeysPressed |= keyFlags[i];
            return f;
        }

        public void Dispose()
        {
            point.Dispose();
            primary.Dispose();
            ctrl.Dispose();
            shift.Dispose();
            foreach (var k in keys) k.Dispose();
        }
    }

    /// <summary>
    /// Input set from code, for tests and scripted building. Held state persists; presses, releases and key
    /// taps are delivered by the next <see cref="Poll"/> and then cleared.
    /// </summary>
    public sealed class ScriptedBuildInput : IBuildInput
    {
        bool pressQueued, releaseQueued;
        BuildKeys queuedKeys;

        public bool HasPointer { get; set; } = true;
        public Vector2 Pointer { get; set; }
        public bool PrimaryHeld { get; private set; }
        public bool Ctrl { get; set; }
        public bool Shift { get; set; }

        /// <summary>Presses the primary button (delivered next poll); it stays held until <see cref="Release"/>.</summary>
        public void Press()
        {
            PrimaryHeld = true;
            pressQueued = true;
        }

        public void Release()
        {
            PrimaryHeld = false;
            releaseQueued = true;
        }

        /// <summary>Taps a key: it counts as pressed in the next poll only.</summary>
        public void Tap(BuildKeys key) => queuedKeys |= key;

        public BuildInputFrame Poll()
        {
            var f = new BuildInputFrame
            {
                HasPointer = HasPointer,
                Pointer = Pointer,
                PrimaryHeld = PrimaryHeld,
                PrimaryPressed = pressQueued,
                PrimaryReleased = releaseQueued,
                Ctrl = Ctrl,
                Shift = Shift,
                KeysPressed = queuedKeys,
            };
            pressQueued = releaseQueued = false;
            queuedKeys = BuildKeys.None;
            return f;
        }
    }
}
