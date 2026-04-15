using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Diagnostics;
using System.Security.Claims;
using System.Linq;
using Npgsql;
using BackendServer.Shared.Data;
using BackendServer.Shared.Data.Entities;
using BackendServer.Features.Classroom.DTOs;
using BackendServer.Features.Classroom.Services;
using BackendServer.Features.Materials.Services;
using BackendServer.Shared.Services;
using System.Text.Json;
using System.Text;
using BackendServer.Features.Classroom;
using BackendServer.Features.Classroom.Hubs;

namespace BackendServer.Features.Classroom.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SessionsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly PresentationStore _presentationStore;
        private readonly IConfiguration _configuration;
        private readonly PdfService _pdfService;
        private readonly ISolutionPageService _solutionPageService;
        private readonly SlideSplitterService _slideSplitter;
        private readonly IHubContext<PresentationHub> _hubContext;
        private readonly IStorageService _storage;
        private readonly ITranscriptArchiveReader _transcriptArchiveReader;
        private readonly ILogger<SessionsController> _logger;

        public SessionsController(
            AppDbContext context,
            PresentationStore presentationStore,
            IConfiguration configuration,
            PdfService pdfService,
            ISolutionPageService solutionPageService,
            SlideSplitterService slideSplitter,
            IHubContext<PresentationHub> hubContext,
            IStorageService storage,
            ITranscriptArchiveReader transcriptArchiveReader,
            ILogger<SessionsController> logger)
        {
            _context = context;
            _presentationStore = presentationStore;
            _configuration = configuration;
            _pdfService = pdfService;
            _solutionPageService = solutionPageService;
            _slideSplitter = slideSplitter;
            _hubContext = hubContext;
            _storage = storage;
            _transcriptArchiveReader = transcriptArchiveReader;
            _logger = logger;
        }

        // DTO for advance-slide body
        public record AdvanceSlideRequest(int NewIndex);

        private const int TranscriptSignedUrlExpirySeconds = 300;
        private const string AnnotatedPptxBucket = "presentations";
        private const string AnnotatedPdfBucket = "slides";
        private const int ReplayDeckSignedUrlExpirySeconds = 300;
        private const string ReplayDeckFileNameSuffix = "_annotated_replay.pdf";

        private sealed record SessionAccessSnapshot(bool IsInstructor, bool IsEnrolled);
        private sealed record SessionViewSnapshot(
            Guid Id,
            string PresentationTitle,
            SessionStatus Status,
            int SlideCount,
            int TotalSlides,
            int CurrentSlideIndex,
            bool AllowStudentDownload,
            Guid? CourseId,
            DateTime CreatedAt,
            DateTime? StartedAt,
            DateTime? EndedAt,
            string? TranscriptStoragePath,
            string? SummaryText,
            string? AnnotatedPptxStoragePath,
            DateTime? DownloadAvailableAt);

        private sealed record SessionDownloadLinks(
            string? originalPptx,
            string? originalPdf,
            string? inkedPptx,
            string? inkedPdf,
            string? inkedWithSolutionsPdf,
            string? inkArtifactPdf,
            string? annotatedPdf,
            string? annotatedPptx)
        {
            public int signedUrlCount => new[]
            {
                originalPptx,
                originalPdf,
                inkedPptx,
                inkedPdf,
                inkedWithSolutionsPdf,
                inkArtifactPdf,
                annotatedPdf,
                annotatedPptx
            }.Count(value => !string.IsNullOrWhiteSpace(value));
        }

        private sealed record ReplayDeckArtifactResult(
            string? Url,
            bool Generated,
            bool IncludesSolutions,
            int SolutionsAppendedCount,
            string StoragePath);

        [HttpPost("create")]
        public async Task<IActionResult> CreateSession([FromBody] CreateSessionDto dto)
        {
            var sessionGuid = Guid.NewGuid();
            var id = sessionGuid.ToString("n");

            var tokenInfo = _presentationStore.RegisterPresentation(id, dto.PresentationTitle, dto.SlideCount, "");

            var session = new Session
            {
                Id = sessionGuid,
                PresentationTitle = dto.PresentationTitle,
                SlideCount = dto.SlideCount,
                CourseId = dto.CourseId,
                PresenterToken = tokenInfo.Token,
                Status = SessionStatus.Created,
                CreatedAt = DateTime.UtcNow
            };

            _context.Sessions.Add(session);
            await _context.SaveChangesAsync();

            var frontendUrl = _configuration["FrontendUrl"] ?? "http://localhost:5173";

            return Ok(new { 
                sessionId = id, 
                presenterToken = session.PresenterToken,
                shareUrl = $"{frontendUrl}/session/{id}" 
            });
        }

        [HttpGet("active")]
        public async Task<IActionResult> GetActiveSessions()
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            var sessions = await _context.Sessions
                .Where(s => s.CreatedAt >= cutoff)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();

            var result = new List<object>();

            foreach (var s in sessions)
            {
                string courseName = "Ad-hoc Session";
                string courseCode = "";

                if (s.CourseId.HasValue)
                {
                    var course = await _context.Courses.FindAsync(s.CourseId.Value);
                    if (course != null)
                    {
                        courseName = course.Name;
                        courseCode = course.Code;
                    }
                }

                result.Add(new 
                {
                    SessionId = s.Id.ToString("n"),
                    PresentationTitle = s.PresentationTitle,
                    CourseId = s.CourseId,
                    CourseName = courseName,
                    CourseCode = courseCode,
                    CreatedAt = s.CreatedAt
                });
            }

            return Ok(result);
        }

        [HttpGet("{sessionId}/download")]
        public async Task<IActionResult> DownloadSessionArtifact(string sessionId, [FromQuery] string format)
        {
            Session? session = null;
            if (Guid.TryParse(sessionId, out var sessionGuid))
            {
                session = await _context.Sessions.FirstOrDefaultAsync(s => s.Id == sessionGuid);
            }
            
            if (session == null) return NotFound("Session not found");

            var uploads = Path.Combine(AppContext.BaseDirectory, "uploads");
            var idStr = session.Id.ToString("n");
            string filePath = "";
            string contentType = "application/octet-stream";
            string downloadName = "download";

            format = format?.ToLower() ?? "pptx";

            if (format == "pptx")
            {
                filePath = Path.Combine(uploads, idStr + ".pptx");
                if (!System.IO.File.Exists(filePath)) filePath = Path.Combine(uploads, idStr + ".ppt");
                
                contentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                downloadName = $"{session.PresentationTitle}.pptx";
            }
            else if (format == "pdf_clean")
            {
                filePath = Path.Combine(uploads, idStr + ".pdf");
                contentType = "application/pdf";
                downloadName = $"{session.PresentationTitle}_Slides.pdf";
            }
            else if (format == "pdf_ink")
            {
                string inkedPdfName = $"{idStr}_inked.pdf";
                filePath = Path.Combine(uploads, inkedPdfName);

                if (!System.IO.File.Exists(filePath))
                {
                    var originalPdf = Path.Combine(uploads, idStr + ".pdf");
                    if (!System.IO.File.Exists(originalPdf)) return NotFound("Base PDF for ink merging not found.");

                    var inkFiles = _pdfService.FindInkFiles(uploads, idStr);
                    
                    if (inkFiles.Count > 0)
                    {
                        try 
                        {
                            var bytes = _pdfService.MergeInkIntoPdf(originalPdf, inkFiles);
                            await System.IO.File.WriteAllBytesAsync(filePath, bytes);
                        }
                        catch (Exception ex)
                        {
                            return BadRequest($"Error generating inked PDF: {ex.Message}");
                        }
                    }
                    else
                    {
                        filePath = originalPdf;
                    }
                }
                
                contentType = "application/pdf";
                downloadName = $"{session.PresentationTitle}_Annotated.pdf";
            }
            else if (format == "pdf_annotated")
            {
                filePath = Path.Combine(uploads, idStr + "_annotated.pdf");
                if (!System.IO.File.Exists(filePath))
                {
                    try
                    {
                        var annotatedBytes = await _pdfService.GetAnnotatedPdfBytesAsync(session.Id.ToString("N"));
                        if (annotatedBytes == null || annotatedBytes.Length == 0)
                            return NotFound("Annotated PDF file not found.");

                        await System.IO.File.WriteAllBytesAsync(filePath, annotatedBytes);
                    }
                    catch (Exception ex)
                    {
                        return BadRequest($"Error generating annotated PDF: {ex.Message}");
                    }
                }

                contentType = "application/pdf";
                downloadName = $"{session.PresentationTitle}_Annotated.pdf";
            }
            else if (format == "json")
            {
                if (!string.IsNullOrEmpty(session.TranscriptStoragePath))
                {
                    try
                    {
                        var signedUrl = await _storage.GetSignedUrlAsync(session.TranscriptStoragePath, TranscriptSignedUrlExpirySeconds);
                        if (string.IsNullOrEmpty(signedUrl))
                        {
                            _logger.LogWarning("Signed URL was null or empty for session {SessionId}", idStr);
                            return NotFound("Transcript file could not be located in storage.");
                        }
                        return Redirect(signedUrl);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate signed URL for transcript of session {SessionId}", idStr);
                        return StatusCode(502, "Failed to retrieve transcript from storage.");
                    }
                }
                else
                {
                    return Ok(new
                    {
                        Id = idStr,
                        session.PresentationTitle,
                        session.SlideCount,
                        session.Status,
                        session.CreatedAt
                    });
                }
            }
            else
            {
                return BadRequest("Invalid format. Supported: pptx, pdf_clean, pdf_ink, pdf_annotated, json");
            }

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound($"File for format '{format}' not found on server.");
            }

            return PhysicalFile(filePath, contentType, downloadName, enableRangeProcessing: true);
        }

        // ================================================================
        // GET /api/sessions/{sessionId}
        // Returns session metadata for enrolled students and the course instructor.
        // All statuses (Created/Running/Ended) are returned — no status restriction.
        // ================================================================
        [Authorize]
        [HttpGet("{sessionId}")]
        public async Task<IActionResult> GetSession(string sessionId)
        {
            var endpointWatch = Stopwatch.StartNew();
            var dbWatch = Stopwatch.StartNew();

            if (!Guid.TryParse(sessionId, out var sessionGuid))
                return BadRequest(new { error = "Invalid session ID" });

            var session = await LoadSessionViewSnapshotAsync(sessionGuid, HttpContext.RequestAborted);
            var sessionLookupMs = dbWatch.ElapsedMilliseconds;
            dbWatch.Restart();

            if (session == null) return NotFound(new { error = "Session not found" });

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdClaim == null) return Unauthorized();
            var userId = Guid.Parse(userIdClaim);
            var role = User.FindFirstValue(ClaimTypes.Role);

            var access = await ResolveSessionAccessAsync(
                session.Id,
                session.CourseId,
                userId,
                role,
                allowTeacherRoleBypass: true,
                HttpContext.RequestAborted);
            var isInstructor = access.IsInstructor;

            if (session.CourseId.HasValue)
            {
                if (!isInstructor)
                {
                    if (!access.IsEnrolled) return Forbid();
                }
            }
            else if (!isInstructor)
            {
                return Forbid();
            }

            var authCheckMs = dbWatch.ElapsedMilliseconds;
            dbWatch.Restart();

            var unlockedSlides = await _context.SessionUnlockedSlides
                .AsNoTracking()
                .Where(u => u.SessionId == sessionGuid)
                .Select(u => u.SlideIndex)
                .OrderBy(i => i)
                .ToListAsync(HttpContext.RequestAborted);

            var unlockedSlidesMs = dbWatch.ElapsedMilliseconds;
            dbWatch.Restart();

            var unlockedUpTo = unlockedSlides.Count > 0 ? unlockedSlides.Max() : 1;

            // ── Signed viewer URL for replay PDF ─────────────────────────────────
            // Prefer backend-owned replay artifact and fall back to the base deck only if needed.
            var pdfUrl = await ResolveReplayPdfUrlAsync(session, HttpContext.RequestAborted);

            var pdfUrlMs = dbWatch.ElapsedMilliseconds;
            dbWatch.Restart();

            // ── Signed URL for transcript JSON ──────────────────────────────────
            string? transcriptUrl = null;
            if (session.Status == SessionStatus.Ended && !string.IsNullOrEmpty(session.TranscriptStoragePath))
            {
                try { transcriptUrl = await _storage.GetSignedUrlAsync(session.TranscriptStoragePath, TranscriptSignedUrlExpirySeconds); }
                catch { /* ignore */ }
            }

            TranscriptArchiveDto? transcriptArchive = null;
            if (session.Status == SessionStatus.Ended && !string.IsNullOrEmpty(session.TranscriptStoragePath))
            {
                transcriptArchive = await _transcriptArchiveReader.LoadAsync(session.TranscriptStoragePath, HttpContext.RequestAborted);
            }

            var transcript = transcriptArchive?.Transcript
                .Select(entry => new { timestamp = entry.Timestamp, text = entry.Text })
                .ToList();

            var summary = !string.IsNullOrWhiteSpace(session.SummaryText)
                ? session.SummaryText
                : transcriptArchive?.Summary;

            var transcriptUrlMs = dbWatch.ElapsedMilliseconds;
            dbWatch.Restart();

            object? downloads = null;
            int downloadsSignedUrlCount = 0;
            if (session.CourseId.HasValue)
            {
                var canDownload = isInstructor || (
                    session.AllowStudentDownload
                    && session.Status == SessionStatus.Ended
                    && (session.DownloadAvailableAt == null || session.DownloadAvailableAt <= DateTime.UtcNow)
                );

                if (canDownload)
                {
                    var result = await BuildDownloadLinksAsync(session);
                    downloads = result.downloads;
                    downloadsSignedUrlCount = result.signedUrlCount;
                }
            }

            var downloadsMs = dbWatch.ElapsedMilliseconds;
            endpointWatch.Stop();

            var signedUrlCount = (pdfUrl != null ? 1 : 0) + (transcriptUrl != null ? 1 : 0) + downloadsSignedUrlCount;

            _logger.LogInformation(
                "Session metadata timing sessionId={SessionId} role={Role} status={Status} sessionLookupMs={SessionLookupMs} authCheckMs={AuthCheckMs} unlockedSlidesMs={UnlockedSlidesMs} pdfMs={PdfMs} transcriptMs={TranscriptMs} downloadsMs={DownloadsMs} totalMs={TotalMs} signedUrlCount={SignedUrlCount} summaryLoaded={SummaryLoaded} downloadsLoaded={DownloadsLoaded}",
                sessionId,
                role,
                session.Status,
                sessionLookupMs,
                authCheckMs,
                unlockedSlidesMs,
                pdfUrlMs,
                transcriptUrlMs,
                downloadsMs,
                endpointWatch.ElapsedMilliseconds,
                signedUrlCount,
                !string.IsNullOrWhiteSpace(summary),
                downloads != null);

            return Ok(new
            {
                id = session.Id,
                presentationTitle = session.PresentationTitle,
                status = (int)session.Status,
                slideCount = session.SlideCount,
                totalSlides = session.TotalSlides,
                currentSlideIndex = session.CurrentSlideIndex,
                allowStudentDownload = session.AllowStudentDownload,
                courseId = session.CourseId,
                createdAt = session.CreatedAt,
                startedAt = session.StartedAt,
                endedAt = session.EndedAt,
                summary,
                transcript,
                pdfUrl,
                transcriptUrl,
                unlockedSlides,
                unlockedUpTo
            });
        }

        // ================================================================
        // GET /api/sessions/{sessionId}/replay-deck
        // Returns a signed URL for the single replay deck artifact.
        // If missing, lazily builds and persists the artifact first.
        // ================================================================
        [Authorize]
        [HttpGet("{sessionId}/replay-deck")]
        public async Task<IActionResult> GetReplayDeck(string sessionId)
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
                return BadRequest(new { error = "Invalid session ID" });

            _logger.LogInformation("Replay deck request received sessionId={SessionId}", sessionId);

            var session = await LoadSessionViewSnapshotAsync(sessionGuid, HttpContext.RequestAborted);
            if (session == null)
                return NotFound(new { error = "Session not found" });

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdClaim == null)
                return Unauthorized();

            var userId = Guid.Parse(userIdClaim);
            var role = User.FindFirstValue(ClaimTypes.Role);

            var access = await ResolveSessionAccessAsync(
                session.Id,
                session.CourseId,
                userId,
                role,
                allowTeacherRoleBypass: true,
                HttpContext.RequestAborted);

            if (session.CourseId.HasValue)
            {
                if (!access.IsInstructor && !access.IsEnrolled)
                    return Forbid();
            }
            else if (!access.IsInstructor)
            {
                return Forbid();
            }

            if (session.Status != SessionStatus.Ended)
                return BadRequest(new { error = "Replay deck is available after the session ends." });

            var replayDeck = await EnsureReplayDeckArtifactAsync(session, HttpContext.RequestAborted);
            if (string.IsNullOrWhiteSpace(replayDeck.Url))
            {
                _logger.LogWarning(
                    "Replay deck request failed sessionId={SessionId} reason=artifact-unavailable",
                    sessionId);
                return NotFound(new { error = "Replay deck is not available yet." });
            }

            _logger.LogInformation(
                "Replay deck signed URL issued sessionId={SessionId} storagePath={StoragePath} generated={Generated} includesSolutions={IncludesSolutions} solutionsAppendedCount={SolutionsAppendedCount}",
                sessionId,
                replayDeck.StoragePath,
                replayDeck.Generated,
                replayDeck.IncludesSolutions,
                replayDeck.SolutionsAppendedCount);

            return Ok(new
            {
                sessionId = session.Id,
                url = replayDeck.Url,
                includesSolutions = replayDeck.IncludesSolutions,
                solutionsAppendedCount = replayDeck.SolutionsAppendedCount,
                storagePath = replayDeck.StoragePath,
                generated = replayDeck.Generated
            });
        }

        // ================================================================
        // GET /api/sessions/{sessionId}/downloads
        // Returns signed URLs for session download assets only.
        // ================================================================
        [Authorize]
        [HttpGet("{sessionId}/downloads")]
        public async Task<IActionResult> GetSessionDownloads(string sessionId)
        {
            var endpointWatch = Stopwatch.StartNew();
            var dbWatch = Stopwatch.StartNew();

            if (!Guid.TryParse(sessionId, out var sessionGuid))
                return BadRequest(new { error = "Invalid session ID" });

            var session = await LoadSessionViewSnapshotAsync(sessionGuid, HttpContext.RequestAborted);
            var sessionLookupMs = dbWatch.ElapsedMilliseconds;
            dbWatch.Restart();

            if (session == null) return NotFound(new { error = "Session not found" });

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdClaim == null) return Unauthorized();
            var userId = Guid.Parse(userIdClaim);
            var role = User.FindFirstValue(ClaimTypes.Role);

            var access = await ResolveSessionAccessAsync(
                session.Id,
                session.CourseId,
                userId,
                role,
                allowTeacherRoleBypass: true,
                HttpContext.RequestAborted);
            var isInstructor = access.IsInstructor;

            if (session.CourseId.HasValue)
            {
                if (!isInstructor)
                {
                    if (!access.IsEnrolled) return Forbid();
                }
            }
            else if (!isInstructor)
            {
                return Forbid();
            }

            var authCheckMs = dbWatch.ElapsedMilliseconds;
            dbWatch.Restart();

            var canDownload = isInstructor || (
                session.AllowStudentDownload
                && session.Status == SessionStatus.Ended
                && (session.DownloadAvailableAt == null || session.DownloadAvailableAt <= DateTime.UtcNow)
            );

            if (!canDownload)
            {
                endpointWatch.Stop();
                _logger.LogInformation(
                    "Session downloads timing sessionId={SessionId} role={Role} status={Status} sessionLookupMs={SessionLookupMs} authCheckMs={AuthCheckMs} storageMs={StorageMs} totalMs={TotalMs} signedUrlCount={SignedUrlCount} canDownload={CanDownload}",
                    sessionId,
                    role,
                    session.Status,
                    sessionLookupMs,
                    authCheckMs,
                    0,
                    endpointWatch.ElapsedMilliseconds,
                    0,
                    false);

                return Ok(new { downloads = (object?)null });
            }

            var (downloads, signedUrlCount) = await BuildDownloadLinksAsync(session);

            var storageMs = dbWatch.ElapsedMilliseconds;
            endpointWatch.Stop();

            _logger.LogInformation(
                "Session downloads timing sessionId={SessionId} role={Role} status={Status} sessionLookupMs={SessionLookupMs} authCheckMs={AuthCheckMs} storageMs={StorageMs} totalMs={TotalMs} signedUrlCount={SignedUrlCount} canDownload={CanDownload}",
                sessionId,
                role,
                session.Status,
                sessionLookupMs,
                authCheckMs,
                storageMs,
                endpointWatch.ElapsedMilliseconds,
                signedUrlCount,
                true);

            return Ok(new { downloads });
        }

        // ================================================================
        // GET /api/sessions/{sessionId}/slide/{pageIndex}
        // Returns a short-lived signed URL for a single-page PDF.
        // Students are gated by unlocked state (CurrentSlideIndex prefix OR explicit unlocked row).
        // Instructors have no restriction.
        // ================================================================
        [Authorize]
        [HttpGet("{sessionId}/slide/{pageIndex:int}")]
        public async Task<IActionResult> GetSlidePage(string sessionId, int pageIndex)
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
                return BadRequest(new { error = "Invalid session ID" });

            var session = await _context.Sessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionGuid);

            if (session == null) return NotFound();

            // Diagnostic logging for race-condition debugging
            Console.WriteLine($"[SlideUrl] sessionId={sessionId}, pageIndex={pageIndex}, currentSlideIndex={session.CurrentSlideIndex}");

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdClaim == null) return Unauthorized();
            var userId = Guid.Parse(userIdClaim);
            var role = User.FindFirstValue(ClaimTypes.Role);

            Course? course = session.CourseId.HasValue
                ? await _context.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == session.CourseId.Value)
                : null;

            bool isInstructor = role == "Teacher"
                && course != null
                && course.TeacherId == userId;

            _logger.LogInformation(
                "Slide URL auth sessionRaw={SessionRaw} sessionGuid={SessionGuid} pageIndex={PageIndex} role={Role} isInstructor={IsInstructor} status={Status} currentSlideIndex={CurrentSlideIndex}",
                sessionId,
                sessionGuid,
                pageIndex,
                role,
                isInstructor,
                session.Status,
                session.CurrentSlideIndex);

            if (!isInstructor)
            {
                if (!session.CourseId.HasValue)
                    return Forbid();

                var enrolled = await _context.Enrollments.AnyAsync(e =>
                    e.CourseId == session.CourseId.Value
                    && e.StudentId == userId
                    && e.Status == EnrollmentStatus.Enrolled);

                if (!enrolled) return Forbid();

                if (session.Status == SessionStatus.Active)
                {
                    // Strict progressive unlock: access is granted only for explicitly unlocked slides.
                    var isExplicitlyUnlocked = await _context.SessionUnlockedSlides
                        .AsNoTracking()
                        .AnyAsync(u => u.SessionId == sessionGuid && u.SlideIndex == pageIndex);

                    if (!isExplicitlyUnlocked)
                    {
                        Console.WriteLine($"[SlideUrl] 403: slide {pageIndex} is locked (currentSlideIndex={session.CurrentSlideIndex})");
                        _logger.LogWarning(
                            "Slide URL denied as locked session={SessionGuid} pageIndex={PageIndex} currentSlideIndex={CurrentSlideIndex}",
                            sessionGuid,
                            pageIndex,
                            session.CurrentSlideIndex);
                        return StatusCode(403, new
                        {
                            error = "Slide not yet unlocked",
                            currentIndex = session.CurrentSlideIndex
                        });
                    }
                }
                else if (session.Status != SessionStatus.Ended)
                {
                    return BadRequest(new { error = "Session is not active" });
                }
                // If Ended: all slides accessible to enrolled students
            }

            // Bounds check (permissive if TotalSlides hasn't been populated yet)
            int totalSlides = session.TotalSlides > 0 ? session.TotalSlides : session.SlideCount;
            if (pageIndex < 1 || (totalSlides > 0 && pageIndex > totalSlides))
                return BadRequest(new { error = "Invalid slide index" });

            if (!session.CourseId.HasValue)
                return BadRequest(new { error = "Session not linked to a course" });

            try
            {
                _logger.LogInformation(
                    "Signing slide URL session={SessionGuid} course={CourseId} pageIndex={PageIndex}",
                    sessionGuid,
                    session.CourseId,
                    pageIndex);

                var signedUrl = await _slideSplitter.GetSlideSignedUrlAsync(
                    session.CourseId.Value, sessionGuid, pageIndex, expirySeconds: 120);

                return Ok(new { url = signedUrl, pageIndex, totalSlides });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to sign slide URL session={SessionGuid} course={CourseId} pageIndex={PageIndex}",
                    sessionGuid,
                    session.CourseId,
                    pageIndex);
                return StatusCode(500, new { error = "Failed to generate signed URL", detail = ex.Message });
            }
        }

        // ================================================================
        // POST /api/sessions/{sessionId}/advance-slide
        // Updates CurrentSlideIndex and broadcasts SlideAdvanced via SignalR.
        // Accepts either X-Presenter-Token (VSTO) or JWT Teacher auth.
        // ================================================================
        [HttpPost("{sessionId}/advance-slide")]
        public async Task<IActionResult> AdvanceSlide(string sessionId, [FromBody] AdvanceSlideRequest body)
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
                return BadRequest(new { error = "Invalid session ID" });

            var normalizedSessionIdN = sessionGuid.ToString("N");
            var normalizedSessionIdD = sessionGuid.ToString("D");
            var slideIndex1 = body.NewIndex < 1 ? 1 : body.NewIndex;

            // ── Auth: presenter token (VSTO) OR JWT Teacher ──
            var presToken = Request.Headers["X-Presenter-Token"].ToString();
            bool authorized;

            if (!string.IsNullOrEmpty(presToken))
            {
                authorized = _presentationStore.ValidateToken(sessionId, presToken, out _)
                    || _presentationStore.ValidateToken(normalizedSessionIdN, presToken, out _)
                    || _presentationStore.ValidateToken(normalizedSessionIdD, presToken, out _);

                if (!authorized)
                {
                    var tokenSession = await _context.Sessions
                        .AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == sessionGuid);
                    authorized = tokenSession != null
                        && string.Equals(tokenSession.PresenterToken, presToken, StringComparison.Ordinal);
                }

                _logger.LogInformation(
                    "Advance slide token auth sessionRaw={SessionRaw} sessionD={SessionD} tokenValid={TokenValid}",
                    sessionId,
                    normalizedSessionIdD,
                    authorized);
            }
            else
            {
                var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                var roleClaim = User.FindFirstValue(ClaimTypes.Role);
                if (userIdClaim == null) return Unauthorized();
                if (roleClaim != "Teacher") return Forbid();

                var sess = await _context.Sessions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == sessionGuid);

                if (sess?.CourseId.HasValue == true)
                {
                    var checkCourse = await _context.Courses
                        .AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == sess.CourseId.Value);
                    authorized = checkCourse?.TeacherId == Guid.Parse(userIdClaim);
                }
                else authorized = false;
            }

            if (!authorized) return Forbid();

            // ── Update DB CurrentSlideIndex (MUST commit before broadcast) ──
            int totalSlides = 0;
            var dbSession = await _context.Sessions.FirstOrDefaultAsync(s => s.Id == sessionGuid);
            if (dbSession != null)
            {
                dbSession.CurrentSlideIndex = slideIndex1;
                // Sync TotalSlides from SlideCount if not yet set
                if (dbSession.TotalSlides == 0 && dbSession.SlideCount > 0)
                    dbSession.TotalSlides = dbSession.SlideCount;
                totalSlides = dbSession.TotalSlides > 0 ? dbSession.TotalSlides : dbSession.SlideCount;

                // Persist current reached frame (plus slide 1 seed) as unlocked (idempotent upsert).
                await _context.Database.ExecuteSqlRawAsync(@"
                    INSERT INTO session_unlocked_slides (""SessionId"", ""SlideIndex"", ""UnlockedAt"")
                    VALUES (@sessionId, 1, (NOW() AT TIME ZONE 'Asia/Hong_Kong')),
                           (@sessionId, @slideIndex, (NOW() AT TIME ZONE 'Asia/Hong_Kong'))
                    ON CONFLICT DO NOTHING",
                    new NpgsqlParameter("sessionId", sessionGuid),
                    new NpgsqlParameter("slideIndex", slideIndex1)
                );

                _logger.LogInformation(
                    "Advance slide persisted session={SessionId} currentSlide={CurrentSlide} totalSlides={TotalSlides}",
                    normalizedSessionIdD,
                    slideIndex1,
                    totalSlides);

                await _context.SaveChangesAsync();
            }

            // ── Query full unlocked set for broadcast payload ──
            var unlockedSlides = await _context.SessionUnlockedSlides
                .Where(u => u.SessionId == sessionGuid)
                .Select(u => u.SlideIndex)
                .OrderBy(i => i)
                .ToListAsync();
            var unlockedUpTo = unlockedSlides.Any() ? unlockedSlides.Max() : 1;

            _logger.LogInformation(
                "Advance slide unlocked set session={SessionId} unlockedCount={UnlockedCount} unlockedUpTo={UnlockedUpTo}",
                normalizedSessionIdD,
                unlockedSlides.Count,
                unlockedUpTo);

            // ── SignalR broadcast (after DB commit) ──
            // Normalize to D-format (with dashes) to match PresentationHub.JoinSession group key
            var groupKey = normalizedSessionIdD;
            Console.WriteLine($"[Broadcast] \ud83d\udce1 SlideAdvanced/SlideUnlocked -> group '{groupKey}'");
            await _hubContext.Clients
                .Group(groupKey)
                .SendAsync("SlideUnlocked", new {
                    unlockedSlides,
                    unlockedUpTo
                });
            await _hubContext.Clients
                .Group(groupKey)
                .SendAsync("SlideAdvanced", new {
                    slideIndex = slideIndex1,
                    totalSlides,
                    unlockedSlides,
                    unlockedUpTo
                });

            return Ok(new { currentIndex = slideIndex1, totalSlides, unlockedSlides, unlockedUpTo });
        }

        // ================================================================
        // GET /api/sessions/{sessionId}/download-package
        // Returns signed-URL download package (PPTX + PDF + summary).
        // Students: session must be Ended + AllowStudentDownload = true.
        // ================================================================
        [Authorize]
        [HttpGet("{sessionId}/download-package")]
        public async Task<IActionResult> DownloadMaterials(string sessionId)
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
                return BadRequest(new { error = "Invalid session ID" });

            var session = await _context.Sessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionGuid);

            if (session == null) return NotFound();

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdClaim == null) return Unauthorized();
            var userId = Guid.Parse(userIdClaim);
            var role = User.FindFirstValue(ClaimTypes.Role);

            Course? dlCourse = session.CourseId.HasValue
                ? await _context.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == session.CourseId.Value)
                : null;

            bool isInstructor = role == "Teacher"
                && dlCourse != null
                && dlCourse.TeacherId == userId;

            if (!isInstructor)
            {
                if (!session.CourseId.HasValue) return Forbid();

                var enrolled = await _context.Enrollments.AnyAsync(e =>
                    e.CourseId == session.CourseId.Value
                    && e.StudentId == userId
                    && e.Status == EnrollmentStatus.Enrolled);

                if (!enrolled) return Forbid();

                if (session.Status != SessionStatus.Ended)
                    return BadRequest(new { error = "Materials are available after the session ends." });

                if (!session.AllowStudentDownload)
                    return StatusCode(403, new { error = "Instructor has not enabled downloads for this session." });

                if (session.DownloadAvailableAt.HasValue && DateTime.UtcNow < session.DownloadAvailableAt.Value)
                    return BadRequest(new
                    {
                        error = $"Downloads become available at {session.DownloadAvailableAt:u}"
                    });
            }

            if (!session.CourseId.HasValue)
                return BadRequest(new { error = "Session not linked to a course." });

            // Build download package
            var materials = new List<object>();

            // 1. Original PPTX (5-min signed URL)
            try
            {
                var pptxUrl = await _slideSplitter.GetPptxSignedUrlAsync(
                    session.CourseId.Value, sessionGuid, expirySeconds: 300);
                materials.Add(new
                {
                    type = "pptx",
                    filename = $"{session.PresentationTitle}.pptx",
                    url = pptxUrl
                });
            }
            catch { /* PPTX may not exist in Supabase for old sessions */ }

            // 2. Full PDF (5-min signed URL)
            try
            {
                var pdfUrl = await _slideSplitter.GetFullPdfSignedUrlAsync(
                    session.CourseId.Value, sessionGuid, expirySeconds: 300);
                materials.Add(new
                {
                    type = "pdf",
                    filename = $"{session.PresentationTitle}.pdf",
                    url = pdfUrl
                });
            }
            catch { /* PDF may not exist in Supabase for old sessions */ }

            // 3. Annotated PPTX from add-in export upload (preferred), or convention fallback.
            try
            {
                var cId = session.CourseId.Value.ToString("D");
                var sId = session.Id.ToString("D");
                var baseName = Path.GetFileNameWithoutExtension(session.PresentationTitle);
                if (string.IsNullOrWhiteSpace(baseName)) baseName = "presentation";

                var annotatedStoragePath = string.IsNullOrWhiteSpace(session.AnnotatedPptxStoragePath)
                    ? $"{cId}/{sId}/annotated.pptx"
                    : session.AnnotatedPptxStoragePath;

                var annotatedUrl = await _storage.GetSignedDownloadUrlIfExistsAsync(
                    AnnotatedPptxBucket,
                    annotatedStoragePath,
                    $"{baseName}_annotated.pptx",
                    300);

                if (!string.IsNullOrWhiteSpace(annotatedUrl))
                {
                    materials.Add(new
                    {
                        type = "annotatedPptx",
                        filename = $"{session.PresentationTitle}_annotated.pptx",
                        url = annotatedUrl
                    });
                }
            }
            catch { }

            // 4. AI Summary (inline text)
            if (!string.IsNullOrEmpty(session.SummaryText))
            {
                materials.Add(new
                {
                    type = "summary",
                    filename = "summary.txt",
                    content = session.SummaryText
                });
            }

            // 5. Transcript (signed URL if stored in Supabase)
            if (!string.IsNullOrEmpty(session.TranscriptStoragePath))
            {
                try
                {
                    // Sign the actual transcript object, not a slide page
                    var transcriptUrl = await _storage
                        .GetSignedUrlAsync(session.TranscriptStoragePath, expirySeconds: 300);

                    materials.Add(new
                    {
                        type = "transcript",
                        filename = $"{session.PresentationTitle}_transcript.txt",
                        url = transcriptUrl
                    });

                    _logger.LogInformation(
                        "[DownloadPackage] Transcript included for session {SessionId}", sessionGuid);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[DownloadPackage] Failed to sign transcript URL for session {SessionId}", sessionGuid);
                }
            }

            return Ok(new
            {
                sessionTitle = session.PresentationTitle,
                materials
            });
        }

        // ================================================================
        // POST /api/sessions/{sessionId}/exports/ink-artifact
        // Generates standalone export PDF from dedicated ink artifact PNG snapshots.
        // ================================================================
        [Authorize]
        [HttpPost("{sessionId}/exports/ink-artifact")]
        public async Task<IActionResult> GenerateInkArtifactExport(string sessionId)
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
                return BadRequest(new { error = "Invalid session ID" });

            var session = await _context.Sessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionGuid);

            if (session == null)
                return NotFound(new { error = "Session not found" });

            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var role = User.FindFirstValue(ClaimTypes.Role);
            bool isInstructor = false;

            if (session.CourseId.HasValue)
            {
                var course = await _context.Courses
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == session.CourseId.Value);

                if (course != null && course.TeacherId == userId)
                    isInstructor = true;
            }

            if (role == "Teacher")
                isInstructor = true;

            if (!session.CourseId.HasValue && !isInstructor)
                return Forbid();

            if (session.CourseId.HasValue && !isInstructor)
            {
                var enrolled = await _context.Enrollments.AnyAsync(e =>
                    e.CourseId == session.CourseId.Value
                    && e.StudentId == userId
                    && e.Status == EnrollmentStatus.Enrolled);

                if (!enrolled)
                    return Forbid();

                if (session.Status != SessionStatus.Ended)
                    return BadRequest(new { error = "Ink-only PDF is available after the session ends." });

                if (!session.AllowStudentDownload)
                    return StatusCode(403, new { error = "Instructor has not enabled downloads for this session." });

                if (session.DownloadAvailableAt.HasValue && DateTime.UtcNow < session.DownloadAvailableAt.Value)
                {
                    return BadRequest(new
                    {
                        error = $"Downloads become available at {session.DownloadAvailableAt:u}"
                    });
                }
            }

            if (session.Status != SessionStatus.Ended)
                return BadRequest(new { error = "Ink artifact export is generated after session end." });

            var exportService = HttpContext.RequestServices.GetRequiredService<IInkArtifactExportService>();
            var result = await exportService.GenerateAndUploadAsync(sessionGuid);

            if (!result.Generated)
                return NotFound(new { error = "No ink artifact snapshots found for this session" });

            return Ok(new
            {
                status = "generated",
                fileName = "ink_only_artifact.pdf",
                pageCount = result.PageCount
            });
        }

        // ================================================================
        // GET /api/sessions/{sessionId}/exports/ink-artifact
        // Returns a signed URL for standalone ink artifact PDF download.
        // ================================================================
        [Authorize]
        [HttpGet("{sessionId}/exports/ink-artifact")]
        [HttpGet("{sessionId}/download-ink-only")]
        public async Task<IActionResult> GetInkArtifactExportUrl(string sessionId)
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
                return BadRequest(new { error = "Invalid session ID" });

            var session = await _context.Sessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionGuid);

            if (session == null)
                return NotFound(new { error = "Session not found" });

            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var role = User.FindFirstValue(ClaimTypes.Role);
            bool isInstructor = false;

            if (session.CourseId.HasValue)
            {
                var course = await _context.Courses
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == session.CourseId.Value);

                if (course != null && course.TeacherId == userId)
                    isInstructor = true;
            }

            if (role == "Teacher")
                isInstructor = true;

            if (!session.CourseId.HasValue && !isInstructor)
                return Forbid();

            if (session.CourseId.HasValue && !isInstructor)
            {
                var enrolled = await _context.Enrollments.AnyAsync(e =>
                    e.CourseId == session.CourseId.Value
                    && e.StudentId == userId
                    && e.Status == EnrollmentStatus.Enrolled);

                if (!enrolled)
                    return Forbid();

                if (session.Status != SessionStatus.Ended)
                    return BadRequest(new { error = "Ink artifact PDF is available after the session ends." });

                if (!session.AllowStudentDownload)
                    return StatusCode(403, new { error = "Instructor has not enabled downloads for this session." });

                if (session.DownloadAvailableAt.HasValue && DateTime.UtcNow < session.DownloadAvailableAt.Value)
                {
                    return BadRequest(new
                    {
                        error = $"Downloads become available at {session.DownloadAvailableAt:u}"
                    });
                }
            }

            var signedUrl = await _storage.GetSignedDownloadUrlIfExistsAsync(
                "uploads",
                $"{sessionGuid:D}/exports/ink_only_artifact.pdf",
                "ink_only_artifact.pdf",
                7200);

            if (signedUrl == null)
                return NotFound(new { error = "ink_only_artifact.pdf is not available yet" });

            return Ok(new
            {
                fileName = "ink_only_artifact.pdf",
                url = signedUrl
            });
        }

        // ================================================================
        // POST /api/sessions/{sessionId}/exports/annotated-pptx
        // Add-in uploads fully baked annotated PPTX and persists storage path.
        // ================================================================
        [HttpPost("{sessionId}/exports/annotated-pptx")]
        public async Task<IActionResult> UploadAnnotatedPptxExport(string sessionId)
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
                return BadRequest(new { error = "Invalid session ID" });

            var session = await _context.Sessions.FirstOrDefaultAsync(s => s.Id == sessionGuid);
            if (session == null)
                return NotFound(new { error = "Session not found" });

            var presenterToken = Request.Headers["X-Presenter-Token"].ToString();
            if (string.IsNullOrWhiteSpace(presenterToken))
                return Unauthorized(new { error = "presenter token required" });

            var tokenValidInStore = _presentationStore.ValidateToken(sessionId, presenterToken, out _)
                || _presentationStore.ValidateToken(sessionGuid.ToString("N"), presenterToken, out _)
                || _presentationStore.ValidateToken(sessionGuid.ToString("D"), presenterToken, out _);

            if (!tokenValidInStore && !string.Equals(session.PresenterToken, presenterToken, StringComparison.Ordinal))
                return Unauthorized(new { error = "invalid presenter token" });

            if (!session.CourseId.HasValue)
                return BadRequest(new { error = "session is not linked to a course" });

            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            var pptxBytes = ms.ToArray();
            if (pptxBytes.Length == 0)
                return BadRequest(new { error = "empty annotated pptx data" });

            var courseId = session.CourseId.Value.ToString("D");
            var normalizedSessionId = session.Id.ToString("D");
            var storagePath = $"{courseId}/{normalizedSessionId}/annotated.pptx";

            _logger.LogInformation(
                "[AnnotatedPptx] Upload start session={SessionId} bytes={Bytes} storagePath={StoragePath}",
                normalizedSessionId,
                pptxBytes.Length,
                storagePath);

            await using (var stream = new MemoryStream(pptxBytes))
            {
                await _storage.UploadToBucketAsync(
                    stream,
                    AnnotatedPptxBucket,
                    storagePath,
                    "application/vnd.openxmlformats-officedocument.presentationml.presentation");
            }

            _logger.LogInformation(
                "[AnnotatedPptx] Upload success session={SessionId} storagePath={StoragePath}",
                normalizedSessionId,
                storagePath);

            session.AnnotatedPptxStoragePath = storagePath;
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "[AnnotatedPptx] Session persistence update success session={SessionId} storagePath={StoragePath}",
                normalizedSessionId,
                storagePath);

            return Ok(new
            {
                success = true,
                storagePath
            });
        }

        // ================================================================
        // GET /api/sessions/{sessionId}/exports/annotated-pptx
        // Returns signed URL for add-in uploaded annotated PPTX.
        // ================================================================
        [Authorize]
        [HttpGet("{sessionId}/exports/annotated-pptx")]
        public async Task<IActionResult> GetAnnotatedPptxExportUrl(string sessionId)
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
                return BadRequest(new { error = "Invalid session ID" });

            var session = await _context.Sessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionGuid);

            if (session == null)
                return NotFound(new { error = "Session not found" });

            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var role = User.FindFirstValue(ClaimTypes.Role);
            bool isInstructor = false;

            if (session.CourseId.HasValue)
            {
                var course = await _context.Courses
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == session.CourseId.Value);

                if (course != null && course.TeacherId == userId)
                    isInstructor = true;
            }

            if (role == "Teacher")
                isInstructor = true;

            if (!session.CourseId.HasValue && !isInstructor)
                return Forbid();

            if (session.CourseId.HasValue && !isInstructor)
            {
                var enrolled = await _context.Enrollments.AnyAsync(e =>
                    e.CourseId == session.CourseId.Value
                    && e.StudentId == userId
                    && e.Status == EnrollmentStatus.Enrolled);

                if (!enrolled)
                    return Forbid();

                if (session.Status != SessionStatus.Ended)
                    return BadRequest(new { error = "Annotated PPTX is available after the session ends." });

                if (!session.AllowStudentDownload)
                    return StatusCode(403, new { error = "Instructor has not enabled downloads for this session." });

                if (session.DownloadAvailableAt.HasValue && DateTime.UtcNow < session.DownloadAvailableAt.Value)
                {
                    return BadRequest(new
                    {
                        error = $"Downloads become available at {session.DownloadAvailableAt:u}"
                    });
                }
            }

            var baseName = Path.GetFileNameWithoutExtension(session.PresentationTitle ?? "presentation");
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "presentation";

            var storagePath = !string.IsNullOrWhiteSpace(session.AnnotatedPptxStoragePath)
                ? session.AnnotatedPptxStoragePath
                : (session.CourseId.HasValue
                    ? $"{session.CourseId.Value:D}/{session.Id:D}/annotated.pptx"
                    : null);

            if (string.IsNullOrWhiteSpace(storagePath))
                return NotFound(new { error = "annotated.pptx is not available yet" });

            _logger.LogInformation(
                "[AnnotatedPptx] Signed URL generation request session={SessionId} storagePath={StoragePath}",
                session.Id,
                storagePath);

            var signedUrl = await _storage.GetSignedDownloadUrlIfExistsAsync(
                AnnotatedPptxBucket,
                storagePath,
                $"{baseName}_annotated.pptx",
                7200);

            if (signedUrl == null)
                return NotFound(new { error = "annotated.pptx is not available yet" });

            _logger.LogInformation(
                "[AnnotatedPptx] Signed URL generation success session={SessionId} storagePath={StoragePath}",
                session.Id,
                storagePath);

            return Ok(new
            {
                fileName = $"{baseName}_annotated.pptx",
                storagePath,
                url = signedUrl
            });
        }

        // ────────────────────────────────────────────────────────────────
        // POST /api/sessions/debug/broadcast/{sessionId}  (TEMPORARY — remove after fix)
        // Forces a test SlideAdvanced broadcast to confirm hub + group membership.
        // curl -X POST http://localhost:5000/api/sessions/debug/broadcast/{sessionId}
        // ────────────────────────────────────────────────────────────────
        [HttpPost("debug/broadcast/{sessionId}")]
        public async Task<IActionResult> DebugBroadcast(string sessionId)
        {
            // Normalize to D-format (with dashes) to match viewer group key
            var normalizedGroupKey = Guid.TryParse(sessionId, out var parsedId)
                ? parsedId.ToString()   // defaults to D-format
                : sessionId;

            Console.WriteLine($"[Debug] Broadcasting test events to group '{normalizedGroupKey}'");
            await _hubContext.Clients.Group(normalizedGroupKey)
                .SendAsync("SlideAdvanced", new {
                    slideIndex = 99,
                    totalSlides = 99,
                    unlockedSlides = new[] { 1, 2, 3 },
                    unlockedUpTo = 3
                });
            await _hubContext.Clients.Group(normalizedGroupKey)
                .SendAsync("SlideUnlocked", new {
                    newlyUnlocked = new[] { 99 },
                    unlockedSlides = new[] { 1, 2, 3, 99 },
                    currentSlide = 99,
                    totalSlides = 99
                });
            return Ok(new { broadcasted = true, group = normalizedGroupKey });
        }

        // ── Signed-URL redirect endpoints (used as download fallbacks) ──────────

        /// <summary>Returns a signed Supabase URL for the annotated PDF.</summary>
        [Authorize]
        [HttpGet("{sessionId}/exports/annotated-pdf")]
        public async Task<IActionResult> GetAnnotatedPdfExportUrl(string sessionId)
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
                return BadRequest(new { error = "Invalid session ID" });

            var session = await _context.Sessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionGuid);

            if (session == null)
                return NotFound(new { error = "Session not found" });

            var userId = GetCurrentUserId();
            if (userId == null)
                return Unauthorized();

            var role = User.FindFirstValue(ClaimTypes.Role);
            bool isInstructor = false;

            if (session.CourseId.HasValue)
            {
                var course = await _context.Courses
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == session.CourseId.Value);

                if (course != null && course.TeacherId == userId)
                    isInstructor = true;
            }

            if (role == "Teacher")
                isInstructor = true;

            if (!session.CourseId.HasValue && !isInstructor)
                return Forbid();

            if (session.CourseId.HasValue && !isInstructor)
            {
                var enrolled = await _context.Enrollments.AnyAsync(e =>
                    e.CourseId == session.CourseId.Value
                    && e.StudentId == userId
                    && e.Status == EnrollmentStatus.Enrolled);

                if (!enrolled)
                    return Forbid();

                if (session.Status != SessionStatus.Ended)
                    return BadRequest(new { error = "Annotated PDF is available after the session ends." });

                if (!session.AllowStudentDownload)
                    return StatusCode(403, new { error = "Instructor has not enabled downloads for this session." });

                if (session.DownloadAvailableAt.HasValue && DateTime.UtcNow < session.DownloadAvailableAt.Value)
                {
                    return BadRequest(new
                    {
                        error = $"Downloads become available at {session.DownloadAvailableAt:u}"
                    });
                }
            }

            var annotatedPdf = await EnsureAnnotatedPdfArtifactAsync(session, HttpContext.RequestAborted);
            if (annotatedPdf == null)
                return NotFound(new { error = "Annotated PDF is not available yet" });

            var baseName = Path.GetFileNameWithoutExtension(session.PresentationTitle ?? "presentation");
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "presentation";

            return Ok(new
            {
                fileName = $"{baseName}_annotated.pdf",
                url = annotatedPdf
            });
        }

        /// <summary>Redirects to a signed Supabase URL for the annotated PDF.</summary>
        [HttpGet("/download/{id}/annotated-pdf")]
        [AllowAnonymous]
        public async Task<IActionResult> DownloadAnnotatedPdf(string id)
        {
            if (!Guid.TryParse(id, out var sessionGuid))
                return BadRequest("Invalid session ID format.");

            var session = await _context.Sessions.FindAsync(sessionGuid);
            if (session == null) return NotFound("Session not found.");

            var annotatedPdf = await EnsureAnnotatedPdfArtifactAsync(session, HttpContext.RequestAborted);
            if (annotatedPdf == null) return NotFound("Annotated PDF not found.");

            return Redirect(annotatedPdf);
        }

        /// <summary>Redirects to a signed Supabase URL for the original full PDF.</summary>
        [HttpGet("/download/{id}/pdf")]
        [AllowAnonymous]
        public async Task<IActionResult> DownloadOriginalPdf(string id)
        {
            if (!Guid.TryParse(id, out var sessionGuid))
                return BadRequest("Invalid session ID format.");

            var session = await _context.Sessions.FindAsync(sessionGuid);
            if (session == null || !session.CourseId.HasValue) return NotFound("Session not found.");

            var cId = session.CourseId.Value.ToString("D");
            var sId = session.Id.ToString("D");
            var baseName = Path.GetFileNameWithoutExtension(session.PresentationTitle ?? "presentation");
            var url = await _storage.GetSignedDownloadUrlIfExistsAsync(
                "slides", $"{cId}/{sId}/full.pdf", $"{baseName}.pdf", 7200);

            if (url == null) return NotFound("Original PDF not found in storage.");
            return Redirect(url);
        }

        /// <summary>Redirects to a signed Supabase URL for the original PPTX.</summary>
        [HttpGet("/download/{id}/pptx")]
        [AllowAnonymous]
        public async Task<IActionResult> DownloadOriginalPptx(string id)
        {
            if (!Guid.TryParse(id, out var sessionGuid))
                return BadRequest("Invalid session ID format.");

            var session = await _context.Sessions.FindAsync(sessionGuid);
            if (session == null || !session.CourseId.HasValue) return NotFound("Session not found.");

            var cId = session.CourseId.Value.ToString("D");
            var sId = session.Id.ToString("D");
            var baseName = Path.GetFileNameWithoutExtension(session.PresentationTitle ?? "presentation");
            var url = await _storage.GetSignedDownloadUrlIfExistsAsync(
                "presentations", $"{cId}/{sId}/original.pptx", $"{baseName}.pptx", 7200);

            if (url == null) return NotFound("Original PPTX not found in storage.");
            return Redirect(url);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────
        private Guid? GetCurrentUserId()
        {
            var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(claim, out var id) ? id : null;
        }

        private Task<SessionAccessSnapshot> ResolveSessionAccessAsync(
            Session session,
            Guid userId,
            string? role,
            bool allowTeacherRoleBypass,
            CancellationToken ct)
        {
            return ResolveSessionAccessAsync(session.Id, session.CourseId, userId, role, allowTeacherRoleBypass, ct);
        }

        private async Task<SessionViewSnapshot?> LoadSessionViewSnapshotAsync(Guid sessionId, CancellationToken ct)
        {
            return await _context.Sessions
                .AsNoTracking()
                .Where(s => s.Id == sessionId)
                .Select(s => new SessionViewSnapshot(
                    s.Id,
                    s.PresentationTitle,
                    s.Status,
                    s.SlideCount,
                    s.TotalSlides,
                    s.CurrentSlideIndex,
                    s.AllowStudentDownload,
                    s.CourseId,
                    s.CreatedAt,
                    s.StartedAt,
                    s.EndedAt,
                    s.TranscriptStoragePath,
                    s.SummaryText,
                    s.AnnotatedPptxStoragePath,
                    s.DownloadAvailableAt))
                .FirstOrDefaultAsync(ct);
        }

        private async Task<SessionAccessSnapshot> ResolveSessionAccessAsync(
            Guid sessionId,
            Guid? courseId,
            Guid userId,
            string? role,
            bool allowTeacherRoleBypass,
            CancellationToken ct)
        {
            var isTeacherRole = string.Equals(role, "Teacher", StringComparison.OrdinalIgnoreCase);
            var isInstructor = false;
            var isEnrolled = false;

            if (courseId.HasValue)
            {
                var ownsCourse = false;
                if (isTeacherRole)
                {
                    ownsCourse = await _context.Courses
                        .AsNoTracking()
                        .AnyAsync(c => c.Id == courseId.Value && c.TeacherId == userId, ct);
                }

                isInstructor = allowTeacherRoleBypass
                    ? isTeacherRole || ownsCourse
                    : isTeacherRole && ownsCourse;

                if (!isInstructor)
                {
                    isEnrolled = await _context.Enrollments
                        .AsNoTracking()
                        .AnyAsync(e => e.CourseId == courseId.Value
                            && e.StudentId == userId
                            && e.Status == EnrollmentStatus.Enrolled, ct);
                }
            }
            else
            {
                isInstructor = allowTeacherRoleBypass && isTeacherRole;
            }

            return new SessionAccessSnapshot(isInstructor, isEnrolled);
        }

        private async Task<(SessionDownloadLinks downloads, int signedUrlCount)> BuildDownloadLinksAsync(SessionViewSnapshot session)
        {
            if (!session.CourseId.HasValue)
            {
                var empty = new SessionDownloadLinks(null, null, null, null, null, null, null, null);
                return (empty, 0);
            }

            var cId = session.CourseId.Value.ToString("D");
            var sId = session.Id.ToString("D");
            var baseName = Path.GetFileNameWithoutExtension(session.PresentationTitle);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "presentation";

            var annotatedStoragePath = string.IsNullOrWhiteSpace(session.AnnotatedPptxStoragePath)
                ? $"{cId}/{sId}/annotated.pptx"
                : session.AnnotatedPptxStoragePath;

            var annotatedPdfStoragePath = $"{cId}/{sId}/annotated.pdf";

            var annotatedPdf = await _storage.GetSignedDownloadUrlIfExistsAsync(
                AnnotatedPdfBucket,
                annotatedPdfStoragePath,
                $"{baseName}_annotated.pdf",
                7200);

            var annotatedPptx = await _storage.GetSignedDownloadUrlIfExistsAsync(
                AnnotatedPptxBucket,
                annotatedStoragePath,
                $"{baseName}_annotated.pptx",
                7200);

            var originalPptx = await _storage.GetSignedDownloadUrlIfExistsAsync(
                "presentations", $"{cId}/{sId}/original.pptx",
                $"{baseName}.pptx", 7200);

            var originalPdf = await _storage.GetSignedDownloadUrlIfExistsAsync(
                "slides", $"{cId}/{sId}/full.pdf",
                $"{baseName}.pdf", 7200);

            var inkedPptx = await _storage.GetSignedDownloadUrlIfExistsAsync(
                "presentations", $"{cId}/{sId}/inked.pptx",
                $"{baseName}_inked.pptx", 7200);

            var inkedPdf = await _storage.GetSignedDownloadUrlIfExistsAsync(
                "slides", $"{cId}/{sId}/inked.pdf",
                $"{baseName}_inked.pdf", 7200);

            var inkedWithSolutionsPdf = await _storage.GetSignedDownloadUrlIfExistsAsync(
                "slides", $"{cId}/{sId}/inked-with-solutions.pdf",
                $"{baseName}_inked_with_solutions.pdf", 7200);

            var inkArtifactPdf = await _storage.GetSignedDownloadUrlIfExistsAsync(
                "uploads", $"{sId}/exports/ink_only_artifact.pdf",
                "ink_only_artifact.pdf", 7200);

            var links = new SessionDownloadLinks(
                originalPptx,
                originalPdf,
                inkedPptx,
                inkedPdf,
                inkedWithSolutionsPdf,
                inkArtifactPdf,
                annotatedPdf,
                annotatedPptx);

            return (links, links.signedUrlCount);
        }

        private async Task<string?> ResolveReplayPdfUrlAsync(SessionViewSnapshot session, CancellationToken ct)
        {
            if (session.Status != SessionStatus.Ended || !session.CourseId.HasValue)
                return null;

            var cId = session.CourseId.Value.ToString("D");
            var sId = session.Id.ToString("D");
            var baseName = Path.GetFileNameWithoutExtension(session.PresentationTitle);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "presentation";

            var replayStoragePath = $"{cId}/{sId}/annotated-replay.pdf";
            var replayFileName = $"{baseName}{ReplayDeckFileNameSuffix}";

            var replayUrl = await _storage.GetSignedDownloadUrlIfExistsAsync(
                AnnotatedPdfBucket,
                replayStoragePath,
                replayFileName,
                ReplayDeckSignedUrlExpirySeconds);

            if (!string.IsNullOrWhiteSpace(replayUrl))
                return replayUrl;

            try
            {
                return await _slideSplitter.GetFullPdfSignedUrlAsync(
                    session.CourseId.Value,
                    session.Id,
                    expirySeconds: ReplayDeckSignedUrlExpirySeconds);
            }
            catch
            {
                return null;
            }
        }

        private async Task<ReplayDeckArtifactResult> EnsureReplayDeckArtifactAsync(SessionViewSnapshot session, CancellationToken ct)
        {
            if (!session.CourseId.HasValue)
                return new ReplayDeckArtifactResult(null, false, false, 0, string.Empty);

            var cId = session.CourseId.Value.ToString("D");
            var sId = session.Id.ToString("D");
            var baseName = Path.GetFileNameWithoutExtension(session.PresentationTitle);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "presentation";

            var storagePath = $"{cId}/{sId}/annotated-replay.pdf";
            var replayFileName = $"{baseName}{ReplayDeckFileNameSuffix}";

            var solutionPages = await _solutionPageService.ListSolutionPagesAsync(session.Id, ct);
            var solutionsAppendedCount = solutionPages.Count(p => p.HasInk);
            var includesSolutions = solutionsAppendedCount > 0;

            var existingUrl = await _storage.GetSignedDownloadUrlIfExistsAsync(
                AnnotatedPdfBucket,
                storagePath,
                replayFileName,
                ReplayDeckSignedUrlExpirySeconds);

            if (!string.IsNullOrWhiteSpace(existingUrl))
            {
                _logger.LogInformation(
                    "Replay deck artifact exists sessionId={SessionId} storagePath={StoragePath} includesSolutions={IncludesSolutions} solutionsAppendedCount={SolutionsAppendedCount}",
                    session.Id,
                    storagePath,
                    includesSolutions,
                    solutionsAppendedCount);

                return new ReplayDeckArtifactResult(
                    existingUrl,
                    false,
                    includesSolutions,
                    solutionsAppendedCount,
                    storagePath);
            }

            _logger.LogInformation(
                "Replay deck artifact missing sessionId={SessionId} storagePath={StoragePath}",
                session.Id,
                storagePath);

            byte[]? annotatedBytes;
            try
            {
                _logger.LogInformation(
                    "Replay deck bake start sessionId={SessionId} includesSolutions={IncludesSolutions} solutionsAppendedCount={SolutionsAppendedCount}",
                    session.Id,
                    includesSolutions,
                    solutionsAppendedCount);

                annotatedBytes = await _pdfService.GetAnnotatedPdfBytesAsync(session.Id.ToString("N"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Replay deck bake failed during annotated bytes generation sessionId={SessionId}", session.Id);
                return new ReplayDeckArtifactResult(null, false, includesSolutions, solutionsAppendedCount, storagePath);
            }

            if (annotatedBytes == null || annotatedBytes.Length == 0)
            {
                _logger.LogWarning("Replay deck bake failed: annotated base deck missing sessionId={SessionId}", session.Id);
                return new ReplayDeckArtifactResult(null, false, includesSolutions, solutionsAppendedCount, storagePath);
            }

            var replayBytes = includesSolutions
                ? await _solutionPageService.AppendSolutionsToDeckPdfAsync(session.Id, annotatedBytes, skipEmptyPages: true, ct)
                : annotatedBytes;

            _logger.LogInformation(
                "Replay deck bake end sessionId={SessionId} replayBytes={ReplayBytes} solutionsAppendedCount={SolutionsAppendedCount}",
                session.Id,
                replayBytes.Length,
                solutionsAppendedCount);

            await _storage.UploadToBucketAsync(
                new MemoryStream(replayBytes, writable: false),
                AnnotatedPdfBucket,
                storagePath,
                "application/pdf");

            _logger.LogInformation(
                "Replay deck upload success sessionId={SessionId} storagePath={StoragePath}",
                session.Id,
                storagePath);

            var signedUrl = await _storage.GetSignedDownloadUrlAsync(
                AnnotatedPdfBucket,
                storagePath,
                replayFileName,
                ReplayDeckSignedUrlExpirySeconds);

            return new ReplayDeckArtifactResult(
                signedUrl,
                true,
                includesSolutions,
                solutionsAppendedCount,
                storagePath);
        }

        private async Task<string?> EnsureAnnotatedPdfArtifactAsync(Session session, CancellationToken ct)
        {
            var sessionId = session.Id.ToString("D");
            var bucket = session.CourseId.HasValue ? AnnotatedPdfBucket : "uploads";
            var storagePath = session.CourseId.HasValue
                ? $"{session.CourseId.Value:D}/{sessionId}/annotated.pdf"
                : $"{sessionId}/exports/annotated.pdf";
            var baseName = Path.GetFileNameWithoutExtension(session.PresentationTitle);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "presentation";

            byte[]? annotatedBytes;
            try
            {
                annotatedBytes = await _pdfService.GetAnnotatedPdfBytesAsync(session.Id.ToString("N"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating annotated PDF for session {SessionId}", session.Id);
                return null;
            }

            if (annotatedBytes == null || annotatedBytes.Length == 0)
                return null;

            var finalAnnotatedBytes = await _solutionPageService.AppendSolutionsToDeckPdfAsync(
                session.Id,
                annotatedBytes,
                skipEmptyPages: true,
                ct);

            _logger.LogInformation(
                "[AnnotatedPdf] Final artifact selected for session {SessionId}: bucket={Bucket} storagePath={StoragePath} bytes={ByteCount}",
                session.Id,
                bucket,
                storagePath,
                finalAnnotatedBytes.Length);

            await _storage.UploadToBucketAsync(
                new MemoryStream(finalAnnotatedBytes),
                bucket,
                storagePath,
                "application/pdf");

            _logger.LogInformation(
                "[AnnotatedPdf] Upload success for session {SessionId}: bucket={Bucket} storagePath={StoragePath}",
                session.Id,
                bucket,
                storagePath);

            return await _storage.GetSignedDownloadUrlIfExistsAsync(
                bucket,
                storagePath,
                $"{baseName}_annotated.pdf",
                7200);
        }
    }
}
