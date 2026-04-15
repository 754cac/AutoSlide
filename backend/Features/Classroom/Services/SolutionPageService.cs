using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using BackendServer.Shared.Services;
using iText.IO.Image;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using PdfPageSize = iText.Kernel.Geom.PageSize;
using Microsoft.Extensions.Logging;

namespace BackendServer.Features.Classroom.Services;

public sealed class SolutionPageMetadata
{
    public Guid SessionId { get; set; }
    public string SolutionPageId { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public string Kind { get; set; } = "blank";
    public int? SourceSlideIndex { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool HasInk { get; set; }
}

public sealed class SolutionAppendixResult
{
    public bool Generated { get; init; }
    public int PageCount { get; init; }
    public string? LocalPath { get; init; }
    public string? StoragePath { get; init; }
    public byte[]? PdfBytes { get; init; }
}

public sealed class CreateSolutionPageServiceRequest
{
    public string Kind { get; set; } = "blank";
    public int? SourceSlideIndex { get; set; }
    public int? OrderIndex { get; set; }
}

public interface ISolutionPageService
{
    Task<SolutionPageMetadata> CreateSolutionPageAsync(Guid sessionId, CreateSolutionPageServiceRequest request, CancellationToken cancellationToken = default);
    Task<SolutionPageMetadata?> UpdateSolutionArtifactAsync(Guid sessionId, string solutionPageId, byte[] pngBytes, bool hasInk, CreateSolutionPageServiceRequest? upsertRequest = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SolutionPageMetadata>> ListSolutionPagesAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<SolutionPageMetadata?> GetSolutionPageAsync(Guid sessionId, string solutionPageId, CancellationToken cancellationToken = default);
    Task<string?> TryGetSignedImageUrlAsync(SolutionPageMetadata metadata, int expirySeconds = 300);
    Task<SolutionAppendixResult> GenerateSolutionsAppendixAsync(Guid sessionId, bool skipEmptyPages = true, CancellationToken cancellationToken = default);
    Task<byte[]> AppendSolutionsToDeckPdfAsync(Guid sessionId, byte[] baseDeckPdfBytes, bool skipEmptyPages = true, CancellationToken cancellationToken = default);
}

public sealed class SolutionPageService : ISolutionPageService
{
    private sealed class SolutionManifest
    {
        public string SessionId { get; set; } = string.Empty;
        public List<SolutionPageMetadata> Items { get; set; } = new();
    }

    private sealed class SessionLockEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        // Accessed via Interlocked to avoid torn reads/writes on 32-bit hosts.
        private long _lastUsedTicks = DateTime.UtcNow.Ticks;

        public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
        public long ReadLastUsedTicks() => Interlocked.Read(ref _lastUsedTicks);
    }

    private static readonly ConcurrentDictionary<string, SessionLockEntry> SessionLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LockIdleEvictionAge = TimeSpan.FromHours(2);
    // Static timer lives for the application lifetime, which is intentional.
    private static readonly Timer EvictionTimer = new(EvictIdleLocks, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private const string ManifestBucket = "uploads";
    private const string ManifestContentType = "application/json";

    private readonly IStorageService _storage;
    private readonly ILogger<SolutionPageService> _logger;

    public SolutionPageService(IStorageService storage, ILogger<SolutionPageService>? logger = null)
    {
        _storage = storage;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SolutionPageService>.Instance;
    }

    public async Task<SolutionPageMetadata> CreateSolutionPageAsync(
        Guid sessionId,
        CreateSolutionPageServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var sessionLock = GetSessionLock(sessionId);
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(sessionId, cancellationToken);
            var requestedOrder = request.OrderIndex.GetValueOrDefault(0);
            var nextOrder = manifest.Items.Count == 0 ? 1 : manifest.Items.Max(i => i.OrderIndex) + 1;
            var orderIndex = requestedOrder > 0 ? requestedOrder : nextOrder;

            var solutionPageId = ResolveNextSolutionId(manifest, orderIndex);
            var now = DateTime.UtcNow;
            var metadata = new SolutionPageMetadata
            {
                SessionId = sessionId,
                SolutionPageId = solutionPageId,
                OrderIndex = orderIndex,
                Kind = string.Equals(request.Kind, "currentSlide", StringComparison.OrdinalIgnoreCase)
                    ? "currentSlide"
                    : "blank",
                SourceSlideIndex = request.SourceSlideIndex,
                StoragePath = BuildStorageArtifactPath(sessionId, solutionPageId),
                CreatedAt = now,
                UpdatedAt = now,
                HasInk = false
            };

            manifest.Items.RemoveAll(i => string.Equals(i.SolutionPageId, metadata.SolutionPageId, StringComparison.OrdinalIgnoreCase));
            manifest.Items.Add(metadata);
            manifest.Items = manifest.Items
                .OrderBy(i => i.OrderIndex)
                .ThenBy(i => i.CreatedAt)
                .ToList();

            await SaveManifestAsync(sessionId, manifest, cancellationToken);
            return metadata;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<SolutionPageMetadata?> UpdateSolutionArtifactAsync(
        Guid sessionId,
        string solutionPageId,
        byte[] pngBytes,
        bool hasInk,
        CreateSolutionPageServiceRequest? upsertRequest = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionPageId) || pngBytes == null || pngBytes.Length == 0)
            return null;

        var sessionLock = GetSessionLock(sessionId);
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(sessionId, cancellationToken);
            var metadata = manifest.Items.FirstOrDefault(i =>
                string.Equals(i.SolutionPageId, solutionPageId, StringComparison.OrdinalIgnoreCase));

            if (metadata == null)
            {
                var now = DateTime.UtcNow;
                var requestedOrder = upsertRequest?.OrderIndex.GetValueOrDefault(0) ?? 0;
                var nextOrder = manifest.Items.Count == 0 ? 1 : manifest.Items.Max(i => i.OrderIndex) + 1;
                var resolvedOrder = requestedOrder > 0 ? requestedOrder : nextOrder;

                metadata = new SolutionPageMetadata
                {
                    SessionId = sessionId,
                    SolutionPageId = solutionPageId,
                    OrderIndex = resolvedOrder,
                    Kind = string.Equals(upsertRequest?.Kind, "currentSlide", StringComparison.OrdinalIgnoreCase)
                        ? "currentSlide"
                        : "blank",
                    SourceSlideIndex = upsertRequest?.SourceSlideIndex,
                    StoragePath = BuildStorageArtifactPath(sessionId, solutionPageId),
                    CreatedAt = now,
                    UpdatedAt = now,
                    HasInk = false
                };

                manifest.Items.Add(metadata);
            }

            var localArtifactPath = GetLocalArtifactPath(sessionId, metadata.SolutionPageId);
            var artifactDir = Path.GetDirectoryName(localArtifactPath);
            if (!string.IsNullOrEmpty(artifactDir))
                Directory.CreateDirectory(artifactDir);

            await File.WriteAllBytesAsync(localArtifactPath, pngBytes, cancellationToken);

            await using (var stream = new MemoryStream(pngBytes, writable: false))
            {
                await _storage.UploadToBucketAsync(stream, "uploads", metadata.StoragePath, "image/png");
            }

            metadata.HasInk = hasInk;
            metadata.UpdatedAt = DateTime.UtcNow;
            manifest.Items = manifest.Items
                .OrderBy(i => i.OrderIndex)
                .ThenBy(i => i.CreatedAt)
                .ToList();
            await SaveManifestAsync(sessionId, manifest, cancellationToken);

            return metadata;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<IReadOnlyList<SolutionPageMetadata>> ListSolutionPagesAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var sessionLock = GetSessionLock(sessionId);
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestAsync(sessionId, cancellationToken);
            return manifest.Items
                .OrderBy(i => i.OrderIndex)
                .ThenBy(i => i.CreatedAt)
                .Select(CloneMetadata)
                .ToList();
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<SolutionPageMetadata?> GetSolutionPageAsync(
        Guid sessionId,
        string solutionPageId,
        CancellationToken cancellationToken = default)
    {
        var items = await ListSolutionPagesAsync(sessionId, cancellationToken);
        return items.FirstOrDefault(i =>
            string.Equals(i.SolutionPageId, solutionPageId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string?> TryGetSignedImageUrlAsync(SolutionPageMetadata metadata, int expirySeconds = 300)
    {
        try
        {
            return await _storage.GetSignedUrlAsync(metadata.StoragePath, expirySeconds);
        }
        catch
        {
            return null;
        }
    }

    public async Task<SolutionAppendixResult> GenerateSolutionsAppendixAsync(
        Guid sessionId,
        bool skipEmptyPages = true,
        CancellationToken cancellationToken = default)
    {
        var allPages = await ListSolutionPagesAsync(sessionId, cancellationToken);
        var pages = skipEmptyPages
            ? allPages.Where(p => p.HasInk).ToList()
            : allPages.ToList();

        if (pages.Count == 0)
        {
            _logger.LogInformation(
                "[SolutionPageService] No solution pages with ink found for session {SessionId}; skipping appendix bake.",
                sessionId);
            return new SolutionAppendixResult { Generated = false, PageCount = 0 };
        }

        var imagePaths = pages
            .Select(p => GetLocalArtifactPath(sessionId, p.SolutionPageId))
            .Where(File.Exists)
            .ToList();

        if (imagePaths.Count == 0)
        {
            _logger.LogWarning(
                "[SolutionPageService] No local solution PNGs found for session {SessionId}; expectedPages={ExpectedPages}.",
                sessionId,
                pages.Count);
            return new SolutionAppendixResult { Generated = false, PageCount = 0 };
        }

        var localExportPath = GetLocalAppendixPath(sessionId);
        var exportDir = Path.GetDirectoryName(localExportPath);
        if (!string.IsNullOrEmpty(exportDir))
            Directory.CreateDirectory(exportDir);

        var tempDir = Path.Combine(Path.GetTempPath(), "solution-appendix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, "solutions_appendix.pdf");

        try
        {
            _logger.LogInformation(
                "[SolutionPageService] Appendix bake start for session {SessionId}: outputPath={OutputPath} pageCount={PageCount}",
                sessionId,
                localExportPath,
                imagePaths.Count);

            BuildPdfFromImages(sessionId, imagePaths, tempPath);
            var pdfBytes = await File.ReadAllBytesAsync(tempPath, cancellationToken);
            await File.WriteAllBytesAsync(localExportPath, pdfBytes, cancellationToken);

            var storagePath = BuildStorageAppendixPath(sessionId);
            await using (var stream = new MemoryStream(pdfBytes, writable: false))
            {
                await _storage.UploadToBucketAsync(stream, "uploads", storagePath, "application/pdf");
            }

            _logger.LogInformation(
                "[SolutionPageService] Appendix upload success for session {SessionId}: storagePath={StoragePath}",
                sessionId,
                storagePath);

            return new SolutionAppendixResult
            {
                Generated = true,
                PageCount = imagePaths.Count,
                LocalPath = localExportPath,
                StoragePath = storagePath,
                PdfBytes = pdfBytes
            };
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { }
        }
    }

    public async Task<byte[]> AppendSolutionsToDeckPdfAsync(
        Guid sessionId,
        byte[] baseDeckPdfBytes,
        bool skipEmptyPages = true,
        CancellationToken cancellationToken = default)
    {
        if (baseDeckPdfBytes == null || baseDeckPdfBytes.Length == 0)
            return Array.Empty<byte>();

        var appendix = await GenerateSolutionsAppendixAsync(sessionId, skipEmptyPages, cancellationToken);
        if (!appendix.Generated || appendix.PdfBytes == null || appendix.PdfBytes.Length == 0)
        {
            _logger.LogInformation(
                "[SolutionPageService] No solution appendix available for session {SessionId}; returning base deck bytes.",
                sessionId);
            return baseDeckPdfBytes;
        }

        using var output = new MemoryStream();
        using (var mergedDoc = new PdfDocument(new PdfWriter(output)))
        {
            using (var baseDoc = new PdfDocument(new PdfReader(new MemoryStream(baseDeckPdfBytes))))
            {
                baseDoc.CopyPagesTo(1, baseDoc.GetNumberOfPages(), mergedDoc);
            }

            using (var appendixDoc = new PdfDocument(new PdfReader(new MemoryStream(appendix.PdfBytes))))
            {
                appendixDoc.CopyPagesTo(1, appendixDoc.GetNumberOfPages(), mergedDoc);
            }
        }

        return output.ToArray();
    }

    private static SemaphoreSlim GetSessionLock(Guid sessionId)
    {
        var key = sessionId.ToString("D");
        var entry = SessionLocks.GetOrAdd(key, _ => new SessionLockEntry());
        entry.Touch();
        return entry.Semaphore;
    }

    private static void EvictIdleLocks(object? state)
    {
        var cutoffTicks = (DateTime.UtcNow - LockIdleEvictionAge).Ticks;
        foreach (var kvp in SessionLocks)
        {
            if (kvp.Value.ReadLastUsedTicks() >= cutoffTicks)
                continue;

            // Atomically remove only this exact entry so we never remove a freshly-added replacement.
            if (!SessionLocks.TryRemove(kvp))
                continue;

            // If the semaphore is held by someone who grabbed it before eviction, re-insert the
            // entry so the holder still observes the same lock object for future callers.
            if (kvp.Value.Semaphore.CurrentCount != 1)
                SessionLocks.TryAdd(kvp.Key, kvp.Value);
        }
    }

    private async Task<SolutionManifest> LoadManifestAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var manifestPath = GetManifestPath(sessionId);
        if (File.Exists(manifestPath))
        {
            try
            {
                await using var localStream = File.OpenRead(manifestPath);
                var localManifest = await JsonSerializer.DeserializeAsync<SolutionManifest>(localStream, ManifestJsonOptions, cancellationToken);
                if (localManifest != null)
                {
                    return localManifest;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[SolutionPageService] Failed to read local solution manifest session={SessionId} path={ManifestPath}",
                    sessionId,
                    manifestPath);
            }
        }

        var storageManifest = await TryLoadManifestFromStorageAsync(sessionId, cancellationToken);
        if (storageManifest != null)
        {
            await TryWriteManifestToLocalAsync(sessionId, storageManifest, cancellationToken);
            return storageManifest;
        }

        return new SolutionManifest { SessionId = sessionId.ToString("D") };
    }

    private async Task SaveManifestAsync(Guid sessionId, SolutionManifest manifest, CancellationToken cancellationToken)
    {
        var manifestPath = GetManifestPath(sessionId);
        var dir = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
        await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken);

        var storagePath = BuildStorageManifestPath(sessionId);
        try
        {
            await using var uploadStream = new MemoryStream(manifestBytes, writable: false);
            await _storage.UploadToBucketAsync(uploadStream, ManifestBucket, storagePath, ManifestContentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[SolutionPageService] Failed to upload solution manifest session={SessionId} storagePath={StoragePath}",
                sessionId,
                storagePath);
        }
    }

    private async Task<SolutionManifest?> TryLoadManifestFromStorageAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var storagePath = BuildStorageManifestPath(sessionId);

        try
        {
            var signedUrl = await _storage.GetSignedUrlAsync(storagePath, expirySeconds: 60);
            if (string.IsNullOrWhiteSpace(signedUrl))
                return null;

            using var client = new HttpClient();
            using var response = await client.GetAsync(signedUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "[SolutionPageService] Solution manifest not available in storage session={SessionId} storagePath={StoragePath} statusCode={StatusCode}",
                    sessionId,
                    storagePath,
                    response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var manifest = await JsonSerializer.DeserializeAsync<SolutionManifest>(stream, ManifestJsonOptions, cancellationToken);
            if (manifest == null)
            {
                return null;
            }

            _logger.LogInformation(
                "[SolutionPageService] Restored solution manifest from storage session={SessionId} storagePath={StoragePath}",
                sessionId,
                storagePath);

            return manifest;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[SolutionPageService] Failed to restore solution manifest from storage session={SessionId} storagePath={StoragePath}",
                sessionId,
                storagePath);
            return null;
        }
    }

    private static async Task TryWriteManifestToLocalAsync(Guid sessionId, SolutionManifest manifest, CancellationToken cancellationToken)
    {
        try
        {
            var manifestPath = GetManifestPath(sessionId);
            var dir = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken);
        }
        catch
        {
            // Local write is best-effort only; storage copy remains the source of truth.
        }
    }

    private static string ResolveNextSolutionId(SolutionManifest manifest, int requestedOrder)
    {
        var order = Math.Max(1, requestedOrder);
        var candidate = BuildSolutionId(order);

        while (manifest.Items.Any(i => string.Equals(i.SolutionPageId, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            order++;
            candidate = BuildSolutionId(order);
        }

        return candidate;
    }

    private static string BuildSolutionId(int orderIndex)
    {
        return "solution-" + orderIndex.ToString("D3");
    }

    private static SolutionPageMetadata CloneMetadata(SolutionPageMetadata source)
    {
        return new SolutionPageMetadata
        {
            SessionId = source.SessionId,
            SolutionPageId = source.SolutionPageId,
            OrderIndex = source.OrderIndex,
            Kind = source.Kind,
            SourceSlideIndex = source.SourceSlideIndex,
            StoragePath = source.StoragePath,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            HasInk = source.HasInk
        };
    }

    private static string BuildStorageArtifactPath(Guid sessionId, string solutionPageId)
    {
        return $"{sessionId:D}/solutions/{solutionPageId}.png";
    }

    private static string BuildStorageManifestPath(Guid sessionId)
    {
        return $"{sessionId:D}/solutions/metadata.json";
    }

    private static string BuildStorageAppendixPath(Guid sessionId)
    {
        return $"{sessionId:D}/exports/solutions_appendix.pdf";
    }

    private static string GetSessionRoot(Guid sessionId)
    {
        return Path.Combine(AppContext.BaseDirectory, "uploads", sessionId.ToString("D"));
    }

    private static string GetManifestPath(Guid sessionId)
    {
        return Path.Combine(GetSessionRoot(sessionId), "solutions", "metadata.json");
    }

    private static string GetLocalArtifactPath(Guid sessionId, string solutionPageId)
    {
        return Path.Combine(GetSessionRoot(sessionId), "solutions", solutionPageId + ".png");
    }

    private static string GetLocalAppendixPath(Guid sessionId)
    {
        return Path.Combine(GetSessionRoot(sessionId), "exports", "solutions_appendix.pdf");
    }

    private void BuildPdfFromImages(Guid sessionId, IReadOnlyList<string> imagePaths, string outputPath)
    {
        using var writer = new PdfWriter(outputPath);
        using var pdfDoc = new PdfDocument(writer);
        using var document = new Document(pdfDoc);

        for (var index = 0; index < imagePaths.Count; index++)
        {
            var imagePath = imagePaths[index];
            _logger.LogInformation(
                "[SolutionPageService] Appendix page bake start session={SessionId} page={PageNumber} path={ImagePath}",
                sessionId,
                index + 1,
                imagePath);

            var imageData = ImageDataFactory.Create(imagePath);
            var pageSize = new PdfPageSize(imageData.GetWidth(), imageData.GetHeight());
            var page = pdfDoc.AddNewPage(pageSize);
            var image = new Image(imageData);
            image.ScaleAbsolute(pageSize.GetWidth(), pageSize.GetHeight());
            image.SetFixedPosition(pdfDoc.GetPageNumber(page), 0, 0);
            document.Add(image);

            _logger.LogInformation(
                "[SolutionPageService] Appendix page bake end session={SessionId} page={PageNumber}",
                sessionId,
                index + 1);
        }
    }
}
