using System.Collections.Generic;
using System.Linq;

namespace PowerPointSharing
{
    internal sealed class DeckIndexBuilder
    {
        public DeckIndex Build(
            IEnumerable<DeckFrameDescriptor> descriptors,
            Dictionary<int, List<int>>? slideAnimationMap)
        {
            var descriptorList = (descriptors ?? Enumerable.Empty<DeckFrameDescriptor>())
                .Select(d => new FrameRecord(
                    d.FrameIndex,
                    d.ExportFrameIndex,
                    d.OriginalSlideIndex,
                    d.ClickIndex,
                    d.IsBoundary,
                    d.BoundaryAfterSlideIndex))
                .ToList();

            var mapCopy = new Dictionary<int, List<int>>();
            if (slideAnimationMap != null)
            {
                foreach (var kvp in slideAnimationMap)
                {
                    mapCopy[kvp.Key] = (kvp.Value ?? new List<int>()).ToList();
                }
            }

            return new DeckIndex(descriptorList, mapCopy, descriptorList.Count > 0);
        }
    }
}
