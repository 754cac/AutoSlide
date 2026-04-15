using System.Collections.Generic;

namespace PowerPointSharing
{
    internal sealed class PresenterCursorState
    {
        public int CurrentSlideIndex { get; set; } = -1;
        public int CurrentAbsoluteFrame { get; set; } = -1;
        public Dictionary<int, int> LastEmittedClickBySlide { get; } = new Dictionary<int, int>();

        public void Reset()
        {
            CurrentSlideIndex = -1;
            CurrentAbsoluteFrame = -1;
            LastEmittedClickBySlide.Clear();
        }
    }

    internal sealed class SessionState
    {
        public SessionState(InkStateRepository inkState)
        {
            InkState = inkState;
        }

        public PresenterCursorState PresenterCursor { get; } = new PresenterCursorState();
        public IntervalSet UnlockedFrames { get; } = new IntervalSet();
        public InkStateRepository InkState { get; }

        public void Reset()
        {
            PresenterCursor.Reset();
            UnlockedFrames.Clear();
            InkState.ClearAll();
        }
    }
}
