using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using BackendServer.Configuration;
using BackendServer.Features.Classroom;
using BackendServer.Features.Classroom.Hubs;
using BackendServer.Features.Classroom.DTOs;
using BackendServer.Shared.Data;
using BackendServer.Shared.Data.Entities;
using BackendServer.Shared.Services;
using BackendServer.Features.Classroom.Services;
using BackendServer.Features.Materials.Services;

namespace BackendServer.Features.Classroom.Controllers;

/// <summary>
/// Handles slide unlock/advance and session lifecycle endpoints.
/// Extracted from Program.cs minimal-API handlers.
/// </summary>
[ApiController]
[Route("api")]
public class LiveSessionController : ControllerBase
{
    private static readonly ConcurrentDictionary<Guid, byte> SummaryGenerationInFlight = new();

    private readonly PresentationStore _store;
    private readonly AppDbContext _db;
    private readonly IHubContext<PresentationHub> _hub;
    private readonly IServiceProvider _sp;
    private readonly ILogger<LiveSessionController> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IArtifactBakingQueue _bakingQueue;

    public LiveSessionController(
        PresentationStore store,
        AppDbContext db,
        IHubContext<PresentationHub> hub,
        IServiceProvider sp,
        ILogger<LiveSessionController> logger,
        IServiceScopeFactory scopeFactory,
        IArtifactBakingQueue bakingQueue)
    {
        _store = store;
        _db = db;
        _hub = hub;
        _sp = sp;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _bakingQueue = bakingQueue;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool ValidatePresenter(string presentationId, out PresentationTokenInfo? info)
    {
        info = null;
        var token = Request.Headers["X-Presenter-Token"].ToString();
        if (string.IsNullOrEmpty(token))
        {
            var auth = Request.Headers["Authorization"].ToString();
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ")) token = auth.Substring(7);
        }
        if (string.IsNullOrEmpty(token)) return false;
        var ok = _store.ValidateToken(presentationId, token, out info);
        if (ok)
        {
            var pres = _store.GetPresentationInfo(presentationId);
            LogEvent("auth", presentationId, pres?.SessionId, new { tokenShort = token.Substring(0, Math.Min(8, token.Length)) });
        }
        return ok;
    }

    private bool IsBearerPresent()
    {
        var auth = Request.Headers["Authorization"].ToString();
        return !string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ");
    }

    private static string NormalizeGroupKey(string id) => Guid.TryParse(id, out var g) ? g.ToString("D") : id;

    private void LogEvent(string eventType, string presentationId, string? sessionId, object? payload)
    {
        try
        {
            _logger.LogInformation("{eventType} presentationId={presentationId} sessionId={sessionId} payload={payload}",
                eventType, presentationId, sessionId, payload);
            PresentationStore.ProgramLogEvent(eventType, presentationId, sessionId, payload);
        }
        catch { }
    }

    // ── Endpoints ─────────────────────────────────────────────────────────────

    [HttpPost("sessions/unlock-slide")]
    public async Task<IActionResult> UnlockSlide()
    {
        var doc = await JsonDocument.ParseAsync(Request.Body);
        var root = doc.RootElement;

        string? presentationId = null;
        if (root.TryGetProperty("presentationId", out var pid)) presentationId = pid.GetString();

        string? token = null;
        if (Request.Headers.TryGetValue("X-Presenter-Token", out var t)) token = t.ToString();
        else if (root.TryGetProperty("presenterToken", out var pt)) token = pt.GetString();

        if (string.IsNullOrEmpty(presentationId) || string.IsNullOrEmpty(token))
            return BadRequest(new { error = "presentationId and token required" });

        // Log token presence (do not log full token in prod)
        var tokenSample = token.Length > 8 ? token.Substring(0, 8) : token;
        _logger.LogInformation("unlock attempt: presentation={presentationId} tokenPresent={hasToken} tokenSample={tokenSample}",
            presentationId, true, tokenSample);

        if (!_store.ValidateToken(presentationId, token, out var _))
        {
            _logger.LogWarning("unlock unauthorized: presentation={presentationId} tokenSample={tokenSample}", presentationId, tokenSample);
            return Unauthorized();
        }

        var pres = _store.GetPresentationInfo(presentationId);
        if (pres == null) return NotFound(new { error = "presentation not found" });

        var slidesToUnlock = new List<int>();

        // Support single slide: { slide: 5 }
        if (root.TryGetProperty("slide", out var slideEl))
        {
            slidesToUnlock.Add(slideEl.GetInt32());
        }

        // Support multiple slides: { slides: [1, 2, 5] }
        if (root.TryGetProperty("slides", out var slidesEl) && slidesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in slidesEl.EnumerateArray())
            {
                slidesToUnlock.Add(s.GetInt32());
            }
        }

        if (slidesToUnlock.Count == 0)
        {
            return BadRequest(new { error = "slide or slides array required" });
        }

        // Unlock each slide (true progressive unlock)
        foreach (var slide in slidesToUnlock)
        {
            if (slide >= 1 && slide <= pres.TotalSlides)
            {
                _store.UnlockSlide(presentationId, slide);
            }
        }

        var unlockedList = pres.GetUnlockedSlidesList();

        // ── DB write BEFORE broadcast (prevents 403 race) ──
        if (Guid.TryParse(presentationId, out var unlockSessionGuid))
        {
            var dbSession = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == unlockSessionGuid);
            if (dbSession != null)
            {
                foreach (var slide in slidesToUnlock)
                {
                    await _db.Database.ExecuteSqlRawAsync(@"
                        INSERT INTO session_unlocked_slides (""SessionId"", ""SlideIndex"", ""UnlockedAt"")
                        VALUES ({0}, {1}, (NOW() AT TIME ZONE 'Asia/Hong_Kong'))
                        ON CONFLICT DO NOTHING", unlockSessionGuid, slide);
                }
                await _db.SaveChangesAsync();
                Console.WriteLine($"[unlock-slide] ✅ DB committed slides [{string.Join(",", slidesToUnlock)}] for session {unlockSessionGuid}");
            }
        }

        // Broadcast — normalize presentationId to dashed D-format to match JoinSession group key
        var groupKey = NormalizeGroupKey(presentationId);
        Console.WriteLine($"[Broadcast] 📡 SlideUnlocked -> group '{groupKey}'");
        await _hub.Clients.Group(groupKey).SendAsync("SlideUnlocked", new
        {
            newlyUnlocked = slidesToUnlock,
            unlockedSlides = unlockedList,
            currentSlide = pres.CurrentSlide,
            totalSlides = pres.TotalSlides
        });

        LogEvent("unlock", presentationId, pres.SessionId, new { slides = slidesToUnlock, total_unlocked = unlockedList.Count });

        return Ok(new
        {
            status = "ok",
            unlocked_slides = unlockedList,
            newly_unlocked = slidesToUnlock
        });
    }

