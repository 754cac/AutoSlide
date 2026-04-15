using System.Collections.Generic;

namespace PowerPointSharing
{
    /// <summary>
    /// Per-slide overlay stroke storage for backtrack restoration (local only, not broadcast).
    /// </summary>
    public class FrameStore
    {
        private readonly object _syncRoot = new object();

        private readonly Dictionary<int, List<OverlayStroke>> _strokesBySlide
            = new Dictionary<int, List<OverlayStroke>>();

        public void Save(int slideIndex, List<OverlayStroke> strokes)
        {
            lock (_syncRoot)
            {
                _strokesBySlide[slideIndex] = strokes;
            }
        }

        public bool TryGet(int slideIndex, out List<OverlayStroke> strokes)
        {
            lock (_syncRoot)
            {
                return _strokesBySlide.TryGetValue(slideIndex, out strokes);
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _strokesBySlide.Clear();
            }
        }
    }
}
