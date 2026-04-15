using System.Collections.Concurrent;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace PowerPointSharing
{
    public class GoldenTraceReducerTests
    {
        [Fact]
        public void NormalForwardProgression_Matches_ExpectedTrace()
        {
            var reducer = new SessionReducer();
            var state = BuildSessionState();
            var deckIndex = BuildSampleDeckIndex();

            var r1 = reducer.Reduce(state, deckIndex, new FrameAdvancedSessionEvent(1, 0, isBackfill: false));
            var r2 = reducer.Reduce(state, deckIndex, new FrameAdvancedSessionEvent(1, 1, isBackfill: false));
            var r3 = reducer.Reduce(state, deckIndex, new FrameAdvancedSessionEvent(2, 0, isBackfill: false));

            AssertUnlockAdvance(r1, expectedFrame: 1);
            AssertUnlockAdvance(r2, expectedFrame: 2);
            AssertUnlockAdvance(r3, expectedFrame: 3);

            state.PresenterCursor.CurrentAbsoluteFrame.Should().Be(3);
            state.PresenterCursor.CurrentSlideIndex.Should().Be(2);
        }

        [Fact]
        public void BackwardNavigation_DoesNotAdvanceWatermark()
        {
            var reducer = new SessionReducer();
            var state = BuildSessionState();
            var deckIndex = BuildSampleDeckIndex();

            reducer.Reduce(state, deckIndex, new FrameAdvancedSessionEvent(1, 0, false));
            reducer.Reduce(state, deckIndex, new FrameAdvancedSessionEvent(1, 1, false));
            reducer.Reduce(state, deckIndex, new FrameAdvancedSessionEvent(2, 0, false));

            var back = reducer.Reduce(state, deckIndex, new FrameAdvancedSessionEvent(1, 0, false));

            back.FrameDecision.Should().NotBeNull();
            back.FrameDecision.IsBackwardNavigation.Should().BeTrue();
            back.NetworkActions.Should().HaveCount(1);
            back.NetworkActions[0].Should().BeOfType<UnlockFrameAction>().Which.FrameIndex.Should().Be(1);
        }

        [Fact]
        public void JumpForwardAcrossUnseenMaterial_SupportsNonPrefixUnlockSet()
        {
            var reducer = new SessionReducer();
            var state = BuildSessionState();
            var deckIndex = BuildSampleDeckIndex();

            var jump = reducer.Reduce(state, deckIndex, new FrameAdvancedSessionEvent(2, 1, false));

            AssertUnlockAdvance(jump, expectedFrame: 4);
            state.UnlockedFrames.Contains(4).Should().BeTrue();
            state.UnlockedFrames.Contains(1).Should().BeFalse();
            state.UnlockedFrames.Contains(2).Should().BeFalse();
            state.UnlockedFrames.Contains(3).Should().BeFalse();
        }

        [Fact]
        public void LateJoinerInkSync_MapsSlideStateToAllExportFrames()
        {
            var deckIndex = BuildSampleDeckIndex();
            var repository = new InkStateRepository(new SlideStateStore(), new ConcurrentDictionary<int, List<InkStrokeData>>());

            repository.ReplaceSlideStrokes(1, new List<InkStrokeData>
            {
                new InkStrokeData
                {
                    StrokeId = "s-1",
                    Points = new List<double[]> { new[] { 0.1, 0.2 } },
                    Color = "#ff0000",
                    Width = 2,
                    Opacity = 1
                }
            });

            var map = LateJoinerInkStateBuilder.BuildAbsoluteFrameMap(repository, deckIndex);

            map.Keys.Should().Contain(1);
            map.Keys.Should().Contain(2);
            map[1].Should().HaveCount(1);
            map[2].Should().HaveCount(1);
            map[1][0].StrokeId.Should().Be("s-1");
            map[2][0].StrokeId.Should().Be("s-1");
        }

        [Fact]
        public void LeavingPartiallyRevealedSlide_BackfillsMissingClicksWithoutReplayMutation()
        {
            var reducer = new SessionReducer();
            var state = BuildSessionState();
            var deckIndex = BuildSampleDeckIndex();

            reducer.Reduce(state, deckIndex, new FrameAdvancedSessionEvent(1, 0, false));

            var slideChanged = reducer.Reduce(
                state,
                deckIndex,
                new SlideChangedSessionEvent(previousSlideIndex: 1, newSlideIndex: 2, totalSlides: 2, endSessionOnLastSlide: false));

            slideChanged.ResetBroadcastDedup.Should().BeTrue();
            slideChanged.NetworkActions.Should().HaveCount(2);
            slideChanged.NetworkActions[0].Should().BeOfType<UnlockFrameAction>().Which.FrameIndex.Should().Be(2);
            slideChanged.NetworkActions[1].Should().BeOfType<AdvanceFrameAction>().Which.FrameIndex.Should().Be(2);
            slideChanged.NetworkActions[1].Should().BeOfType<AdvanceFrameAction>().Which.DelayForBackfill.Should().BeTrue();

            var next = reducer.Reduce(state, deckIndex, new FrameAdvancedSessionEvent(2, 0, false));
            AssertUnlockAdvance(next, expectedFrame: 3);
        }

        private static SessionState BuildSessionState()
        {
            var repository = new InkStateRepository(new SlideStateStore(), new ConcurrentDictionary<int, List<InkStrokeData>>());
            return new SessionState(repository);
        }

        private static DeckIndex BuildSampleDeckIndex()
        {
            var builder = new DeckIndexBuilder();
            return builder.Build(
                new[]
                {
                    new DeckFrameDescriptor { FrameIndex = 1, ExportFrameIndex = 1, OriginalSlideIndex = 1, ClickIndex = 0, IsBoundary = false },
                    new DeckFrameDescriptor { FrameIndex = 2, ExportFrameIndex = 2, OriginalSlideIndex = 1, ClickIndex = 1, IsBoundary = false },
                    new DeckFrameDescriptor { FrameIndex = 3, ExportFrameIndex = null, OriginalSlideIndex = 1, ClickIndex = null, IsBoundary = true, BoundaryAfterSlideIndex = 1 },
                    new DeckFrameDescriptor { FrameIndex = 4, ExportFrameIndex = 3, OriginalSlideIndex = 2, ClickIndex = 0, IsBoundary = false },
                    new DeckFrameDescriptor { FrameIndex = 5, ExportFrameIndex = 4, OriginalSlideIndex = 2, ClickIndex = 1, IsBoundary = false }
                },
                new Dictionary<int, List<int>>
                {
                    { 1, new List<int> { 1, 2 } },
                    { 2, new List<int> { 4, 5 } }
                });
        }

        private static void AssertUnlockAdvance(SessionReductionResult result, int expectedFrame)
        {
            result.NetworkActions.Should().HaveCount(2);
            result.NetworkActions[0].Should().BeOfType<UnlockFrameAction>().Which.FrameIndex.Should().Be(expectedFrame);
            result.NetworkActions[1].Should().BeOfType<AdvanceFrameAction>().Which.FrameIndex.Should().Be(expectedFrame);
        }
    }
}
