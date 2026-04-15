using BackendServer.Features.Materials.Services;
using BackendServer.Shared.Services;
using Microsoft.AspNetCore.SignalR;
using BackendServer.Features.Classroom.Hubs;

namespace BackendServer.Features.Classroom.Services;

public class ArtifactBakingWorker : BackgroundService
{
    private readonly IArtifactBakingQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ArtifactBakingWorker> _logger;
    private readonly IHubContext<PresentationHub> _hubContext;

    public ArtifactBakingWorker(
        IArtifactBakingQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ArtifactBakingWorker> logger,
        IHubContext<PresentationHub> hubContext)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // verify that the hosted service started
        _logger.LogWarning("🚀🚀🚀 ArtifactBakingWorker IS ALIVE AND LISTENING FOR JOBS! 🚀🚀🚀");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _queue.DequeueAsync(stoppingToken);
                _logger.LogWarning("🚀🚀🚀 ArtifactBakingWorker PICKED UP A JOB FOR SESSION {SessionId}! 🚀🚀🚀", job.SessionId);
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔥 [BAKING] ❌ Failed to process bake job.");
            }
        }

        _logger.LogWarning("🚀🚀🚀 ArtifactBakingWorker SHUTTING DOWN 🚀🚀🚀");
    }

    private async Task ProcessJobAsync(BakeJob job, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var pdfService = scope.ServiceProvider.GetRequiredService<PdfService>();
        var pptxService = scope.ServiceProvider.GetRequiredService<PptxService>();
        var storage = scope.ServiceProvider.GetRequiredService<IStorageService>();
        var inkArtifactExporter = scope.ServiceProvider.GetRequiredService<IInkArtifactExportService>();
        var solutionPageService = scope.ServiceProvider.GetRequiredService<ISolutionPageService>();

        var sessionIdStr = job.SessionId.ToString("D");
        var courseIdStr = job.CourseId.ToString("D");
        var legacyIdStr = job.SessionId.ToString("N");

        _logger.LogInformation("🔥 [BAKING] Started for session {SessionId}", sessionIdStr);

        // --- BAKE PDF ---
        _logger.LogInformation("🔥 [BAKING] Generating annotated PDF...");
        var annotatedPdfBytes = await pdfService.GetAnnotatedPdfBytesAsync(legacyIdStr);
        if (annotatedPdfBytes != null && annotatedPdfBytes.Length > 0)
        {
            var finalAnnotatedPdfBytes = await solutionPageService.AppendSolutionsToDeckPdfAsync(
                job.SessionId,
                annotatedPdfBytes,
                skipEmptyPages: true,
                stoppingToken);

            await storage.UploadToBucketAsync(
                new MemoryStream(annotatedPdfBytes),
                "slides",
                $"{courseIdStr}/{sessionIdStr}/inked.pdf",
                "application/pdf"
            );
            _logger.LogInformation("🔥 [BAKING] ✅ Base inked PDF uploaded to slides/{CourseId}/{SessionId}/inked.pdf.", courseIdStr, sessionIdStr);

            await storage.UploadToBucketAsync(
                new MemoryStream(finalAnnotatedPdfBytes),
                "slides",
                $"{courseIdStr}/{sessionIdStr}/annotated.pdf",
                "application/pdf"
            );
            _logger.LogInformation("🔥 [BAKING] ✅ Annotated PDF uploaded to slides/{CourseId}/{SessionId}/annotated.pdf.", courseIdStr, sessionIdStr);

            if (finalAnnotatedPdfBytes.Length > 0
                && !ReferenceEquals(finalAnnotatedPdfBytes, annotatedPdfBytes))
            {
                await storage.UploadToBucketAsync(
                    new MemoryStream(finalAnnotatedPdfBytes),
                    "slides",
                    $"{courseIdStr}/{sessionIdStr}/inked-with-solutions.pdf",
                    "application/pdf"
                );

                _logger.LogInformation(
                    "🔥 [BAKING] ✅ Solution-inclusive inked PDF uploaded to slides/{CourseId}/{SessionId}/inked-with-solutions.pdf.",
                    courseIdStr,
                    sessionIdStr);
            }
            else
            {
                _logger.LogInformation(
                    "🔥 [BAKING] No solution appendix artifacts found for session {SessionId}; annotated.pdf remains the ink-only merge.",
                    sessionIdStr);
            }
        }
        else
        {
            _logger.LogWarning("🔥 [BAKING] ⚠️ PDF generation returned null.");
        }

        // --- BAKE PPTX ---
        _logger.LogInformation("🔥 [BAKING] Generating Inked PPTX...");
        var pptxBytes = await pptxService.GetAnnotatedPptxBytesAsync(legacyIdStr);
        if (pptxBytes != null && pptxBytes.Length > 0)
        {
            await storage.UploadToBucketAsync(
                new MemoryStream(pptxBytes),
                "presentations",
                $"{courseIdStr}/{sessionIdStr}/inked.pptx",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation"
            );
            _logger.LogInformation("🔥 [BAKING] ✅ PPTX uploaded to Supabase.");
        }
        else
        {
            _logger.LogWarning("🔥 [BAKING] ⚠️ PPTX generation returned null.");
        }

        // --- BAKE STANDALONE INK ARTIFACT PDF ---
        _logger.LogInformation("🔥 [BAKING] Generating standalone ink artifact PDF...");
        try
        {
            var artifactResult = await inkArtifactExporter.GenerateAndUploadAsync(job.SessionId, stoppingToken);
            if (artifactResult.Generated)
                _logger.LogInformation("🔥 [BAKING] ✅ Ink artifact PDF uploaded to {Bucket}/{Path} ({Pages} pages).", artifactResult.StorageBucket, artifactResult.StoragePath, artifactResult.PageCount);
            else
                _logger.LogInformation("🔥 [BAKING] Ink artifact export skipped (no ink snapshots found).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "🔥 [BAKING] Failed to generate/upload standalone ink artifact PDF");
        }

        // notify clients that baked assets are available
        try
        {
            await _hubContext.Clients.Group(sessionIdStr)
                .SendAsync("BakeCompleted", new { sessionId = sessionIdStr });
            _logger.LogInformation("🔥 [BAKING] Signaled BakeCompleted for session {SessionId}", sessionIdStr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "🔥 [BAKING] Failed to send BakeCompleted event");
        }
    }
}