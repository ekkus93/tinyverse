using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>
    /// Three numeric text fields (X, Y, Z). A field commits on Enter or when it loses focus; text
    /// that does not parse reverts. Up / Down arrows nudge by <see cref="Step"/> (Shift x10).
    /// </summary>
    public sealed class Vector3Fields
    {
        readonly TextField[] _fields = new TextField[3];
        readonly Func<float, float> _clamp;
        Vector3 _value;

        /// <summary>Raised with the whole vector when the user commits a changed component.</summary>
        public event Action<Vector3> Changed;

        public Vector3 Value => _value;
        public float Step { get; set; } = 0.1f;
        public bool IsReadOnly { get; private set; }

        public VisualElement Root { get; }

        internal Vector3Fields(VisualElement root, Func<float, float> clamp = null)
        {
            Root = root;
            _clamp = clamp;
            string[] axes = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                int axis = i;
                var cell = new VisualElement();
                cell.AddToClassList("tv-vec__cell");
                var tag = UIUtil.Label(axes[i], "tv-vec__axis");
                tag.AddToClassList("tv-vec__axis--" + axes[i].ToLowerInvariant());
                cell.Add(tag);
                var f = new TextField { maxLength = 16 };
                f.AddToClassList("tv-vec__field");
                f.RegisterCallback<KeyDownEvent>(e => OnKey(e, axis), TrickleDown.TrickleDown);
                f.RegisterCallback<FocusOutEvent>(_ => Commit(axis, f.value));
                cell.Add(f);
                _fields[i] = f;
                root.Add(cell);
            }
            SetValue(Vector3.zero);
        }

        /// <summary>Shows a value without raising <see cref="Changed"/>. A field being typed in is left alone.</summary>
        public void SetValue(Vector3 v, bool evenIfEditing = false)
        {
            _value = v;
            for (int i = 0; i < 3; i++)
            {
                var f = _fields[i];
                if (!evenIfEditing && f.focusController?.focusedElement == f && !IsReadOnly) continue;
                f.SetValueWithoutNotify(Validation.FormatFloat(v[i]));
                f.RemoveFromClassList("tv-field--invalid");
            }
        }

        public void SetReadOnly(bool readOnly)
        {
            IsReadOnly = readOnly;
            foreach (var f in _fields)
            {
                f.isReadOnly = readOnly;
                f.focusable = !readOnly;
                UIUtil.SetClass(f, "tv-field--readonly", readOnly);
            }
        }

        /// <summary>Applies typed text to one component as committing the field would. Returns false if it does not parse.</summary>
        public bool Commit(int axis, string text)
        {
            if (IsReadOnly) return false;
            var f = _fields[axis];
            if (!Validation.TryParseFloat(text, out float n))
            {
                f.AddToClassList("tv-field--invalid");
                f.SetValueWithoutNotify(Validation.FormatFloat(_value[axis]));
                f.schedule.Execute(() => f.RemoveFromClassList("tv-field--invalid")).ExecuteLater(900);
                return false;
            }
            if (_clamp != null) n = _clamp(n);
            f.RemoveFromClassList("tv-field--invalid");
            f.SetValueWithoutNotify(Validation.FormatFloat(n));
            if (Mathf.Approximately(n, _value[axis])) return true;
            var v = _value;
            v[axis] = n;
            _value = v;
            Changed?.Invoke(v);
            return true;
        }

        internal TextField Field(int axis) => _fields[axis];

        void OnKey(KeyDownEvent e, int axis)
        {
            var f = _fields[axis];
            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    e.StopPropagation();
                    Commit(axis, f.value);
                    break;
                case KeyCode.Escape:
                    f.SetValueWithoutNotify(Validation.FormatFloat(_value[axis]));
                    break;
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                    if (IsReadOnly || !Validation.TryParseFloat(f.value, out float cur)) break;
                    e.StopPropagation();
                    float step = Step * (e.shiftKey ? 10f : 1f) * (e.keyCode == KeyCode.UpArrow ? 1f : -1f);
                    Commit(axis, Validation.FormatFloat(cur + step));
                    break;
            }
        }
    }
}
