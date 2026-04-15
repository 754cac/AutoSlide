using System.Collections.Generic;
using System.Linq;

namespace PowerPointSharing
{
    /// <summary>
    /// Per-slide ink annotation cache for SignalR late-joiner sync. Thread-safe.
    /// </summary>
    public class SlideStateStore
    {
        private readonly Dictionary<int, List<InkStrokeData>> _annotations
            = new Dictionary<int, List<InkStrokeData>>();

        public void AddStroke(int slideIndex, InkStrokeData stroke)
        {
            lock (_annotations)
            {
                if (!_annotations.TryGetValue(slideIndex, out var list))
                {
                    list = new List<InkStrokeData>();
                    _annotations[slideIndex] = list;
                }
                list.Add(stroke);
            }
        }

        public void ReplaceStrokes(int slideIndex, List<InkStrokeData> strokes)
        {
            lock (_annotations)
            {
                _annotations[slideIndex] = strokes != null
                    ? new List<InkStrokeData>(strokes)
                    : new List<InkStrokeData>();
            }
        }

        public void ClearSlide(int slideIndex)
        {
            lock (_annotations)
            {
                if (_annotations.TryGetValue(slideIndex, out var list))
                    list.Clear();
            }
        }

        public void ClearAll()
        {
            lock (_annotations) { _annotations.Clear(); }
        }

        public List<InkStrokeData> GetStrokesForSlide(int frameIndex)
        {
            lock (_annotations)
            {
                if (_annotations.TryGetValue(frameIndex, out var strokes))
                {
                    // Return a brand new list so background tasks can't be corrupted by new ink additions
                    return new List<InkStrokeData>(strokes);
                }
                return new List<InkStrokeData>();
            }
        }

        /// <summary>
        /// Returns a snapshot of all slides' ink data for late-joiner sync.
        /// </summary>
        public Dictionary<int, List<InkStrokeData>> GetAllSnapshot()
        {
            lock (_annotations)
            {
                return _annotations.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new List<InkStrokeData>(kvp.Value));
            }
        }
    }
}