    [HttpGet("sessions/{id}/status")]
    public IActionResult GetStatus(string id)
    {
        var pres = _store.GetPresentationInfo(id) ?? _store.GetBySessionId(id);
        if (pres == null) return NotFound();

        List<int> unlocked;
        if (pres.State == "ended")
        {
            // If ended, unlock all slides
            unlocked = Enumerable.Range(1, pres.TotalSlides).ToList();
        }
        else
        {
            // True progressive unlock: return actual unlocked slides set
            unlocked = pres.GetUnlockedSlidesList();
        }

        return Ok(new
        {
            unlocked_slides = unlocked,
            current_slide = pres.CurrentSlide,
            total_slides = pres.TotalSlides,
            status = pres.State
        });
    }

    [HttpGet("sessions/{id}/replay")]
    public async Task<IActionResult> GetReplay(string id)
    {
        // id can be sessionId or presentationId. Try both.
        Presentation? pres = _store.GetPresentationInfo(id) ?? _store.GetBySessionId(id);

        var uploads = Path.Combine(AppContext.BaseDirectory, "uploads");
        string? archivePath = null;

        // Try finding by presentationId first if we have it
        if (pres != null)
        {
            archivePath = Path.Combine(uploads, pres.Id + "_archive.json");
        }
        else
        {
            // If pres is null, maybe the ID passed IS the presentationId?
            var pPath = Path.Combine(uploads, id + "_archive.json");
            if (System.IO.File.Exists(pPath)) archivePath = pPath;
            else
            {
                // Search archive files for a matching sessionId
                if (archivePath == null)
                {
                    var files = Directory.GetFiles(uploads, "*_archive.json");
                    foreach (var f in files)
                    {
                        var txt = await System.IO.File.ReadAllTextAsync(f);
                        if (txt.Contains($"\"sessionId\": \"{id}\""))
                        {
                            archivePath = f;
                            break;
                        }
                    }
                }
            }
        }

        if (archivePath == null || !System.IO.File.Exists(archivePath))
            return NotFound(new { error = "Session not found or not archived" });

        var content = await System.IO.File.ReadAllTextAsync(archivePath);
        var archiveData = JsonDocument.Parse(content).RootElement;

        // Fixup PDF URL to be absolute (if present)
        if (archiveData.ValueKind == JsonValueKind.Object && archiveData.TryGetProperty("pdfUrl", out var pdfUrlElement))
        {
            var backendBase = Request.Scheme + "://" + Request.Host.Value;
            var relativePdf = pdfUrlElement.GetString();
            if (!string.IsNullOrEmpty(relativePdf) && !relativePdf.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Archive saves "/uploads/..." — serve as-is; frontend handles relative URLs
            }
        }

        return Content(content, "application/json");
    }

