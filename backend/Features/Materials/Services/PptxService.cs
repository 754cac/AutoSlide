using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace BackendServer.Features.Materials.Services
{
    /// <summary>
    /// Service for PPTX manipulation, including merging ink overlay PNGs into PowerPoint files.
    /// PPTX is a ZIP archive containing XML slides - we modify the XML structure to embed images.
    /// </summary>
    public class PptxService
    {
        // XML Namespaces used in PPTX files
        private static readonly XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        private static readonly XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";

        /// <summary>
        /// Merges ink overlay PNG images into the corresponding slides of a PPTX file.
        /// </summary>
        /// <param name="originalPptxPath">Path to the original PPTX file.</param>
        /// <param name="inkImagePaths">Dictionary mapping slide number (1-based) to ink PNG file path.</param>
        /// <returns>Byte array of the modified PPTX.</returns>
        public byte[] MergeInkIntoPptx(string originalPptxPath, Dictionary<int, string> inkImagePaths)
        {
            if (!File.Exists(originalPptxPath))
            {
                throw new FileNotFoundException($"Original PPTX not found: {originalPptxPath}");
            }

            using (var ms = new MemoryStream())
            {
                // Copy original PPTX to memory stream
                using (var fileStream = File.OpenRead(originalPptxPath))
                {
                    fileStream.CopyTo(ms);
                }
                ms.Position = 0;

                // Open as ZIP archive for modification
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
                {
                    // Process each slide with ink
                    foreach (var entry in inkImagePaths)
                    {
                        int slideNumber = entry.Key;
                        string inkImagePath = entry.Value;

                        if (File.Exists(inkImagePath))
                        {
                            try
                            {
                                InsertInkOverlayIntoSlide(archive, slideNumber, inkImagePath);
                                Console.WriteLine($"[PptxService] Inserted ink overlay for slide {slideNumber}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[PptxService] Error inserting ink for slide {slideNumber}: {ex.Message}");
                                // Continue with other slides
                            }
                        }
                    }

                    // Update [Content_Types].xml to include PNG type if not present
                    EnsurePngContentType(archive);
                }

                return ms.ToArray();
            }
        }

        private void InsertInkOverlayIntoSlide(ZipArchive archive, int slideNumber, string inkImagePath)
        {
            string slideXmlPath = $"ppt/slides/slide{slideNumber}.xml";
            string slideRelsPath = $"ppt/slides/_rels/slide{slideNumber}.xml.rels";
            string mediaPath = $"ppt/media/ink_overlay_slide{slideNumber}.png";

            // ===== STEP 1: Add image to media folder =====
            var existingMedia = archive.GetEntry(mediaPath);
            existingMedia?.Delete();
            
            var mediaEntry = archive.CreateEntry(mediaPath, CompressionLevel.Optimal);
            using (var mediaStream = mediaEntry.Open())
            using (var fileStream = File.OpenRead(inkImagePath))
            {
                fileStream.CopyTo(mediaStream);
            }

            // ===== STEP 2: Update relationships file =====
            var relEntry = archive.GetEntry(slideRelsPath);
            XDocument relDoc;
            
            if (relEntry != null)
            {
                using (var stream = relEntry.Open())
                {
                    relDoc = XDocument.Load(stream);
                }
            }
            else
            {
                // Create new relationships file
                relDoc = new XDocument(
                    new XElement(rel + "Relationships")
                );
            }

            // Find next available rId
            var root = relDoc.Root;
            int maxId = root?.Elements()
                .Select(e => {
                    var idAttr = e.Attribute("Id")?.Value ?? "rId0";
                    return int.TryParse(idAttr.Replace("rId", ""), out int id) ? id : 0;
                })
                .DefaultIfEmpty(0)
                .Max() ?? 0;

            string newRId = $"rId{maxId + 1}";

            // Add new image relationship
            root?.Add(new XElement(rel + "Relationship",
                new XAttribute("Id", newRId),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"),
                new XAttribute("Target", $"../media/ink_overlay_slide{slideNumber}.png")
            ));

            // Save updated relationships
            if (relEntry != null)
            {
                relEntry.Delete();
            }
            relEntry = archive.CreateEntry(slideRelsPath, CompressionLevel.Optimal);
            using (var stream = relEntry.Open())
            {
                relDoc.Save(stream);
            }

            // ===== STEP 3: Insert image shape into slide XML =====
            var slideEntry = archive.GetEntry(slideXmlPath);
            if (slideEntry == null)
            {
                Console.WriteLine($"[PptxService] Slide {slideNumber} not found in archive");
                return;
            }

            XDocument slideDoc;
            using (var stream = slideEntry.Open())
            {
                slideDoc = XDocument.Load(stream);
            }

            var slideRoot = slideDoc.Root;
            var cSld = slideRoot?.Element(p + "cSld");
            var spTree = cSld?.Element(p + "spTree");

            if (spTree == null)
            {
                Console.WriteLine($"[PptxService] Invalid slide structure for slide {slideNumber}");
                return;
            }

            // Get a unique shape ID (find max existing ID and add 1)
            int maxShapeId = spTree.Descendants()
                .Where(e => e.Attribute("id") != null)
                .Select(e => int.TryParse(e.Attribute("id")?.Value, out int id) ? id : 0)
                .DefaultIfEmpty(100)
                .Max() + 1;

            // Standard slide dimensions in EMU (English Metric Units)
            // 1 inch = 914400 EMU
            // Standard 16:9 slide: 10" x 5.625" = 9144000 x 5143500 EMU
            // Standard 4:3 slide: 10" x 7.5" = 9144000 x 6858000 EMU
            long slideWidth = 9144000;
            long slideHeight = 5143500;

            // Create picture element (p:pic)
            var picElement = new XElement(p + "pic",
                // Non-visual properties
                new XElement(p + "nvPicPr",
                    new XElement(p + "cNvPr",
                        new XAttribute("id", maxShapeId),
                        new XAttribute("name", $"Ink Overlay {slideNumber}")
                    ),
                    new XElement(p + "cNvPicPr",
                        new XElement(a + "picLocks",
                            new XAttribute("noChangeAspect", "1")
                        )
                    ),
                    new XElement(p + "nvPr")
                ),
                // Blip fill (image reference)
                new XElement(p + "blipFill",
                    new XElement(a + "blip",
                        new XAttribute(r + "embed", newRId)
                    ),
                    new XElement(a + "stretch",
                        new XElement(a + "fillRect")
                    )
                ),
                // Shape properties (position and size)
                new XElement(p + "spPr",
                    new XElement(a + "xfrm",
                        new XElement(a + "off",
                            new XAttribute("x", "0"),
                            new XAttribute("y", "0")
                        ),
                        new XElement(a + "ext",
                            new XAttribute("cx", slideWidth),
                            new XAttribute("cy", slideHeight)
                        )
                    ),
                    new XElement(a + "prstGeom",
                        new XAttribute("prst", "rect"),
                        new XElement(a + "avLst")
                    )
                )
            );

            // Add the picture element at the END of spTree (so it's on top)
            spTree.Add(picElement);

            // Save updated slide XML
            slideEntry.Delete();
            var newSlideEntry = archive.CreateEntry(slideXmlPath, CompressionLevel.Optimal);
            using (var stream = newSlideEntry.Open())
            {
                slideDoc.Save(stream);
            }
        }

        /// <summary>
        /// Ensures the [Content_Types].xml includes PNG extension.
        /// </summary>
        private void EnsurePngContentType(ZipArchive archive)
        {
            var contentTypesEntry = archive.GetEntry("[Content_Types].xml");
            if (contentTypesEntry == null) return;

            XDocument doc;
            using (var stream = contentTypesEntry.Open())
            {
                doc = XDocument.Load(stream);
            }

            XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
            var root = doc.Root;

            // Check if PNG extension is already defined
            bool hasPng = root?.Elements(ct + "Default")
                .Any(e => e.Attribute("Extension")?.Value?.ToLower() == "png") ?? false;

            if (!hasPng)
            {
                root?.Add(new XElement(ct + "Default",
                    new XAttribute("Extension", "png"),
                    new XAttribute("ContentType", "image/png")
                ));

                // Save updated content types
                contentTypesEntry.Delete();
                var newEntry = archive.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
                using (var stream = newEntry.Open())
                {
                    doc.Save(stream);
                }
            }
        }

        /// <summary>
        /// Finds all ink PNG files for a given presentation in the uploads directory.
        /// </summary>
        public Dictionary<int, string> FindInkFiles(string uploadsDirectory, string presentationId)
        {
            var inkMap = new Dictionary<int, string>();
            var timestamps = new Dictionary<int, DateTime>();

            if (!Directory.Exists(uploadsDirectory))
            {
                return inkMap;
            }

            CollectInkFiles(uploadsDirectory, presentationId, "slide", inkMap, timestamps);
            CollectInkFiles(uploadsDirectory, presentationId, "frame", inkMap, timestamps);

            return inkMap;
        }

        private static void CollectInkFiles(
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
                string filename = Path.GetFileNameWithoutExtension(file);
                int keyStart = filename.IndexOf("_" + keyPrefix, StringComparison.Ordinal) + keyPrefix.Length + 1;
                int inkStart = filename.IndexOf("_ink", StringComparison.Ordinal);

                if (keyStart <= keyPrefix.Length || inkStart <= keyStart)
                {
                    continue;
                }

                string numberPart = filename.Substring(keyStart, inkStart - keyStart);
                if (!int.TryParse(numberPart, out int index) || index <= 0)
                {
                    continue;
                }

                var timestamp = File.Exists(file)
                    ? File.GetLastWriteTimeUtc(file)
                    : DateTime.MinValue;

                if (!inkMap.ContainsKey(index) || timestamp >= timestamps[index])
                {
                    inkMap[index] = file;
                    timestamps[index] = timestamp;
                }
            }
        }

        /// <summary>
        /// Convenience wrapper used by background tasks: merge ink into the PPTX
        /// and return bytes, or simply return the original file if no ink exists.
        /// Returns null if the base PPTX does not exist.
        /// </summary>
        public async Task<byte[]?> GetAnnotatedPptxBytesAsync(string presentationId)
        {
            var uploads = Path.Combine(AppContext.BaseDirectory, "uploads");
            var pptxPath = Path.Combine(uploads, presentationId + ".pptx");
            if (!File.Exists(pptxPath))
                return null;

            var inkMap = FindInkFiles(uploads, presentationId);
            if (inkMap.Count > 0)
            {
                var frameToSlideMap = LoadFrameToSlideMap(uploads, presentationId);
                var normalizedInkMap = NormalizeInkMapForPhysicalSlides(inkMap, frameToSlideMap);
                return MergeInkIntoPptx(pptxPath, normalizedInkMap);
            }

            return await File.ReadAllBytesAsync(pptxPath);
        }

        private static Dictionary<int, string> NormalizeInkMapForPhysicalSlides(
            Dictionary<int, string> inkMap,
            Dictionary<int, int> frameToSlideMap)
        {
            if (inkMap.Count == 0)
            {
                return inkMap;
            }

            var normalized = new Dictionary<int, string>();
            var timestamps = new Dictionary<int, DateTime>();

            foreach (var kvp in inkMap)
            {
                var sourceKey = kvp.Key;
                var targetSlide = sourceKey;
                if (frameToSlideMap.TryGetValue(sourceKey, out var mappedSlideIndex) && mappedSlideIndex > 0)
                {
                    targetSlide = mappedSlideIndex;
                }

                if (targetSlide <= 0)
                {
                    continue;
                }

                var timestamp = File.Exists(kvp.Value)
                    ? File.GetLastWriteTimeUtc(kvp.Value)
                    : DateTime.MinValue;

                if (!normalized.ContainsKey(targetSlide)
                    || timestamp >= timestamps[targetSlide])
                {
                    normalized[targetSlide] = kvp.Value;
                    timestamps[targetSlide] = timestamp;
                }
            }

            return normalized;
        }

        private static Dictionary<int, int> LoadFrameToSlideMap(string uploadsDirectory, string presentationId)
        {
            var frameToSlide = new Dictionary<int, int>();
            var mapPath = Path.Combine(uploadsDirectory, presentationId + ".framemap.json");

            if (!File.Exists(mapPath))
            {
                return frameToSlide;
            }

            try
            {
                var json = File.ReadAllText(mapPath);
                var slideToFrames = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, List<int>>>(json);
                if (slideToFrames == null)
                {
                    return frameToSlide;
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PptxService] Failed to load frame map for {presentationId}: {ex.Message}");
            }

            return frameToSlide;
        }
    }
}
