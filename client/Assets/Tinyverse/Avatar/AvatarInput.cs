using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// What the local avatar reads each frame. The default is <see cref="KeyboardAvatarInput"/>; tests and scripted
    /// movement use <see cref="ScriptedAvatarInput"/>.
    /// </summary>
    public interface ILocalAvatarInput
    {
        /// <summary>x = right, y = forward, length at most 1.</summary>
        Vector2 Move { get; }

        /// <summary>Run on the ground; full speed while flying.</summary>
        bool Run { get; }

        /// <summary>Held: rise while flying.</summary>
        bool Up { get; }

        /// <summary>Held: descend while flying.</summary>
        bool Down { get; }

        /// <summary>True once per press of the jump key. Called once per frame.</summary>
        bool ConsumeJump();

        /// <summary>True once per press of the fly toggle. Called once per frame.</summary>
        bool ConsumeToggleFly();
    }

    /// <summary>
    /// Keyboard controls built in code with the Input System: WASD or arrows move, Shift runs, F toggles flying,
    /// Space jumps or rises, Ctrl or C descends.
    /// </summary>
    public sealed class KeyboardAvatarInput : ILocalAvatarInput, IDisposable
    {
        private readonly InputAction move;
        private readonly InputAction run;
        private readonly InputAction up;
        private readonly InputAction down;
        private readonly InputAction toggleFly;

        public KeyboardAvatarInput()
        {
            move = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector2");
            move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");

            run = Button("Run", "<Keyboard>/leftShift", "<Keyboard>/rightShift");
            up = Button("Up", "<Keyboard>/space");
            down = Button("Down", "<Keyboard>/leftCtrl", "<Keyboard>/rightCtrl", "<Keyboard>/c");
            toggleFly = Button("ToggleFly", "<Keyboard>/f");

            move.Enable();
            run.Enable();
            up.Enable();
            down.Enable();
            toggleFly.Enable();
        }

        private static InputAction Button(string name, params string[] paths)
        {
            var action = new InputAction(name, InputActionType.Button);
            foreach (var p in paths) action.AddBinding(p);
            return action;
        }

        public Vector2 Move => Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f);
        public bool Run => run.IsPressed();
        public bool Up => up.IsPressed();
        public bool Down => down.IsPressed();
        public bool ConsumeJump() => up.WasPressedThisFrame();
        public bool ConsumeToggleFly() => toggleFly.WasPressedThisFrame();

        public void Dispose()
        {
            move.Dispose();
            run.Dispose();
            up.Dispose();
            down.Dispose();
            toggleFly.Dispose();
        }
    }

    /// <summary>Input set from code. Jump and fly toggle are one-shot requests consumed by the next frame.</summary>
    public sealed class ScriptedAvatarInput : ILocalAvatarInput
    {
        private bool jumpRequested;
        private bool toggleFlyRequested;

        public Vector2 Move { get; set; }
        public bool Run { get; set; }
        public bool Up { get; set; }
        public bool Down { get; set; }

        public void Jump() => jumpRequested = true;
        public void ToggleFly() => toggleFlyRequested = true;

        public bool ConsumeJump()
        {
            bool r = jumpRequested;
            jumpRequested = false;
            return r;
        }

        public bool ConsumeToggleFly()
        {
            bool r = toggleFlyRequested;
            toggleFlyRequested = false;
            return r;
        }

        public void Reset()
        {
            Move = Vector2.zero;
            Run = Up = Down = false;
            jumpRequested = toggleFlyRequested = false;
        }
    }
}
