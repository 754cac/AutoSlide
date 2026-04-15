using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using BackendServer.Configuration;
using BackendServer.Features.Classroom;
using BackendServer.Features.Classroom.Hubs;
using BackendServer.Shared.Data;
using BackendServer.Shared.Data.Entities;
using Microsoft.AspNetCore.Http;

namespace BackendServer.Features.Materials.Services;

/// <summary>
/// Handles the full PPTX upload pipeline: file save, DB session creation,
/// PDF conversion via LibreOffice, and Supabase secure storage.
/// Extracted from the /api/upload minimal-API handler in Program.cs.
/// </summary>
public class UploadService
{
    private readonly PresentationStore _store;
    private readonly AppDbContext _db;
    private readonly IHubContext<PresentationHub> _hub;
    private readonly SlideSplitterService _slideSplitter;
    private readonly ILogger<UploadService> _logger;
    // Pre-wired for Approach 1 animation keyframe upload (feat/animation-approach1-keyframes)
    private readonly SupabaseOptions _supabaseOptions;

    private const long MaxAllowedBytes = 190 * 1024 * 1024; // 190 MB

    public UploadService(
        PresentationStore store,
        AppDbContext db,
        IHubContext<PresentationHub> hub,
        SlideSplitterService slideSplitter,
        ILogger<UploadService> logger,
        IOptions<SupabaseOptions> supabaseOptions)
    {
        _store = store;
        _db = db;
        _hub = hub;
        _slideSplitter = slideSplitter;
        _logger = logger;
        _supabaseOptions = supabaseOptions.Value;
    }

    public async Task<IResult> HandleUploadAsync(HttpRequest req)
    {
        if (!req.HasFormContentType)
            return Results.BadRequest(new { error = "multipart/form-data required" });

        try
        {
            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "file required" });

            _logger.LogInformation("upload attempt: filename={fileName} size={size}", file.FileName, file.Length);

            if (file.Length > MaxAllowedBytes)
            {
                _logger.LogWarning("upload rejected: file too large ({size} bytes)", file.Length);
                return Results.StatusCode(413); // Payload Too Large
            }

            var name = req.Query["name"].ToString().Length > 0
                ? req.Query["name"].ToString()
                : Path.GetFileName(file.FileName);
            var totalSlidesQ = req.Query["totalSlides"].ToString();
            int totalSlides = 0;
            if (!string.IsNullOrEmpty(totalSlidesQ)) int.TryParse(totalSlidesQ, out totalSlides);
            var frameMapQ = req.Query["frameMap"].ToString();

            // Optional CourseId for linking to DB
            var courseIdQ = req.Query["courseId"].ToString();
            Guid? courseId = null;
            if (Guid.TryParse(courseIdQ, out var cid)) courseId = cid;

            var uploads = Path.Combine(AppContext.BaseDirectory, "uploads");
            Directory.CreateDirectory(uploads);
            var sessionGuid = Guid.NewGuid();
            var id = sessionGuid.ToString("n");
            var filePath = Path.Combine(uploads, id + Path.GetExtension(file.FileName));
            await using (var fs = File.Create(filePath))
            {
                await file.CopyToAsync(fs);
            }

            // Optional flattened PDF payload (from the add-in)
            var flatPdf = form.Files.GetFile("flatPdf");
            var convertedPdfPath = Path.Combine(uploads, id + ".pdf");
            var usedFlatPdf = false;
            if (flatPdf != null && flatPdf.Length > 0)
            {
                // Enforce an explicit size limit for the flattened PDF to avoid bypassing MaxAllowedBytes
                const long MaxFlatPdfBytes = 50L * 1024 * 1024; // 50 MB limit for flattened PDFs
                if (flatPdf.Length > MaxFlatPdfBytes)
                {
                    _logger.LogWarning("Flattened PDF too large ({length} bytes) for presentation {id}", flatPdf.Length, id);
                    throw new BadHttpRequestException("Flattened PDF payload too large.", StatusCodes.Status413PayloadTooLarge);
                }

                await using (var fs = File.Create(convertedPdfPath))
                {
                    await flatPdf.CopyToAsync(fs);
                }
                usedFlatPdf = true;
                _logger.LogInformation("Received flattened PDF for presentation {id}", id);
            }

            // Prefer frame map from multipart form field; fall back to query-string parameter for backwards compatibility
            var frameMapJson = form["frameMap"].ToString();
            if (string.IsNullOrWhiteSpace(frameMapJson))
            {
                frameMapJson = frameMapQ;
            }

