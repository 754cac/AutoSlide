using System;
using System.Collections.Generic;

namespace PowerPointSharing
{
    internal abstract class SessionEvent
    {
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    }

    internal sealed class FrameAdvancedSessionEvent : SessionEvent
    {
        public FrameAdvancedSessionEvent(int slideIndex, int clickIndex, bool isBackfill)
        {
            SlideIndex = slideIndex;
            ClickIndex = clickIndex;
            IsBackfill = isBackfill;
        }

        public int SlideIndex { get; }
        public int ClickIndex { get; }
        public bool IsBackfill { get; }
    }

    internal sealed class SlideChangedSessionEvent : SessionEvent
    {
        public SlideChangedSessionEvent(int previousSlideIndex, int newSlideIndex, int totalSlides, bool endSessionOnLastSlide)
        {
            PreviousSlideIndex = previousSlideIndex;
            NewSlideIndex = newSlideIndex;
            TotalSlides = totalSlides;
            EndSessionOnLastSlide = endSessionOnLastSlide;
        }

        public int PreviousSlideIndex { get; }
        public int NewSlideIndex { get; }
        public int TotalSlides { get; }
        public bool EndSessionOnLastSlide { get; }
    }

    internal abstract class SessionNetworkAction
    {
    }

    internal sealed class UnlockFrameAction : SessionNetworkAction
    {
        public UnlockFrameAction(int frameIndex, bool isBackfill)
        {
            FrameIndex = frameIndex;
            IsBackfill = isBackfill;
        }

        public int FrameIndex { get; }
        public bool IsBackfill { get; }
    }

    internal sealed class AdvanceFrameAction : SessionNetworkAction
    {
        public AdvanceFrameAction(int frameIndex, bool delayForBackfill)
        {
            FrameIndex = frameIndex;
            DelayForBackfill = delayForBackfill;
        }

        public int FrameIndex { get; }
        public bool DelayForBackfill { get; }
    }

    internal sealed class EndSessionAction : SessionNetworkAction
    {
    }

    internal sealed class FrameAdvanceDecision
    {
        public int PreviousSlideIndex { get; set; }
        public int PreviousAbsoluteFrame { get; set; }
        public int SlideIndex { get; set; }
        public int RequestedClickIndex { get; set; }
        public int ResolvedClickIndex { get; set; }
        public int ContentFrameIndex { get; set; }
        public int ExportFrameIndex { get; set; }
        public bool IsBackfill { get; set; }
        public bool IsBackwardNavigation { get; set; }
        public bool PhysicalSlideChanged { get; set; }
    }

    internal sealed class SessionReductionResult
    {
        public List<SessionNetworkAction> NetworkActions { get; } = new List<SessionNetworkAction>();
        public FrameAdvanceDecision? FrameDecision { get; set; }
        public bool ResetBroadcastDedup { get; set; }
    }

    internal sealed class SessionReducer
    {
        public SessionReductionResult Reduce(SessionState state, DeckIndex deckIndex, SessionEvent sessionEvent)
        {
            if (sessionEvent is FrameAdvancedSessionEvent frameEvent)
                return ReduceFrameAdvanced(state, deckIndex, frameEvent);

            if (sessionEvent is SlideChangedSessionEvent slideEvent)
                return ReduceSlideChanged(state, deckIndex, slideEvent);

            return new SessionReductionResult();
        }

        private static SessionReductionResult ReduceFrameAdvanced(
            SessionState state,
            DeckIndex deckIndex,
            FrameAdvancedSessionEvent frameEvent)
        {
            var result = new SessionReductionResult();

            var previousSlideIndex = state.PresenterCursor.CurrentSlideIndex;
            var previousAbsoluteFrame = state.PresenterCursor.CurrentAbsoluteFrame;
            var hasPreviousClick = state.PresenterCursor.LastEmittedClickBySlide.TryGetValue(
                frameEvent.SlideIndex,
                out var previousClickForSlide);

            var contentFrameIndex = deckIndex.ResolveContentFrameForSlideClick(frameEvent.SlideIndex, frameEvent.ClickIndex);
            var exportFrameIndex = deckIndex.ResolveExportFrame(contentFrameIndex);
            var resolvedClickIndex = deckIndex.ResolveClickIndex(frameEvent.SlideIndex, contentFrameIndex, frameEvent.ClickIndex);

            var isBackwardNavigation = previousAbsoluteFrame > 0 && exportFrameIndex < previousAbsoluteFrame;
            var physicalSlideChanged = previousAbsoluteFrame > 0 && previousSlideIndex != frameEvent.SlideIndex;
            var isDuplicateFrameEvent =
                previousAbsoluteFrame == exportFrameIndex
                && previousSlideIndex == frameEvent.SlideIndex
                && hasPreviousClick
                && previousClickForSlide == resolvedClickIndex;

            state.PresenterCursor.CurrentSlideIndex = frameEvent.SlideIndex;
            state.PresenterCursor.CurrentAbsoluteFrame = exportFrameIndex;
            state.PresenterCursor.LastEmittedClickBySlide[frameEvent.SlideIndex] = resolvedClickIndex;
            state.UnlockedFrames.Add(exportFrameIndex);

            result.FrameDecision = new FrameAdvanceDecision
            {
                PreviousSlideIndex = previousSlideIndex,
                PreviousAbsoluteFrame = previousAbsoluteFrame,
                SlideIndex = frameEvent.SlideIndex,
                RequestedClickIndex = frameEvent.ClickIndex,
                ResolvedClickIndex = resolvedClickIndex,
                ContentFrameIndex = contentFrameIndex,
                ExportFrameIndex = exportFrameIndex,
                IsBackfill = frameEvent.IsBackfill,
                IsBackwardNavigation = isBackwardNavigation,
                PhysicalSlideChanged = physicalSlideChanged
            };

            if (!isDuplicateFrameEvent)
            {
                result.NetworkActions.Add(new UnlockFrameAction(exportFrameIndex, frameEvent.IsBackfill));
                if (!frameEvent.IsBackfill && !isBackwardNavigation)
                {
                    result.NetworkActions.Add(new AdvanceFrameAction(exportFrameIndex, delayForBackfill: false));
                }
            }

            return result;
        }

        private static SessionReductionResult ReduceSlideChanged(
            SessionState state,
            DeckIndex deckIndex,
            SlideChangedSessionEvent slideEvent)
        {
            var result = new SessionReductionResult();
            // Strict progressive mode: do not auto-backfill skipped animation clicks
            // when the presenter moves to a different physical slide.
            result.ResetBroadcastDedup = false;

            if (slideEvent.EndSessionOnLastSlide
                && slideEvent.TotalSlides > 0
                && slideEvent.NewSlideIndex >= slideEvent.TotalSlides)
            {
                result.NetworkActions.Add(new EndSessionAction());
            }

            return result;
        }
    }
}
