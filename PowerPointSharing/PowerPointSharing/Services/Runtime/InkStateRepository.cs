using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace PowerPointSharing
{
    internal sealed class InkStateRepository
    {
        private readonly SlideStateStore _slideStateStore;
        private readonly ConcurrentDictionary<int, List<InkStrokeData>> _frameStrokeCache;

        public InkStateRepository(
            SlideStateStore slideStateStore,
            ConcurrentDictionary<int, List<InkStrokeData>> frameStrokeCache)
        {
            _slideStateStore = slideStateStore;
            _frameStrokeCache = frameStrokeCache;
        }

        public void AddStroke(int slideIndex, InkStrokeData stroke)
        {
            _slideStateStore.AddStroke(slideIndex, stroke);
        }

        public void ReplaceSlideStrokes(int slideIndex, List<InkStrokeData> strokes)
        {
            _slideStateStore.ReplaceStrokes(slideIndex, CloneStrokes(strokes));
        }

        public void ClearSlide(int slideIndex)
        {
            _slideStateStore.ClearSlide(slideIndex);
        }

        public void SetFrameStrokes(int frameIndex, List<InkStrokeData> strokes)
        {
            _frameStrokeCache[frameIndex] = CloneStrokes(strokes);
        }

        public bool TryGetFrameStrokes(int frameIndex, out List<InkStrokeData> strokes)
        {
            if (_frameStrokeCache.TryGetValue(frameIndex, out var stored))
            {
                strokes = CloneStrokes(stored);
                return true;
            }

            strokes = new List<InkStrokeData>();
            return false;
        }

        public List<InkStrokeData> GetStrokesForSlide(int slideIndex)
        {
            return _slideStateStore.GetStrokesForSlide(slideIndex);
        }

        public Dictionary<int, List<InkStrokeData>> GetAllSlidesSnapshot()
        {
            return _slideStateStore.GetAllSnapshot();
        }

        public Dictionary<int, List<InkStrokeData>> GetAllFrameSnapshot()
        {
            return _frameStrokeCache.ToDictionary(
                kvp => kvp.Key,
                kvp => CloneStrokes(kvp.Value));
        }

        public void ClearAll()
        {
            _slideStateStore.ClearAll();
            _frameStrokeCache.Clear();
        }

        private static List<InkStrokeData> CloneStrokes(List<InkStrokeData> strokes)
        {
            return strokes != null ? new List<InkStrokeData>(strokes) : new List<InkStrokeData>();
        }
    }
}
