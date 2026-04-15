using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using iText.Layout.Element;
using iText.IO.Image;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BackendServer.Features.Materials.Services
{
    /// <summary>
    /// Service for PDF manipulation, including merging ink overlay PNGs onto base PDFs.
    /// </summary>
    public class PdfService
    {
        private static readonly Regex CanonicalInkFileRegex = new(@"^slide_(?<index>\d+)\.png$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LegacyInkFileRegex = new(@"^(?<prefix>.+)_(?<kind>slide|frame)(?<index>\d+)_ink\.png$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ILogger<PdfService> _logger;

        public PdfService(ILogger<PdfService>? logger = null)
        {
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfService>.Instance;
        }

        /// <summary>
        /// Merges ink overlay PNG images onto the corresponding pages of a PDF.
        /// </summary>
        /// <param name="originalPdfPath">Path to the original PDF file.</param>
        /// <param name="inkImagePaths">Dictionary mapping slide number (1-based) to ink PNG file path.</param>
        /// <param name="frameToSlideMap">Optional mapping of flattened PDF page index (1-based) to physical slide index.</param>
        /// <returns>Byte array of the merged PDF.</returns>
        public byte[] MergeInkIntoPdf(string originalPdfPath, Dictionary<int, string> inkImagePaths, Dictionary<int, int>? frameToSlideMap = null)
        {
            if (!File.Exists(originalPdfPath))
            {
                throw new FileNotFoundException($"Original PDF not found: {originalPdfPath}");
            }

            using var reader = new PdfReader(originalPdfPath);
            using var msOut = new MemoryStream();
            using var writer = new PdfWriter(msOut);
            using var pdfDoc = new PdfDocument(reader, writer);

            var pageCount = pdfDoc.GetNumberOfPages();
            for (int i = 1; i <= pageCount; i++)
            {
                if (!TryResolveInkPathForPage(i, inkImagePaths, frameToSlideMap, out var pngPath, out var resolvedInkKey))
                {
                    _logger.LogDebug("[PdfService] No ink artifact found for page {PageNumber}", i);
                    continue;
                }

                try
                {
                    var imageData = ImageDataFactory.Create(pngPath);
                    var inkImage = new Image(imageData);

                    var page = pdfDoc.GetPage(i);
                    var pageSize = page.GetPageSize();

                    _logger.LogInformation(
                        "[PdfService] Per-page bake start page={PageNumber} inkKey={InkKey} baseSize={BaseWidth}x{BaseHeight} overlaySize={OverlayWidth}x{OverlayHeight} mode=base-page-plus-overlay path={InkPath}",
                        i,
                        resolvedInkKey,
                        pageSize.GetWidth(),
                        pageSize.GetHeight(),
                        imageData.GetWidth(),
                        imageData.GetHeight(),
                        pngPath);

                    inkImage.ScaleAbsolute(pageSize.GetWidth(), pageSize.GetHeight());
                    inkImage.SetFixedPosition(0, 0);

                    using var canvas = new Canvas(new PdfCanvas(page), pageSize);
                    canvas.Add(inkImage);

                    _logger.LogInformation(
                        "[PdfService] Per-page bake end page={PageNumber} inkKey={InkKey}",
                        i,
                        resolvedInkKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PdfService] Error merging ink for page {PageNumber} using key {InkKey}", i, resolvedInkKey);
                }
            }

            pdfDoc.Close();
            return msOut.ToArray();
        }

        private static bool TryResolveInkPathForPage(
            int pageIndex,
            Dictionary<int, string> inkImagePaths,
            Dictionary<int, int>? frameToSlideMap,
            out string pngPath,
            out int resolvedInkKey)
        {
            pngPath = string.Empty;
            resolvedInkKey = -1;

            var candidateKeys = new List<int>();
            if (frameToSlideMap != null
                && frameToSlideMap.TryGetValue(pageIndex, out var mappedSlideIndex)
                && mappedSlideIndex > 0)
            {
                candidateKeys.Add(mappedSlideIndex);
            }

            candidateKeys.Add(pageIndex);

            foreach (var key in candidateKeys.Distinct())
            {
                if (inkImagePaths.TryGetValue(key, out var candidatePath) && File.Exists(candidatePath))
                {
                    pngPath = candidatePath;
                    resolvedInkKey = key;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Convenience wrapper used by background tasks: merge ink if present or return
        /// original PDF bytes. Returns null if the base PDF does not exist.
        /// </summary>
        public async Task<byte[]?> GetAnnotatedPdfBytesAsync(string presentationId)
        {
            var uploads = Path.Combine(AppContext.BaseDirectory, "uploads");
            var pdfPath = ResolveBasePdfPath(uploads, presentationId);
            if (pdfPath == null)
            {
                _logger.LogWarning("[PdfService] Base PDF not found for presentation {PresentationId}", presentationId);
                return null;
            }

            var inkMap = FindInkFiles(uploads, presentationId);
            if (inkMap.Count > 0)
            {
                var frameToSlideMap = LoadFrameToSlideMap(uploads, presentationId);
                _logger.LogInformation(
                    "[PdfService] Annotated PDF source discovered for presentation {PresentationId}: basePdf={BasePdfPath} inkTargets={InkTargetCount}",
                    presentationId,
                    pdfPath,
                    inkMap.Count);

                return MergeInkIntoPdf(pdfPath, inkMap, frameToSlideMap);
            }

            _logger.LogWarning(
                "[PdfService] No ink artifacts discovered for presentation {PresentationId}; returning the base PDF bytes from {BasePdfPath}",
                presentationId,
                pdfPath);

            return await File.ReadAllBytesAsync(pdfPath);
        }

        /// <param name="uploadsDirectory">Path to the uploads directory.</param>
        /// <param name="presentationId">The presentation ID.</param>
        /// <returns>Dictionary mapping slide/frame index to ink file path.</returns>
        public Dictionary<int, string> FindInkFiles(string uploadsDirectory, string presentationId)
        {
            var inkMap = new Dictionary<int, string>();
            var timestamps = new Dictionary<int, DateTime>();

            if (!Directory.Exists(uploadsDirectory))
            {
                return inkMap;
            }

            var canonicalDirectories = ResolveCanonicalInkDirectories(uploadsDirectory, presentationId);
            foreach (var directory in canonicalDirectories)
            {
                CollectCanonicalInkFiles(directory, inkMap, timestamps);
            }

            foreach (var candidatePresentationId in ResolvePresentationIdCandidates(presentationId))
            {
                CollectLegacyInkFiles(uploadsDirectory, candidatePresentationId, "slide", inkMap, timestamps);
                CollectLegacyInkFiles(uploadsDirectory, candidatePresentationId, "frame", inkMap, timestamps);
            }

            _logger.LogInformation(
                "[PdfService] Artifact discovery for presentation {PresentationId}: inkTargets={InkTargets}, canonicalDirs={CanonicalDirCount}",
                presentationId,
                inkMap.Count,
                canonicalDirectories.Count);

            return inkMap;
        }

        private static void CollectCanonicalInkFiles(
            string inkDirectory,
            Dictionary<int, string> inkMap,
            Dictionary<int, DateTime> timestamps)
        {
            if (!Directory.Exists(inkDirectory))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(inkDirectory, "*.png"))
            {
                var filename = Path.GetFileName(file);
                var match = CanonicalInkFileRegex.Match(filename);
                if (!match.Success)
                {
                    continue;
                }

                if (!int.TryParse(match.Groups["index"].Value, out var index) || index <= 0)
                {
                    continue;
                }

                UpdateInkMapEntry(inkMap, timestamps, index, file);
            }
        }

        private static void CollectLegacyInkFiles(
            string uploadsDirectory,
            string presentationId,
            string keyPrefix,
            Dictionary<int, string> inkMap,
            Dictionary<int, DateTime> timestamps)
        {
            var pattern = $"{presentationId}_{keyPrefix}*_ink.png";
            var files = Directory.GetFiles(uploadsDirectory, pattern);

            foreach (var file in files)
            {
                var filename = Path.GetFileName(file);
                var match = LegacyInkFileRegex.Match(filename);
                if (!match.Success)
                {
                    continue;
                }

                if (!string.Equals(match.Groups["kind"].Value, keyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!int.TryParse(match.Groups["index"].Value, out var index) || index <= 0)
                {
                    continue;
                }

                UpdateInkMapEntry(inkMap, timestamps, index, file);
            }
        }

        private static void UpdateInkMapEntry(
            Dictionary<int, string> inkMap,
            Dictionary<int, DateTime> timestamps,
            int index,
            string file)
        {
            var timestamp = File.Exists(file)
                ? File.GetLastWriteTimeUtc(file)
                : DateTime.MinValue;

            if (!inkMap.ContainsKey(index) || timestamp >= timestamps[index])
            {
                inkMap[index] = file;
                timestamps[index] = timestamp;
            }
        }

        /// <summary>
        /// Loads a flattened frame->slide mapping from uploads/{presentationId}.framemap.json.
        /// JSON format is expected as slide->frames, e.g. {"2":[2,3,4]}.
        /// </summary>
        private Dictionary<int, int> LoadFrameToSlideMap(string uploadsDirectory, string presentationId)
        {
            var frameToSlide = new Dictionary<int, int>();

            foreach (var mapPath in ResolveFrameMapCandidates(uploadsDirectory, presentationId))
            {
                if (!File.Exists(mapPath))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(mapPath);
                    var slideToFrames = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, List<int>>>(json);
                    if (slideToFrames == null)
                    {
                        continue;
                    }

                    foreach (var kvp in slideToFrames)
                    {
                        var slideIndex = kvp.Key;
                        if (slideIndex <= 0)
                        {
                            continue;
                        }

                        foreach (var frameIndex in kvp.Value ?? new List<int>())
                        {
                            if (frameIndex <= 0)
                            {
                                continue;
                            }

                            if (!frameToSlide.ContainsKey(frameIndex))
                            {
                                frameToSlide[frameIndex] = slideIndex;
                            }
                        }
                    }

                    _logger.LogInformation(
                        "[PdfService] Loaded frame map for presentation {PresentationId} from {MapPath}: frameEntries={FrameEntries}",
                        presentationId,
                        mapPath,
                        frameToSlide.Count);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PdfService] Failed to load frame map for presentation {PresentationId} from {MapPath}", presentationId, mapPath);
                }
            }

            return frameToSlide;
        }

        private static string? ResolveBasePdfPath(string uploadsDirectory, string presentationId)
        {
            foreach (var candidate in ResolvePresentationIdCandidates(presentationId))
            {
                var path = Path.Combine(uploadsDirectory, candidate + ".pdf");
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static IReadOnlyList<string> ResolvePresentationIdCandidates(string presentationId)
        {
            var candidates = new List<string> { presentationId };

            if (Guid.TryParse(presentationId, out var guid))
            {
                candidates.Add(guid.ToString("D"));
                candidates.Add(guid.ToString("N"));
            }

            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IReadOnlyList<string> ResolveCanonicalInkDirectories(string uploadsDirectory, string presentationId)
        {
            var directories = new List<string>();

            foreach (var candidate in ResolvePresentationIdCandidates(presentationId))
            {
                directories.Add(Path.Combine(uploadsDirectory, candidate, "ink-artifacts"));
            }

            return directories
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToList();
        }

        private static IReadOnlyList<string> ResolveFrameMapCandidates(string uploadsDirectory, string presentationId)
        {
            var paths = new List<string>();

            foreach (var candidate in ResolvePresentationIdCandidates(presentationId))
            {
                paths.Add(Path.Combine(uploadsDirectory, candidate + ".framemap.json"));
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
