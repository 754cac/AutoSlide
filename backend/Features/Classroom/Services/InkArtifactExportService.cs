using System.Text.RegularExpressions;
using BackendServer.Shared.Services;
using iText.IO.Image;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using iText.Layout.Element;
using PdfPageSize = iText.Kernel.Geom.PageSize;
using IOPath = System.IO.Path;

namespace BackendServer.Features.Classroom.Services;

public sealed class InkArtifactExportResult
{
    public bool Generated { get; init; }
    public int PageCount { get; init; }
    public string? LocalPath { get; init; }
    public string? StorageBucket { get; init; }
    public string? StoragePath { get; init; }
}

public interface IInkArtifactExportService
{
    Task<InkArtifactExportResult> GenerateAndUploadAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public class InkArtifactExportService : IInkArtifactExportService
{
    private static readonly Regex SlideRegex = new(@"^slide_(?<index>\d+)\.png$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record IndexedArtifact(int Index, string Path, DateTime LastWriteUtc);

    private sealed record ArtifactResolution(
        IReadOnlyList<string> OrderedArtifactPaths,
        int SlideTargetCount,
        int ExpectedSolutionTargetCount,
        int ResolvedSolutionTargetCount,
        int MissingSolutionTargetCount);

    private readonly IStorageService _storage;
    private readonly ISolutionPageService _solutionPageService;
    private readonly ILogger<InkArtifactExportService> _logger;

    public InkArtifactExportService(
        IStorageService storage,
        ISolutionPageService solutionPageService,
        ILogger<InkArtifactExportService> logger)
    {
        _storage = storage;
        _solutionPageService = solutionPageService;
        _logger = logger;
    }

    public async Task<InkArtifactExportResult> GenerateAndUploadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var sessionIdD = sessionId.ToString("D");
        var sessionIdN = sessionId.ToString("N");

        var uploadsRoot = IOPath.Combine(AppContext.BaseDirectory, "uploads");
        var candidateArtifactDirs = new[]
        {
            IOPath.Combine(uploadsRoot, sessionIdD, "ink-artifacts"),
            IOPath.Combine(uploadsRoot, sessionIdN, "ink-artifacts")
        };

        var artifactResolution = await ResolveOrderedArtifactsAsync(
            sessionId,
            candidateArtifactDirs,
            uploadsRoot,
            cancellationToken);

        if (artifactResolution.OrderedArtifactPaths.Count == 0)
        {
            _logger.LogInformation(
                "[InkArtifactExport] No artifact targets found for session {SessionId}. slideTargets={SlideTargets} expectedSolutionTargets={ExpectedSolutionTargets} missingSolutionTargets={MissingSolutionTargets}",
                sessionIdD,
                artifactResolution.SlideTargetCount,
                artifactResolution.ExpectedSolutionTargetCount,
                artifactResolution.MissingSolutionTargetCount);
            return new InkArtifactExportResult { Generated = false, PageCount = 0 };
        }

        _logger.LogInformation(
            "[InkArtifactExport] Resolved export targets for session {SessionId}: slideTargets={SlideTargets}, expectedSolutionTargets={ExpectedSolutionTargets}, resolvedSolutionTargets={ResolvedSolutionTargets}, missingSolutionTargets={MissingSolutionTargets}, totalPages={TotalPages}",
            sessionIdD,
            artifactResolution.SlideTargetCount,
            artifactResolution.ExpectedSolutionTargetCount,
            artifactResolution.ResolvedSolutionTargetCount,
            artifactResolution.MissingSolutionTargetCount,
            artifactResolution.OrderedArtifactPaths.Count);

        var localExportDir = IOPath.Combine(uploadsRoot, sessionIdD, "exports");
        Directory.CreateDirectory(localExportDir);
        var localPdfPath = IOPath.Combine(localExportDir, "ink_only_artifact.pdf");

        var tempDir = IOPath.Combine(IOPath.GetTempPath(), "ink-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempPdfPath = IOPath.Combine(tempDir, "ink_only_artifact.pdf");

        try
        {
            BuildStandalonePdf(artifactResolution.OrderedArtifactPaths, tempPdfPath);
            System.IO.File.Copy(tempPdfPath, localPdfPath, overwrite: true);

            var storageBucket = "uploads";
            var storagePath = $"{sessionIdD}/exports/ink_only_artifact.pdf";

            await using (var stream = System.IO.File.OpenRead(tempPdfPath))
            {
                await _storage.UploadToBucketAsync(stream, storageBucket, storagePath, "application/pdf");
            }

            _logger.LogInformation(
                "[InkArtifactExport] Uploaded standalone ink artifact PDF for session {SessionId}: {StorageBucket}/{StoragePath} ({PageCount} pages)",
                sessionIdD,
                storageBucket,
                storagePath,
                artifactResolution.OrderedArtifactPaths.Count);

            return new InkArtifactExportResult
            {
                Generated = true,
                PageCount = artifactResolution.OrderedArtifactPaths.Count,
                LocalPath = localPdfPath,
                StorageBucket = storageBucket,
                StoragePath = storagePath
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InkArtifactExport] Failed to clean temp staging directory {TempDir}", tempDir);
            }
        }
    }

    private async Task<ArtifactResolution> ResolveOrderedArtifactsAsync(
        Guid sessionId,
        IEnumerable<string> candidateDirs,
        string uploadsRoot,
        CancellationToken cancellationToken)
    {
        var allSlideArtifacts = candidateDirs
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.GetFiles(dir, "*.png"))
            .ToList();

        var slideTargets = CollectOrderedByIndex(allSlideArtifacts, SlideRegex);
        var orderedArtifactPaths = slideTargets
            .OrderBy(target => target.Index)
            .Select(target => target.Path)
            .ToList();

        IReadOnlyList<SolutionPageMetadata> solutionPages;
        try
        {
            solutionPages = await _solutionPageService.ListSolutionPagesAsync(sessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[InkArtifactExport] Failed to enumerate solution page metadata for session {SessionId}. Proceeding with slide artifacts only.",
                sessionId.ToString("D"));

            return new ArtifactResolution(
                orderedArtifactPaths,
                slideTargets.Count,
                ExpectedSolutionTargetCount: 0,
                ResolvedSolutionTargetCount: 0,
                MissingSolutionTargetCount: 0);
        }

        var expectedSolutionTargets = solutionPages
            .Where(page => page.HasInk)
            .OrderBy(page => page.OrderIndex)
            .ThenBy(page => page.CreatedAt)
            .ThenBy(page => page.SolutionPageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resolvedSolutionTargetCount = 0;
        var missingSolutionTargetCount = 0;

        foreach (var solutionPage in expectedSolutionTargets)
        {
            var localPath = ResolveSolutionArtifactLocalPath(uploadsRoot, sessionId, solutionPage.SolutionPageId);
            if (string.IsNullOrWhiteSpace(localPath))
            {
                missingSolutionTargetCount++;
                _logger.LogWarning(
                    "[InkArtifactExport] Missing local solution artifact for session {SessionId}: solutionPageId={SolutionPageId} storagePath={StoragePath}",
                    sessionId.ToString("D"),
                    solutionPage.SolutionPageId,
                    solutionPage.StoragePath);
                continue;
            }

            orderedArtifactPaths.Add(localPath);
            resolvedSolutionTargetCount++;
        }

        return new ArtifactResolution(
            orderedArtifactPaths,
            slideTargets.Count,
            expectedSolutionTargets.Count,
            resolvedSolutionTargetCount,
            missingSolutionTargetCount);
    }

    private static string? ResolveSolutionArtifactLocalPath(string uploadsRoot, Guid sessionId, string solutionPageId)
    {
        if (string.IsNullOrWhiteSpace(solutionPageId))
            return null;

        var normalizedSolutionPageId = solutionPageId.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? solutionPageId[..^4]
            : solutionPageId;

        var candidates = new[]
        {
            IOPath.Combine(uploadsRoot, sessionId.ToString("D"), "solutions", normalizedSolutionPageId + ".png"),
            IOPath.Combine(uploadsRoot, sessionId.ToString("N"), "solutions", normalizedSolutionPageId + ".png")
        };

        foreach (var candidate in candidates)
        {
            if (System.IO.File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static List<IndexedArtifact> CollectOrderedByIndex(IEnumerable<string> paths, Regex regex)
    {
        var selected = new Dictionary<int, (string Path, DateTime LastWriteUtc)>();

        foreach (var path in paths)
        {
            var fileName = IOPath.GetFileName(path);
            var match = regex.Match(fileName);
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups["index"].Value, out var index) || index <= 0)
                continue;

            var stamp = System.IO.File.GetLastWriteTimeUtc(path);
            if (!selected.TryGetValue(index, out var existing) || stamp >= existing.LastWriteUtc)
                selected[index] = (path, stamp);
        }

        return selected
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new IndexedArtifact(kvp.Key, kvp.Value.Path, kvp.Value.LastWriteUtc))
            .ToList();
    }

    private static void BuildStandalonePdf(IReadOnlyList<string> orderedInkArtifacts, string outputPdfPath)
    {
        using var writer = new PdfWriter(outputPdfPath);
        using var pdf = new PdfDocument(writer);

        foreach (var imagePath in orderedInkArtifacts)
        {
            var imageData = ImageDataFactory.Create(imagePath);
            var pageSize = new PdfPageSize(imageData.GetWidth(), imageData.GetHeight());
            var page = pdf.AddNewPage(pageSize);

            using var canvas = new Canvas(new PdfCanvas(page), pageSize);
            var image = new Image(imageData)
                .ScaleAbsolute(pageSize.GetWidth(), pageSize.GetHeight())
                .SetFixedPosition(0, 0);

            canvas.Add(image);
        }
    }
}
