using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;
using StorageFileOptions = Supabase.Storage.FileOptions;  // alias avoids ambiguity

// using Supabase;
// using Supabase.Storage;

namespace BackendServer.Features.Materials.Services
{
    /// <summary>
    /// Splits a converted PDF into single-page PDFs and stores everything in private Supabase buckets.
    ///
    /// Bucket layout
    /// ─────────────
    ///   presentations/{courseId}/{sessionId}/original.pptx   ← source PPTX
    ///   slides/{courseId}/{sessionId}/full.pdf               ← instructor backup
    ///   slides/{courseId}/{sessionId}/pages/page_001.pdf     ← per-page PDF (student viewer)
    ///   slides/{courseId}/{sessionId}/pages/page_002.pdf
    ///   …
    /// </summary>
    public class SlideSplitterService
    {
        private readonly Supabase.Client _supabaseClient;

        public SlideSplitterService(Supabase.Client supabaseClient)
        {
            _supabaseClient = supabaseClient;
        }

        /// <summary>
        /// Uploads both PPTX and full PDF, splits the PDF into pages and uploads each page.
        /// Returns total number of pages.
        /// </summary>
        public async Task<int> UploadPptxAndSplitPdf(
            byte[] pptxBytes, byte[] fullPdfBytes, Guid courseId, Guid sessionId)
        {
            // 1. Upload original PPTX to presentations bucket
            var pptxPath = $"{courseId}/{sessionId}/original.pptx";
            await _supabaseClient.Storage
                .From("presentations")
                .Upload(pptxBytes, pptxPath, new StorageFileOptions
                {
                    ContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                    Upsert = true
                });

            // 2. Upload full PDF backup to slides bucket
            var fullPdfPath = $"{courseId}/{sessionId}/full.pdf";
            await _supabaseClient.Storage
                .From("slides")
                .Upload(fullPdfBytes, fullPdfPath, new StorageFileOptions
                {
                    ContentType = "application/pdf",
                    Upsert = true
                });

            // 3. Split into single-page PDFs and upload each
            using var sourceDoc = PdfDocument.Open(fullPdfBytes);
            int totalPages = sourceDoc.NumberOfPages;

            for (int i = 1; i <= totalPages; i++)
            {
                var pageBytes = ExtractSinglePage(sourceDoc, i);
                var pagePath = $"{courseId}/{sessionId}/pages/page_{i:D3}.pdf";

                await _supabaseClient.Storage
                    .From("slides")
                    .Upload(pageBytes, pagePath, new StorageFileOptions
                    {
                        ContentType = "application/pdf",
                        Upsert = true
                    });
            }

            return totalPages;
        }

        /// <summary>
        /// Splits any arbitrary PDF (typically the inked/annotated version) into
        /// per‑page files and uploads them to the same locations used for normal
        /// slides. This allows viewers who reload after the session ends to
        /// continue receiving the annotated versions without needing live ink
        /// state from the presenter.
        /// </summary>
        public async Task SplitPdfAndUploadPages(byte[] pdfBytes, Guid courseId, Guid sessionId)
        {
            using var sourceDoc = PdfDocument.Open(pdfBytes);
            int totalPages = sourceDoc.NumberOfPages;

            for (int i = 1; i <= totalPages; i++)
            {
                var pageBytes = ExtractSinglePage(sourceDoc, i);
                var pagePath = $"{courseId}/{sessionId}/pages/page_{i:D3}.pdf";

                await _supabaseClient.Storage
                    .From("slides")
                    .Upload(pageBytes, pagePath, new StorageFileOptions
                    {
                        ContentType = "application/pdf",
                        Upsert = true
                    });
            }
        }

        // ──────────────────────────────────────────────────────
        // Signed URL helpers
        // ──────────────────────────────────────────────────────

        /// <summary>Issues a short-lived signed URL for a single-page PDF (default 120 s).</summary>
        public async Task<string> GetSlideSignedUrlAsync(
            Guid courseId, Guid sessionId, int pageIndex, int expirySeconds = 120)
        {
            var path = $"{courseId}/{sessionId}/pages/page_{pageIndex:D3}.pdf";
            return await _supabaseClient.Storage.From("slides").CreateSignedUrl(path, expirySeconds);
        }

        /// <summary>Issues a signed URL for the full PDF backup (default 300 s).</summary>
        public async Task<string> GetFullPdfSignedUrlAsync(
            Guid courseId, Guid sessionId, int expirySeconds = 300)
        {
            var path = $"{courseId}/{sessionId}/full.pdf";
            return await _supabaseClient.Storage.From("slides").CreateSignedUrl(path, expirySeconds);
        }

        /// <summary>Issues a signed URL for the original PPTX (default 300 s).</summary>
        public async Task<string> GetPptxSignedUrlAsync(
            Guid courseId, Guid sessionId, int expirySeconds = 300)
        {
            var path = $"{courseId}/{sessionId}/original.pptx";
            return await _supabaseClient.Storage.From("presentations").CreateSignedUrl(path, expirySeconds);
        }

        // ──────────────────────────────────────────────────────
        // Private helpers
        // ──────────────────────────────────────────────────────

        private static byte[] ExtractSinglePage(PdfDocument source, int pageNumber)
        {
            // Caller owns 'source' lifetime; use builder to create a new single-page document
            var builder = new PdfDocumentBuilder();
            builder.AddPage(source, pageNumber);
            return builder.Build();
        }
    }
}