    [HttpPost("sessions/end")]
    public async Task<IActionResult> EndSession()
    {
        var doc = await JsonDocument.ParseAsync(Request.Body);
        var root = doc.RootElement;
        string? presentationId = null;
        if (root.TryGetProperty("presentationId", out var pid)) presentationId = pid.GetString();

        string? token = null;
        if (Request.Headers.TryGetValue("X-Presenter-Token", out var t)) token = t.ToString();

        if (string.IsNullOrEmpty(presentationId) || !_store.ValidateToken(presentationId, token ?? string.Empty, out var _))
            return Unauthorized();

        _store.MarkEnded(presentationId);
        var pres = _store.GetPresentationInfo(presentationId);
        if (pres == null) return NotFound();

        // Resolve course and session IDs before the background task (needs DB access on request scope)
        Guid.TryParse(presentationId, out var bakeSessionGuid);
        var bakeSession = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == bakeSessionGuid);
        var bakeCourseId = bakeSession?.CourseId?.ToString("D") ?? "unknown";
        var bakeSessionIdStr = bakeSession?.Id.ToString("D") ?? presentationId;

        // enqueue bake job and return immediately
        if (bakeSession != null && bakeSession.CourseId.HasValue)
        {
            _logger.LogWarning("🚀🚀🚀 QUEUEING BAKE JOB FOR SESSION {SessionId} 🚀🚀🚀", bakeSession.Id);
            _bakingQueue.QueueBakeJob(new BakeJob
            {
                SessionId = bakeSession.Id,
                CourseId = bakeSession.CourseId.Value
            });
            _logger.LogInformation("Enqueued bake job for session {SessionId}", bakeSessionIdStr);
        }


        // Serialize archive to memory and upload to Supabase Storage
        var archiveData = new
        {
            sessionId = pres.SessionId,
            transcript = pres.TranscriptHistory.Select(t => new { timestamp = t.Timestamp, text = t.Text }).ToList(),
            duration = (DateTime.UtcNow - pres.StartTime).TotalSeconds,
            status = "archived"
        };
        var archiveJson = JsonSerializer.Serialize(archiveData, new JsonSerializerOptions { WriteIndented = true });
        var archiveBytes = Encoding.UTF8.GetBytes(archiveJson);

        // DB PERSISTENCE: Mark session ended, save transcript path, clear active session
        try
        {
            Guid.TryParse(presentationId, out var endSessionGuid);
            var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == endSessionGuid);

