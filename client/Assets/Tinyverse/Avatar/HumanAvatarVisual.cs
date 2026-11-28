using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// The default avatar visual: a <see cref="HumanFigure"/> of the look's body preset, or the
    /// <see cref="PlaceholderAvatarVisual"/> figure when the preset is unknown (or its assets are missing). A
    /// change of preset swaps the figure; the name tag stays.
    /// </summary>
    public sealed class HumanAvatarVisual : MonoBehaviour, IAvatarVisual
    {
        private const float TagGap = 0.22f;

        private NameTag nameTag;
        private HumanFigure figure;
        private PlaceholderAvatarVisual placeholder;
        private AvatarLook look = AvatarLook.Default;
        private AvatarPose lastPose;
        private bool hasLook;
        private BodyPreset requestedPreset;
        private bool visible = true;

        public Transform Root => transform;
        public float Height => figure != null ? figure.Height : PlaceholderAvatarVisual.TotalHeight;
        public NameTag NameTag => nameTag;
        public bool Visible => visible;

        /// <summary>The human figure drawn, or null while the placeholder stands in.</summary>
        public HumanFigure Figure => figure;

        /// <summary>The placeholder drawn for an unknown body preset, or null.</summary>
        public PlaceholderAvatarVisual Placeholder => placeholder;

        /// <summary>The body preset drawn, or null for the placeholder.</summary>
        public BodyPreset Preset => figure != null ? figure.Preset : null;

        public static IAvatarVisual Create(Transform avatarRoot)
        {
            var go = new GameObject("Visual");
            go.transform.SetParent(avatarRoot, false);
            var visual = go.AddComponent<HumanAvatarVisual>();
            visual.nameTag = NameTag.Create(go.transform, PlaceholderAvatarVisual.TotalHeight + TagGap);
            visual.SetLook(AvatarLook.Default);
            return visual;
        }

        public void SetLook(AvatarLook newLook)
        {
            var preset = HumanCatalog.FindPreset(newLook.BodyPreset);
            bool rebuild = !hasLook || preset != requestedPreset;
            requestedPreset = preset;
            look = newLook;
            hasLook = true;
            if (rebuild) Rebuild(preset);
            if (figure != null) figure.ApplyLook(look);
            else if (placeholder != null) placeholder.SetLook(look);
            nameTag.AnchorHeight = Height + TagGap;
        }

        private void Rebuild(BodyPreset preset)
        {
            if (figure != null) DestroyNow(figure.gameObject);
            if (placeholder != null) DestroyNow(placeholder.gameObject);
            figure = null;
            placeholder = null;
            if (preset != null) figure = HumanFigure.Create(transform, preset);
            if (figure == null) placeholder = PlaceholderAvatarVisual.CreateBody(transform);
            if (figure != null) figure.SetVisible(visible);
            else placeholder.SetVisible(visible);
            UpdatePose(lastPose, 0f);
        }

        private static void DestroyNow(GameObject go)
        {
            go.SetActive(false);
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        public void SetName(string displayName) => nameTag.SetText(displayName);

        public void SetAgent(bool isAgent) => nameTag.SetAgent(isAgent);

        public void SetVisible(bool show)
        {
            visible = show;
            if (figure != null) figure.SetVisible(show);
            if (placeholder != null) placeholder.SetVisible(show);
            nameTag.SetVisible(show);
        }

        public void UpdatePose(in AvatarPose pose, float deltaTime)
        {
            lastPose = pose;
            if (figure != null)
            {
                figure.Animate(pose, deltaTime);
                // Hovering lifts the head above its standing height; the tag rises with it, never below its
                // standing place (flying forward the head is lower). Swimming, the tag comes down with the head.
                float head = figure.CurrentHeadTop;
                nameTag.AnchorHeight = head + (1f - figure.SwimShare) * Mathf.Max(0f, Height - head) + TagGap;
            }
            else if (placeholder != null) placeholder.UpdatePose(pose, deltaTime);
        }

        public void Destroy()
        {
            if (this != null && gameObject != null) Object.Destroy(gameObject);
        }
    }
}
