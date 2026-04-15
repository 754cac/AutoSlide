using System;
using System.Collections.Generic;

namespace PowerPointSharing
{
    internal enum SolutionPageKind
    {
        Blank = 0,
        CurrentSlide = 1
    }

    internal enum SolutionDraftLifecycleState
    {
        NormalSlideInk = 0,
        DraftBlankSolution = 1,
        DraftCurrentSlideSolution = 2,
        SavingSolution = 3
    }

    internal sealed class SolutionPageState
    {
        public string SessionId { get; set; } = string.Empty;
        public string SolutionPageId { get; set; } = string.Empty;
        public SolutionPageKind Kind { get; set; }
        public int? SourceSlideIndex { get; set; }
        public int OrderIndex { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public int RenderWidth { get; set; }
        public int RenderHeight { get; set; }
        public byte[] BackgroundImageBytes { get; set; } = Array.Empty<byte>();
        public List<InkStrokeData> LatestStrokes { get; set; } = new List<InkStrokeData>();

        public bool HasInk => LatestStrokes != null && LatestStrokes.Count > 0;
    }

    internal sealed class CreateSolutionPageRequest
    {
        public string Kind { get; set; } = "blank";
        public int? SourceSlideIndex { get; set; }
        public int OrderIndex { get; set; }
    }
}