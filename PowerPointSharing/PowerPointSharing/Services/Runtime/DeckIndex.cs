using System;
using System.Collections.Generic;
using System.Linq;

namespace PowerPointSharing
{
    internal sealed class DeckFrameDescriptor
    {
        public int FrameIndex { get; set; }
        public int? ExportFrameIndex { get; set; }
        public int OriginalSlideIndex { get; set; }
        public int? ClickIndex { get; set; }
        public bool IsBoundary { get; set; }
        public int? BoundaryAfterSlideIndex { get; set; }
    }

    internal sealed class FrameRecord
    {
        public FrameRecord(
            int frameIndex,
            int? exportFrameIndex,
            int originalSlideIndex,
            int? clickIndex,
            bool isBoundary,
            int? boundaryAfterSlideIndex)
        {
            FrameIndex = frameIndex;
            ExportFrameIndex = exportFrameIndex;
            OriginalSlideIndex = originalSlideIndex;
            ClickIndex = clickIndex;
            IsBoundary = isBoundary;
            BoundaryAfterSlideIndex = boundaryAfterSlideIndex;
        }

        public int FrameIndex { get; }
        public int? ExportFrameIndex { get; }
        public int OriginalSlideIndex { get; }
        public int? ClickIndex { get; }
        public bool IsBoundary { get; }
        public int? BoundaryAfterSlideIndex { get; }
    }

    internal sealed class SlideRange
    {
        public SlideRange(int slideIndex, int startFrameIndex, int endFrameIndex)
        {
            SlideIndex = slideIndex;
            StartFrameIndex = startFrameIndex;
            EndFrameIndex = endFrameIndex;
        }

        public int SlideIndex { get; }
        public int StartFrameIndex { get; }
        public int EndFrameIndex { get; }
    }

    internal sealed class DeckIndex
    {
        private static readonly List<int> EmptyIntList = new List<int>();

        private readonly Dictionary<int, FrameRecord> _frameByFrameIndex;
        private readonly Dictionary<int, List<int>> _contentFramesBySlide;
        private readonly Dictionary<int, List<int>> _exportFramesBySlide;
        private readonly Dictionary<string, int> _exportFrameBySlideClick;
        private readonly Dictionary<int, int> _clickByFrameIndex;
        private readonly List<FrameRecord> _orderedFrames;

        public DeckIndex(
            IEnumerable<FrameRecord> frames,
            Dictionary<int, List<int>>? slideAnimationMap,
            bool hasCanonicalDescriptors)
        {
            _orderedFrames = (frames ?? Enumerable.Empty<FrameRecord>())
                .OrderBy(f => f.FrameIndex)
                .ToList();

            _frameByFrameIndex = _orderedFrames
                .GroupBy(f => f.FrameIndex)
                .ToDictionary(g => g.Key, g => g.First());

            HasCanonicalDescriptors = hasCanonicalDescriptors;
            HasAnyFrameData = _orderedFrames.Count > 0 || ((slideAnimationMap?.Count ?? 0) > 0);

            _contentFramesBySlide = BuildContentFramesBySlide(_orderedFrames, slideAnimationMap);
            _exportFramesBySlide = BuildExportFramesBySlide(_orderedFrames, _contentFramesBySlide);
            _exportFrameBySlideClick = BuildSlideClickExportMap(_orderedFrames, _contentFramesBySlide);
            _clickByFrameIndex = BuildClickByFrameIndex(_orderedFrames, _contentFramesBySlide);
            SlideRanges = BuildSlideRanges(_exportFramesBySlide);
        }

        public bool HasCanonicalDescriptors { get; }
        public bool HasAnyFrameData { get; }
        public IReadOnlyList<SlideRange> SlideRanges { get; }

        public List<int> GetContentFramesForSlide(int slideIndex)
        {
            if (_contentFramesBySlide.TryGetValue(slideIndex, out var frames))
                return new List<int>(frames);

            return new List<int> { slideIndex };
        }

        public List<int> GetExportFramesForSlide(int slideIndex)
        {
            if (_exportFramesBySlide.TryGetValue(slideIndex, out var frames))
                return new List<int>(frames);

            return new List<int> { slideIndex };
        }

        public bool IsBoundaryFrame(int frameIndex)
        {
            if (_frameByFrameIndex.TryGetValue(frameIndex, out var record))
                return record.IsBoundary;

            return false;
        }

        public int ResolveContentFrameForSlideClick(int slideIndex, int clickIndex)
        {
            var frames = GetContentFramesForSlide(slideIndex);
            if (frames.Count == 0)
                return slideIndex;

            if (clickIndex < 0) clickIndex = 0;
            if (clickIndex >= frames.Count) clickIndex = frames.Count - 1;
            return frames[clickIndex];
        }

        public int ResolveExportFrameForSlideClick(int slideIndex, int clickIndex)
        {
            if (_exportFrameBySlideClick.TryGetValue(BuildSlideClickKey(slideIndex, clickIndex), out var mappedFrame))
                return mappedFrame;

            var contentFrame = ResolveContentFrameForSlideClick(slideIndex, clickIndex);
            return ResolveExportFrame(contentFrame);
        }