            if (!string.IsNullOrWhiteSpace(frameMapJson))
            {
                try
                {
                    var frameMap = JsonSerializer.Deserialize<Dictionary<int, List<int>>>(frameMapJson);
                    if (frameMap != null && frameMap.Count > 0)
                    {
                        var frameMapPath = Path.Combine(uploads, id + ".framemap.json");
                        var normalized = NormalizeFrameMap(frameMap);
                        var payload = JsonSerializer.Serialize(normalized);
                        await File.WriteAllTextAsync(frameMapPath, payload);
                        _logger.LogInformation("Persisted frame map for presentation {id} with {count} slides", id, normalized.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Invalid frameMap metadata for presentation {id}; continuing without mapping", id);
                }
            }

            var presenterToken = _store.RegisterPresentation(id, name, totalSlides, filePath);
            var sessionId = _store.GetPresentationInfo(id)?.SessionId;

            // ATOMIC SQL TRANSACTION: Link session to course + persist file
            if (courseId.HasValue)
            {
                using var transaction = await _db.Database.BeginTransactionAsync();
                try
                {
                    var session = new Session
                    {
                        Id = sessionGuid,
                        PresentationTitle = name,
                        SlideCount = totalSlides,
                        TotalSlides = totalSlides,
                        CourseId = courseId.Value,
                        PresenterToken = presenterToken.Token,
                        CreatedAt = DateTime.UtcNow,
                        Status = SessionStatus.Active,
                        StartedAt = DateTime.UtcNow
                    };
                    _db.Sessions.Add(session);

                    // Update Course.ActiveSessionId
                    var course = await _db.Courses.FindAsync(courseId.Value);
                    if (course != null)
                    {
                        course.ActiveSessionId = sessionGuid;
                    }

                    await _db.SaveChangesAsync();

                    // Seed slide 1 as unlocked — presenter starts on it before first transition
                    await _db.Database.ExecuteSqlRawAsync(@"
                        INSERT INTO session_unlocked_slides (""SessionId"", ""SlideIndex"", ""UnlockedAt"")
                        VALUES ({0}, 1, (NOW() AT TIME ZONE 'Asia/Hong_Kong'))
                        ON CONFLICT DO NOTHING", sessionGuid);

                    await transaction.CommitAsync();

                    // Broadcast to course group that a session started
                    try
                    {
                        if (course != null && sessionId != null)
                        {
                            await _hub.Clients.Group($"Course_{course.Id}")
                                .SendAsync("SessionStarted", new { courseId = course.Id, sessionId = sessionId, presentationTitle = session.PresentationTitle });
                        }
                    }
                    catch { /* best-effort notify */ }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Transaction failed: could not sync session to SQL DB. Rolling back upload.");
                    await transaction.RollbackAsync();

                    // Rollback file write
                    try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                    return Results.StatusCode(500);
                }
            }

            // Attempt to convert PPT/PPTX -> PDF using LibreOffice if available
            // If a flattened PDF was uploaded (flatPdf), use it directly instead of converting.
            if (!usedFlatPdf)
            {
                try
                {
                    var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
                    if (ext == ".ppt" || ext == ".pptx")
                    {
                        var converted = await ConvertPptToPdfAsync(filePath, uploads, _logger);
                        if (converted && File.Exists(convertedPdfPath))
                        {
                            LogEvent(_logger, "convert", id, _store.GetPresentationInfo(id)?.SessionId, new { result = "ok", pdf = Path.GetFileName(convertedPdfPath) });
                        }
                        else
                        {
                            LogEvent(_logger, "convert", id, _store.GetPresentationInfo(id)?.SessionId, new { result = "failed" });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PPT->PDF conversion attempt failed");
                    LogEvent(_logger, "convert", id, _store.GetPresentationInfo(id)?.SessionId, new { result = "exception", error = ex.Message });
                }
            }
            else
            {
                _logger.LogInformation("Skipping LibreOffice conversion because flattened PDF was provided for {id}", id);
            }

            // ── Supabase secure storage: upload PPTX + split PDF into per-page files ──
            // Only runs when the session is linked to a course and the PDF conversion succeeded.
            if (courseId.HasValue && File.Exists(convertedPdfPath))
            {
                try
                {
                    var pdfBytes = await File.ReadAllBytesAsync(convertedPdfPath);

                    byte[]? pptxBytes = null;
                    if (File.Exists(filePath))
                    {
                        pptxBytes = await File.ReadAllBytesAsync(filePath);
                    }

                    var pageCount = await _slideSplitter.UploadPptxAndSplitPdf(
                        pptxBytes ?? Array.Empty<byte>(),
                        pdfBytes,
                        courseId.Value,
                        sessionGuid);
                    _logger.LogInformation("PPTX/PDF uploaded and split into {pages} pages for session {id}", pageCount, id);

                    // Persist TotalSlides in DB
                    var dbSess = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionGuid);
                    if (dbSess != null)
                    {
                        dbSess.TotalSlides = pageCount;
                        await _db.SaveChangesAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Supabase slide upload failed for session {id} – secure delivery unavailable", id);
                    // Do NOT fail the whole upload; the local PDF will still be served
                }
            }

            var pinfo = _store.GetPresentationInfo(id);
            LogEvent(_logger, "upload", id, pinfo?.SessionId, new { file = file.FileName, totalSlides, name });

            // Prefer the converted PDF if present.
            var returnedPdfPath = File.Exists(convertedPdfPath) ? convertedPdfPath : null;
            var returnedFileName = returnedPdfPath != null ? Path.GetFileName(returnedPdfPath) : null;

            // guard against null; when conversion fails we should not return "/uploads/" which is a broken URL
            var returnedUrlRelative = returnedFileName != null ? $"/uploads/{returnedFileName}" : null;

            // Build absolute viewer/backend URLs. Environment variables are read directly,
            // matching earlier behaviour; they may be null which is handled below.
            var viewerBaseEnv = Environment.GetEnvironmentVariable("VIEWER_BASE_URL");
            var viewerPortEnv = Environment.GetEnvironmentVariable("VIEWER_PORT");
            var viewerPort = string.IsNullOrEmpty(viewerPortEnv) ? "3000" : viewerPortEnv;
            var viewerBase = !string.IsNullOrEmpty(viewerBaseEnv)
                ? viewerBaseEnv.TrimEnd('/')
                : $"{req.Scheme}://{req.Host.Host}:{viewerPort}";
            var backendBase = req.Scheme + "://" + req.Host.Value;
            var pdfAbsolute = string.IsNullOrEmpty(returnedFileName) ? null : (backendBase + returnedUrlRelative);
            var pdfEncoded = string.IsNullOrEmpty(pdfAbsolute) ? "" : Uri.EscapeDataString(pdfAbsolute);
            var viewerUrl = $"{viewerBase}/?sessionId={_store.GetPresentationInfo(id)?.SessionId}"
                + (string.IsNullOrEmpty(pdfEncoded) ? "" : $"&pdf={pdfEncoded}");

            var res = new
            {
                presentationId = id,
                presenterToken = presenterToken.Token,
                expires = presenterToken.Expires.ToString("o"),
                pdf = returnedUrlRelative,
                pdfAbsolute = pdfAbsolute,
                thumbnail = (string?)null,
                sessionId = _store.GetPresentationInfo(id)?.SessionId,
                viewerUrl = viewerUrl
            };
            return Results.Ok(res);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "upload failed");
            return Results.StatusCode(500);
        }
    }

    // ── PPT → PDF conversion via LibreOffice ──────────────────────────────
    private static async Task<bool> ConvertPptToPdfAsync(string filePath, string outDir, ILogger logger, int timeoutMs = 60000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "soffice",
                Arguments = $"--headless --convert-to pdf --outdir \"{outDir}\" \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            logger.LogInformation("Invoking soffice to convert {file}", filePath);

            var proc = Process.Start(psi);
            if (proc == null)
            {
                logger.LogWarning("Failed to start soffice process");
                return false;
            }

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { }
                // drain output streams to avoid UnobservedTaskException
                try { await Task.WhenAll(outputTask, errTask).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                logger.LogWarning("soffice conversion timed out for {file}", filePath);
                return false;
            }

            var stdout = await outputTask;
            var stderr = await errTask;

            if (!string.IsNullOrEmpty(stdout)) logger.LogInformation("soffice stdout: {out}", stdout);
            if (!string.IsNullOrEmpty(stderr)) logger.LogWarning("soffice stderr: {err}", stderr);

            var success = proc.ExitCode == 0;
            logger.LogInformation("soffice exit code: {code}", proc.ExitCode);
            return success;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "soffice conversion failed");
            return false;
        }
    }

    private static Dictionary<int, List<int>> NormalizeFrameMap(Dictionary<int, List<int>> map)
    {
        var normalized = new Dictionary<int, List<int>>();
        foreach (var kvp in map)
        {
            if (kvp.Key <= 0)
            {
                continue;
            }

            var frames = (kvp.Value ?? new List<int>())
                .Where(frame => frame > 0)
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();

            if (frames.Count == 0)
            {
                frames.Add(kvp.Key);
            }

            normalized[kvp.Key] = frames;
        }

        return normalized;
    }

    private static void LogEvent(ILogger logger, string eventType, string presentationId, string? sessionId, object? payload)
    {
        try
        {
            logger.LogInformation("{eventType} presentationId={presentationId} sessionId={sessionId} payload={payload}", eventType, presentationId, sessionId, payload);
            PresentationStore.ProgramLogEvent(eventType, presentationId, sessionId, payload);
        }
        catch { }
    }
}