            if (session != null)
            {
                session.Status = SessionStatus.Ended;
                session.EndedAt = DateTime.UtcNow;

                // allow students to download automatically when the session ends
                session.AllowStudentDownload = true;

                // Upload transcript/archive to Supabase Storage
                string? transcriptStoragePath = null;
                try
                {
                    var storageService = _sp.GetService<IStorageService>();
                    var sessionStorageOptions = _sp.GetRequiredService<IOptions<SessionStorageOptions>>().Value;
                    if (storageService != null)
                    {
                        using var archiveStream = new MemoryStream(archiveBytes);
                        transcriptStoragePath = sessionStorageOptions.BuildTranscriptArchivePath(presentationId);
                        await storageService.UploadAsync(archiveStream, transcriptStoragePath, "application/json");
                    }
                }
                catch (Exception uploadEx)
                {
                    _logger.LogWarning(uploadEx, "Failed to upload transcript to Supabase Storage");
                }
                session.TranscriptStoragePath = transcriptStoragePath;

                // Clear active session on the course
                var sessionCourse = await _db.Courses.FirstOrDefaultAsync(c => c.Id == session.CourseId);
                if (sessionCourse != null && sessionCourse.ActiveSessionId == session.Id)
                {
                    sessionCourse.ActiveSessionId = null;
                }
                await _db.SaveChangesAsync();

                if (!string.IsNullOrEmpty(transcriptStoragePath))
                {
                    if (TryQueueTranscriptSummary(session.Id, transcriptStoragePath))
                    {
                        _logger.LogInformation(
                            "Queued transcript summary generation for session {SessionId}. Transcript archive path: {TranscriptStoragePath}",
                            session.Id,
                            transcriptStoragePath);
                    }
                }

                // Notify course group that session ended
                try
                {
                    if (sessionCourse != null)
                    {
                        await _hub.Clients.Group($"Course_{sessionCourse.Id}")
                            .SendAsync("SessionEnded", new { courseId = sessionCourse.Id, sessionId = pres.SessionId });
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update session end state in DB");
            // Log but don't fail the request
        }

        // Broadcast session ended — normalized group key
        var endGroupKey = NormalizeGroupKey(presentationId);
        Console.WriteLine($"[Broadcast] 📡 SessionEnded -> group '{endGroupKey}'");
        await _hub.Clients.Group(endGroupKey).SendAsync("SessionEnded");

        // Return download links
        var host = Request.Scheme + "://" + Request.Host.Value;
        var pptxUrl = $"{host}/download/{pres.Id}/pptx";
        var pdfUrl = $"{host}/download/{pres.Id}/pdf";

        return Ok(new
        {
            files = new { pptx_url = pptxUrl, pdf_url = pdfUrl },
            expiry = pres.TokenExpires
        });
    }

    [HttpPost("{presentationId}/start")]
    public async Task<IActionResult> Start(string presentationId)
    {
        string? token = null;
        if (Request.Headers.TryGetValue("X-Presenter-Token", out var t)) token = t.ToString();
        var tokenSample = token != null && token.Length > 8 ? token.Substring(0, 8) : token;
        _logger.LogInformation("start attempt: presentation={presentationId} tokenPresent={hasToken} tokenSample={tokenSample}",
            presentationId, token != null, tokenSample);

        if (!ValidatePresenter(presentationId, out var _))
        {
            _logger.LogWarning("start unauthorized: presentation={presentationId} tokenSample={tokenSample}", presentationId, tokenSample);
            return Unauthorized();
        }

        await JsonDocument.ParseAsync(Request.Body); // consume body
        _store.MarkRunning(presentationId);
        _logger.LogInformation("start ok: presentation={presentationId}", presentationId);
        return Ok();
    }

    [HttpPost("{presentationId}/slide")]
    public async Task<IActionResult> SetSlide(string presentationId)
    {
        if (!ValidatePresenter(presentationId, out var _)) return Unauthorized();
        var doc = await JsonDocument.ParseAsync(Request.Body);
        if (!doc.RootElement.TryGetProperty("slide", out var slideEl)) return BadRequest();
        var slide = slideEl.GetInt32();
        var unlock = doc.RootElement.TryGetProperty("unlock", out var u) && u.GetBoolean();

        // Update state
        _store.SetCurrentSlide(presentationId, slide);
        if (unlock) _store.UnlockSlide(presentationId, slide);

        var pres = _store.GetPresentationInfo(presentationId);
        if (pres == null) return NotFound();

        // ── DB write BEFORE broadcast (prevents 403 race) ──
        if (Guid.TryParse(presentationId, out var slideSessionGuid))
        {
            var dbSession = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == slideSessionGuid);
            if (dbSession != null)
            {
                if (slide > dbSession.CurrentSlideIndex)
                    dbSession.CurrentSlideIndex = slide;

                if (unlock)
                {
                    await _db.Database.ExecuteSqlRawAsync(@"
                        INSERT INTO session_unlocked_slides (""SessionId"", ""SlideIndex"", ""UnlockedAt"")
                        VALUES ({0}, {1}, (NOW() AT TIME ZONE 'Asia/Hong_Kong'))
                        ON CONFLICT DO NOTHING", slideSessionGuid, slide);
                }
                await _db.SaveChangesAsync();
                Console.WriteLine($"[/slide] ✅ DB committed slide {slide} (unlock={unlock}) for session {slideSessionGuid}");
            }
        }

        // Broadcast to connected WebSocket clients
        var msg = JsonSerializer.Serialize(new { type = "slide", presentationId, slide, unlock, timestamp = DateTime.UtcNow.ToString("o") });
        await _store.BroadcastAsync(presentationId, msg);

        // Broadcast to SignalR clients with full unlocked slides list (true progressive unlock)
        var unlockedList = pres.GetUnlockedSlidesList();
        var slideGroupKey = NormalizeGroupKey(presentationId);
        Console.WriteLine($"[Broadcast] 📡 SlideUnlocked (slide endpoint) -> group '{slideGroupKey}'");
        await _hub.Clients.Group(slideGroupKey).SendAsync("SlideUnlocked", new
        {
            slide,
            unlockedSlides = unlockedList,
            currentSlide = pres.CurrentSlide,
            totalSlides = pres.TotalSlides
        });

        LogEvent("slide", presentationId, pres.SessionId, new { slide, unlock, unlockedCount = unlockedList.Count });
        return Ok();
    }

    [HttpPost("{presentationId}/end")]
    public IActionResult EndPresentation(string presentationId)
    {
        if (!ValidatePresenter(presentationId, out var _)) return Unauthorized();
        _store.MarkEnded(presentationId);
        var pres = _store.GetPresentationInfo(presentationId);
        LogEvent("end", presentationId, pres?.SessionId, null);
        return Ok();
    }

    [HttpPost("transcript")]
    public async Task<IActionResult> PostTranscript()
    {
        // Accept presenter token or Bearer
        string? presentationId = null;
        string bodyText;
        using (var sr = new StreamReader(Request.Body)) bodyText = await sr.ReadToEndAsync();
        try
        {
            var doc = JsonDocument.Parse(bodyText);
            if (doc.RootElement.TryGetProperty("presentationId", out var pid)) presentationId = pid.GetString();
        }
        catch { }

        // Validate token if presenter action
        if (!string.IsNullOrEmpty(presentationId) && ValidatePresenter(presentationId, out var _))
        {
            var payloadEl = JsonDocument.Parse(bodyText).RootElement;
            var text = payloadEl.GetProperty("text").GetString();
            var transcriptEntry = new TranscriptEntry(DateTime.UtcNow, text ?? string.Empty);
            var transcriptPayload = new { timestamp = transcriptEntry.Timestamp, text = transcriptEntry.Text };

            var pres = _store.GetPresentationInfo(presentationId);
            LogEvent("transcript", presentationId, pres?.SessionId, transcriptPayload);

            // Accumulate transcript
            if (!string.IsNullOrEmpty(text))
            {
                _store.AppendTranscript(presentationId, transcriptEntry);
            }

            await _store.BroadcastAsync(presentationId!, JsonSerializer.Serialize(new { type = "transcript", payload = transcriptPayload }));
            if (pres != null)
            {
                await _hub.Clients.Group(NormalizeGroupKey(presentationId)).SendAsync("transcriptreceived", transcriptPayload);
                await _hub.Clients.Group(NormalizeGroupKey(presentationId)).SendAsync("transcript", transcriptPayload);
            }

            return Ok();
        }

        // If not presenter, allow Bearer user tokens for viewers
        var auth = Request.Headers["Authorization"].ToString();
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer "))
        {
            if (!string.IsNullOrEmpty(presentationId))
            {
                var payloadEl = JsonDocument.Parse(bodyText).RootElement;
                var text = payloadEl.TryGetProperty("text", out var tv) ? tv.GetString() : null;
                var transcriptPayload = new { timestamp = DateTime.UtcNow, text };
                var pres = _store.GetPresentationInfo(presentationId);
                LogEvent("transcript", presentationId, pres?.SessionId, transcriptPayload);
                await _store.BroadcastAsync(presentationId!, JsonSerializer.Serialize(new { type = "transcript", payload = transcriptPayload }));
                if (pres != null)
                {
                    await _hub.Clients.Group(NormalizeGroupKey(presentationId)).SendAsync("transcriptreceived", transcriptPayload);
                    await _hub.Clients.Group(NormalizeGroupKey(presentationId)).SendAsync("transcript", transcriptPayload);
                }
                return Ok();
            }
            return BadRequest(new { error = "presentationId required" });
        }

        return Unauthorized();
    }

    [HttpPost("drawing")]
    public async Task<IActionResult> PostDrawing()
    {
        if (!Request.HasFormContentType) return BadRequest(new { error = "multipart/form-data required" });
        var form = await Request.ReadFormAsync();
        var metadata = form["metadata"].ToString();
        if (string.IsNullOrEmpty(metadata)) return BadRequest(new { error = "metadata required" });

        JsonDocument metaDoc;
        try { metaDoc = JsonDocument.Parse(metadata); }
        catch { return BadRequest(new { error = "invalid metadata" }); }

        var presentationId = metaDoc.RootElement.GetProperty("presentationId").GetString();
        if (string.IsNullOrEmpty(presentationId)) return BadRequest(new { error = "presentationId required" });
        if (!ValidatePresenter(presentationId, out var _)) return Unauthorized();

        var image = form.Files.GetFile("slideImage");
        if (image != null && image.Length > 200 * 1024) return BadRequest(new { error = "image too large (max 200KB)" });

        var fingerprint = metaDoc.RootElement.GetProperty("fingerprint").GetString();
        if (_store.IsDuplicate(presentationId, fingerprint)) return Ok(new { status = "duplicate" });
        _store.AddFingerprint(presentationId, fingerprint);

        // Save image if present
        if (image != null)
        {
            var uploads = Path.Combine(AppContext.BaseDirectory, "uploads");
            Directory.CreateDirectory(uploads);
            var path = Path.Combine(uploads, Guid.NewGuid().ToString("n") + Path.GetExtension(image.FileName));
            await using var fs = System.IO.File.Create(path);
            await image.CopyToAsync(fs);
        }

        // Broadcast drawing
        await _store.BroadcastAsync(presentationId, JsonSerializer.Serialize(new { type = "live", action = "insert", payload = metaDoc.RootElement }));
        var presInfo = _store.GetPresentationInfo(presentationId);
        LogEvent("drawing", presentationId, presInfo?.SessionId, new { fingerprint });
        return Ok(new { status = "ok" });
    }

    [HttpPost("answers")]
    public async Task<IActionResult> PostAnswers()
    {
        using var sr = new StreamReader(Request.Body);
        var bodyText = await sr.ReadToEndAsync();
        var doc = JsonDocument.Parse(bodyText);
        var presentationId = doc.RootElement.GetProperty("presentationId").GetString();
        if (string.IsNullOrEmpty(presentationId)) return BadRequest(new { error = "presentationId required" });

        // Allow viewer answers with Bearer or presenter
        if (!ValidatePresenter(presentationId, out var _) && !IsBearerPresent()) return Unauthorized();

        await _store.BroadcastAsync(presentationId, JsonSerializer.Serialize(new { type = "answer", payload = doc.RootElement }));
        var pres = _store.GetPresentationInfo(presentationId);
        LogEvent("answer", presentationId, pres?.SessionId,
            new
            {
                questionId = doc.RootElement.GetProperty("questionId").GetString(),
                choice = doc.RootElement.GetProperty("choice").GetString()
            });
        return Ok();
    }

        [HttpPost("debug/sessions/{sessionId}/summary")]
        public async Task<IActionResult> DebugRegenerateSessionSummary(string sessionId)
        {
            if (!Guid.TryParse(sessionId, out var sessionGuid))
            {
                return BadRequest(new { error = "Invalid session ID" });
            }

            var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionGuid);
            if (session == null)
            {
                return NotFound(new { error = "Session not found" });
            }

            if (string.IsNullOrWhiteSpace(session.TranscriptStoragePath))
            {
                return NotFound(new { error = "Transcript archive path is missing" });
            }

            _logger.LogInformation(
                "Debug summary regeneration requested for session {SessionId}. Transcript archive path: {TranscriptStoragePath}",
                sessionId,
                session.TranscriptStoragePath);

            _logger.LogInformation(
                "Summary regeneration accepted for session {SessionId}. OpenRouter generation may take 60-120 seconds.",
                sessionId);

            TryQueueTranscriptSummary(sessionGuid, session.TranscriptStoragePath);

            return Accepted(new
            {
                sessionId = sessionGuid,
                transcriptStoragePath = session.TranscriptStoragePath,
                message = "Summary regeneration queued"
            });
        }

