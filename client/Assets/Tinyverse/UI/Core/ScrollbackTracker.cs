namespace Tinyverse.UI
{
    /// <summary>
    /// Decides whether a scrolling log follows new lines. It sticks to the bottom until the user
    /// scrolls up, counts the lines that arrive meanwhile, and sticks again once the user scrolls
    /// back down to the end. Pure, so it is tested without layout.
    /// </summary>
    public sealed class ScrollbackTracker
    {
        /// <summary>How close to the end, in pixels, still counts as "at the bottom".</summary>
        public const float BottomTolerance = 4f;

        public bool StickToBottom { get; private set; } = true;
        public int UnseenCount { get; private set; }

        /// <summary>Call when the scroll offset changes. <paramref name="maxOffset"/> is the largest offset.</summary>
        public void OnScrolled(float offset, float maxOffset)
        {
            StickToBottom = maxOffset <= 0f || offset >= maxOffset - BottomTolerance;
            if (StickToBottom) UnseenCount = 0;
        }

        /// <summary>Call when a line is appended. Returns true if the view should scroll to the end.</summary>
        public bool OnLineAdded()
        {
            if (StickToBottom) return true;
            UnseenCount++;
            return false;
        }

        /// <summary>Call after a programmatic jump to the end.</summary>
        public void OnJumpedToBottom()
        {
            StickToBottom = true;
            UnseenCount = 0;
        }
    }
}
