using System.Collections.Generic;

namespace PowerPointSharing
{
    internal static class LateJoinerInkStateBuilder
    {
        public static Dictionary<int, List<InkStrokeData>> BuildAbsoluteFrameMap(
            InkStateRepository inkStateRepository,
            DeckIndex deckIndex)
        {
            var absoluteFrameMap = inkStateRepository.GetAllFrameSnapshot();
            if (absoluteFrameMap.Count > 0)
                return absoluteFrameMap;

            var allSlides = inkStateRepository.GetAllSlidesSnapshot();
            foreach (var kvp in allSlides)
            {
                int slideIndex = kvp.Key;
                var strokes = kvp.Value;

                var frames = deckIndex != null
                    ? deckIndex.GetExportFramesForSlide(slideIndex)
                    : new List<int> { slideIndex };

                foreach (var frameIndex in frames)
                    absoluteFrameMap[frameIndex] = new List<InkStrokeData>(strokes);
            }

            return absoluteFrameMap;
        }
    }
}