    private Task QueueTranscriptSummaryAsync(Guid sessionId, string transcriptStoragePath)
    {
        return Task.Run(async () =>
        {
            var workerStartedAtUtc = DateTime.UtcNow;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var archiveReader = scope.ServiceProvider.GetRequiredService<ITranscriptArchiveReader>();
                var openRouter = scope.ServiceProvider.GetRequiredService<IOpenRouterChatService>();
                var storage = scope.ServiceProvider.GetRequiredService<IStorageService>();
                var storageOptions = scope.ServiceProvider.GetRequiredService<IOptions<SessionStorageOptions>>().Value;
                var openRouterOptions = scope.ServiceProvider.GetRequiredService<IOptions<OpenRouterOptions>>().Value;

                _logger.LogInformation(
                    "Summary worker started for session {SessionId}. Transcript archive path: {TranscriptStoragePath}",
                    sessionId,
                    transcriptStoragePath);

                _logger.LogInformation(
                    "Summary worker queued OpenRouter generation for session {SessionId}. Expected latency: 60-120 seconds.",
                    sessionId);

                var archive = await archiveReader.LoadAsync(transcriptStoragePath, CancellationToken.None);
                if (archive == null || archive.Transcript.Count == 0)
                {
                    _logger.LogWarning("Summary generation skipped for session {SessionId}: transcript archive missing or empty.", sessionId);
                    return;
                }

                _logger.LogInformation(
                    "Summary worker loaded transcript archive for session {SessionId}. EntryCount={EntryCount}",
                    sessionId,
                    archive.Transcript.Count);

                var fullTranscript = archiveReader.BuildSummaryTranscript(archive.Transcript);
                if (string.IsNullOrWhiteSpace(fullTranscript))
                {
                    _logger.LogWarning("Summary generation skipped for session {SessionId}: transcript text was empty.", sessionId);
                    return;
                }

                var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId);
                if (session == null)
                {
                    _logger.LogWarning("Summary persistence skipped for session {SessionId}: session not found.", sessionId);
                    return;
                }

                var systemPrompt = "You are an academic summarization assistant. Summarize lecture transcripts into concise study notes in plain text. Do not use markdown tables or code fences.";
                var userPrompt = $"""
Presentation title: {session.PresentationTitle}

Transcript:
{fullTranscript}

Return a concise lecture summary with the main teaching points, important terminology, and any action items if present.
""";

                string? summary;
                try
                {
                    var openRouterStartedAtUtc = DateTime.UtcNow;
                    _logger.LogInformation("OpenRouter summary call started for session {SessionId}.", sessionId);
                    summary = await openRouter.GenerateAsync(systemPrompt, userPrompt, CancellationToken.None);
                    var openRouterDurationMs = (DateTime.UtcNow - openRouterStartedAtUtc).TotalMilliseconds;
                    _logger.LogInformation(
                        "OpenRouter summary call completed for session {SessionId} in {DurationMs} ms.",
                        sessionId,
                        Math.Round(openRouterDurationMs));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error generating summary for session {SessionId}", sessionId);
                    return;
                }

                if (string.IsNullOrWhiteSpace(summary))
                {
                    var elapsedMs = (DateTime.UtcNow - workerStartedAtUtc).TotalMilliseconds;
                    _logger.LogInformation(
                        "Summary generation returned no content for session {SessionId}. elapsedMs={ElapsedMs}",
                        sessionId,
                        Math.Round(elapsedMs));
                    return;
                }

                session.SummaryText = summary;
                await db.SaveChangesAsync(CancellationToken.None);

                var archiveWithSummary = new TranscriptArchiveDto
                {
                    SessionId = archive.SessionId,
                    Transcript = archive.Transcript,
                    Duration = archive.Duration,
                    Status = archive.Status,
                    Summary = summary
                };

                var summaryArtifact = new SessionSummaryArtifactDto
                {
                    SessionId = session.Id.ToString("n"),
                    PresentationTitle = session.PresentationTitle,
                    GeneratedAtUtc = DateTime.UtcNow,
                    Model = openRouterOptions.Model,
                    Summary = summary,
                    TranscriptStoragePath = transcriptStoragePath
                };

                try
                {
                    using var archiveUpdateStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(archiveWithSummary, new JsonSerializerOptions { WriteIndented = true })));
                    await storage.UploadToBucketAsync(archiveUpdateStream, storageOptions.BucketName, transcriptStoragePath, "application/json");
                    _logger.LogInformation(
                        "Transcript archive updated with summary for session {SessionId}. StoragePath={TranscriptStoragePath}",
                        sessionId,
                        transcriptStoragePath);
                }
                catch (Exception archiveUploadEx)
                {
                    _logger.LogWarning(archiveUploadEx, "Failed to update transcript archive with summary for session {SessionId}", sessionId);
                }