        public int ResolveExportFrame(int contentFrameIndex)
        {
            if (_frameByFrameIndex.TryGetValue(contentFrameIndex, out var record))
            {
                if (record.ExportFrameIndex.HasValue)
                    return record.ExportFrameIndex.Value;

                if (record.IsBoundary)
                    return GetNextRenderableFrame(contentFrameIndex);
            }

            return contentFrameIndex;
        }

        public int ResolveClickIndex(int slideIndex, int contentFrameIndex, int fallbackClickIndex)
        {
            if (_clickByFrameIndex.TryGetValue(contentFrameIndex, out var clickIndex))
                return clickIndex;

            var contentFrames = GetContentFramesForSlide(slideIndex);
            var ordinal = contentFrames.IndexOf(contentFrameIndex);
            if (ordinal >= 0)
                return ordinal;

            return fallbackClickIndex;
        }

        private int GetNextRenderableFrame(int frameIndex)
        {
            var next = _orderedFrames
                .Where(fd => !fd.IsBoundary && fd.ExportFrameIndex.HasValue && fd.FrameIndex > frameIndex)
                .OrderBy(fd => fd.FrameIndex)
                .FirstOrDefault();

            if (next != null)
                return next.ExportFrameIndex ?? frameIndex;

            return frameIndex;
        }

        private static Dictionary<int, List<int>> BuildContentFramesBySlide(
            List<FrameRecord> orderedFrames,
            Dictionary<int, List<int>>? slideAnimationMap)
        {
            var fromDescriptors = orderedFrames
                .Where(fd => !fd.IsBoundary && fd.ExportFrameIndex.HasValue)
                .GroupBy(fd => fd.OriginalSlideIndex)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(fd => fd.FrameIndex).Distinct().OrderBy(i => i).ToList());

            if (fromDescriptors.Count > 0)
                return fromDescriptors;

            var fromAnimationMap = new Dictionary<int, List<int>>();
            if (slideAnimationMap == null)
                return fromAnimationMap;

            foreach (var kvp in slideAnimationMap)
            {
                var values = kvp.Value ?? EmptyIntList;
                if (values.Count == 0)
                    continue;

                fromAnimationMap[kvp.Key] = values
                    .Distinct()
                    .OrderBy(v => v)
                    .ToList();
            }

            return fromAnimationMap;
        }

        private static Dictionary<int, List<int>> BuildExportFramesBySlide(
            List<FrameRecord> orderedFrames,
            Dictionary<int, List<int>> contentFramesBySlide)
        {
            var exportFramesBySlide = orderedFrames
                .Where(fd => !fd.IsBoundary && fd.ExportFrameIndex.HasValue)
                .GroupBy(fd => fd.OriginalSlideIndex)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(fd => fd.ExportFrameIndex ?? fd.FrameIndex).Distinct().OrderBy(i => i).ToList());

            if (exportFramesBySlide.Count > 0)
                return exportFramesBySlide;

            var fallback = new Dictionary<int, List<int>>();
            foreach (var kvp in contentFramesBySlide)
            {
                fallback[kvp.Key] = kvp.Value
                    .Distinct()
                    .OrderBy(v => v)
                    .ToList();
            }
            return fallback;
        }

        private static Dictionary<string, int> BuildSlideClickExportMap(
            List<FrameRecord> orderedFrames,
            Dictionary<int, List<int>> contentFramesBySlide)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var frame in orderedFrames)
            {
                if (frame.IsBoundary || !frame.ExportFrameIndex.HasValue || !frame.ClickIndex.HasValue)
                    continue;

                var key = BuildSlideClickKey(frame.OriginalSlideIndex, frame.ClickIndex.Value);
                map[key] = frame.ExportFrameIndex.Value;
            }

            if (map.Count > 0)
                return map;

            foreach (var kvp in contentFramesBySlide)
            {
                var slideIndex = kvp.Key;
                for (int click = 0; click < kvp.Value.Count; click++)
                {
                    map[BuildSlideClickKey(slideIndex, click)] = kvp.Value[click];
                }
            }

            return map;
        }

        private static Dictionary<int, int> BuildClickByFrameIndex(
            List<FrameRecord> orderedFrames,
            Dictionary<int, List<int>> contentFramesBySlide)
        {
            var map = new Dictionary<int, int>();

            foreach (var frame in orderedFrames)
            {
                if (frame.IsBoundary || !frame.ClickIndex.HasValue)
                    continue;

                map[frame.FrameIndex] = frame.ClickIndex.Value;
            }

            if (map.Count > 0)
                return map;

            foreach (var kvp in contentFramesBySlide)
            {
                for (int click = 0; click < kvp.Value.Count; click++)
                {
                    map[kvp.Value[click]] = click;
                }
            }

            return map;
        }

        private static List<SlideRange> BuildSlideRanges(Dictionary<int, List<int>> exportFramesBySlide)
        {
            return exportFramesBySlide
                .OrderBy(kvp => kvp.Key)
                .Select(kvp =>
                {
                    var ordered = kvp.Value.OrderBy(v => v).ToList();
                    return new SlideRange(kvp.Key, ordered.First(), ordered.Last());
                })
                .ToList();
        }

        private static string BuildSlideClickKey(int slideIndex, int clickIndex)
        {
            return slideIndex.ToString() + ":" + clickIndex.ToString();
        }
    }
}
