using System.Collections.Generic;
using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// Keeps the name tags of avatars standing close together readable: once per frame, after every tag has
    /// placed and sized itself, the tags are laid out on screen nearest first; a tag that would overlap one
    /// already placed is nudged upward until it is clear. Nudges ease in and out so tags do not jump.
    /// </summary>
    [DefaultExecutionOrder(210)]
    public sealed class NameTagLayout : MonoBehaviour
    {
        /// <summary>Vertical gap kept between tags, pixels.</summary>
        public const float GapPixels = 2f;

        private static readonly List<NameTag> tags = new List<NameTag>();
        private static NameTagLayout instance;

        private readonly List<NameTag> shown = new List<NameTag>();
        private readonly List<Rect> rects = new List<Rect>();
        private readonly List<float> depths = new List<float>();
        private readonly List<int> order = new List<int>();
        private readonly List<Rect> sortedRects = new List<Rect>();

        internal static void Register(NameTag tag)
        {
            if (!tags.Contains(tag)) tags.Add(tag);
            if (instance == null && Application.isPlaying)
            {
                var go = new GameObject("NameTagLayout") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(go);
                instance = go.AddComponent<NameTagLayout>();
            }
        }

        internal static void Unregister(NameTag tag) => tags.Remove(tag);

        /// <summary>
        /// Upward shifts (pixels) that separate screen rectangles given nearest first: each keeps clear of the
        /// ones before it, the nearest never moves. Rectangles use screen pixels with y up.
        /// </summary>
        public static float[] Resolve(IReadOnlyList<Rect> nearestFirst, float gap = GapPixels)
        {
            var shifts = new float[nearestFirst.Count];
            var placed = new List<Rect>(nearestFirst.Count);
            for (int i = 0; i < nearestFirst.Count; i++)
            {
                var r = nearestFirst[i];
                float shift = 0f;
                // Each pass moves the rectangle above one blocker; at most one pass per placed rectangle.
                for (int pass = 0; pass <= placed.Count; pass++)
                {
                    bool moved = false;
                    foreach (var p in placed)
                    {
                        var s = new Rect(r.x, r.y + shift, r.width, r.height);
                        if (s.xMin < p.xMax && s.xMax > p.xMin && s.yMin < p.yMax + gap && s.yMax > p.yMin - gap)
                        {
                            shift = p.yMax + gap - r.yMin;
                            moved = true;
                        }
                    }
                    if (!moved) break;
                }
                shifts[i] = shift;
                placed.Add(new Rect(r.x, r.y + shift, r.width, r.height));
            }
            return shifts;
        }

        private void LateUpdate()
        {
            var cam = NameTag.ViewCamera != null ? NameTag.ViewCamera : Camera.main;
            if (cam == null) return;
            shown.Clear();
            rects.Clear();
            depths.Clear();
            foreach (var tag in tags)
            {
                if (tag == null || !tag.isActiveAndEnabled) continue;
                if (!tag.IsShown || !tag.TryGetScreenRect(cam, out var rect, out float depth))
                {
                    tag.SetLayoutTarget(0f, cam);
                    continue;
                }
                shown.Add(tag);
                rects.Add(rect);
                depths.Add(depth);
            }

            order.Clear();
            for (int i = 0; i < shown.Count; i++) order.Add(i);
            order.Sort((a, b) => depths[a].CompareTo(depths[b]));
            sortedRects.Clear();
            foreach (int i in order) sortedRects.Add(rects[i]);
            var shifts = Resolve(sortedRects);
            for (int k = 0; k < order.Count; k++)
                shown[order[k]].SetLayoutTarget(shifts[k], cam);
            foreach (var tag in shown) tag.ApplyLayout(cam, Time.deltaTime);
        }
    }
}