                try
                {
                    var summaryStoragePath = storageOptions.BuildSummaryArtifactPath(sessionId);
                    using var summaryStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(summaryArtifact, new JsonSerializerOptions { WriteIndented = true })));
                    await storage.UploadToBucketAsync(summaryStream, storageOptions.BucketName, summaryStoragePath, "application/json");
                    _logger.LogInformation(
                        "Summary artifact uploaded for session {SessionId}. StoragePath={SummaryStoragePath}",
                        sessionId,
                        summaryStoragePath);
                }
                catch (Exception summaryUploadEx)
                {
                    _logger.LogWarning(summaryUploadEx, "Failed to upload summary artifact for session {SessionId}", sessionId);
                }

                _logger.LogInformation(
                    "Summary persisted for session {SessionId}. SummaryLength={SummaryLength} totalElapsedMs={ElapsedMs}",
                    sessionId,
                    summary.Length,
                    Math.Round((DateTime.UtcNow - workerStartedAtUtc).TotalMilliseconds));

                try
                {
                    await _hub.Clients.Group(NormalizeGroupKey(sessionId.ToString("D")))
                        .SendAsync("summary_update", new { summary });
                }
                catch (Exception hubEx)
                {
                    _logger.LogWarning(hubEx, "Failed to broadcast summary update for session {SessionId}", sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate persisted transcript summary for session {SessionId}", sessionId);
            }
            finally
            {
                SummaryGenerationInFlight.TryRemove(sessionId, out _);
            }
        });
    }

    private bool TryQueueTranscriptSummary(Guid sessionId, string transcriptStoragePath)
    {
        if (!SummaryGenerationInFlight.TryAdd(sessionId, 0))
        {
            _logger.LogInformation(
                "Summary generation already in progress for session {SessionId}. Duplicate queue request ignored.",
                sessionId);
            return false;
        }

        _ = QueueTranscriptSummaryAsync(sessionId, transcriptStoragePath);
        return true;
    }
}
