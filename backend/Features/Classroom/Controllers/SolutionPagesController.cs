using System.Security.Claims;
using BackendServer.Features.Classroom;
using BackendServer.Features.Classroom.Services;
using BackendServer.Features.Materials.Services;
using BackendServer.Shared.Data;
using BackendServer.Shared.Data.Entities;
using BackendServer.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendServer.Features.Classroom.Controllers;

[ApiController]
[Route("api/sessions/{sessionId}")]
public sealed class SolutionPagesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PresentationStore _presentationStore;
    private readonly ISolutionPageService _solutionPageService;
    private readonly PdfService _pdfService;
    private readonly IStorageService _storage;
    private readonly ILogger<SolutionPagesController> _logger;

    public SolutionPagesController(
        AppDbContext db,
        PresentationStore presentationStore,
        ISolutionPageService solutionPageService,
        PdfService pdfService,
        IStorageService storage,
        ILogger<SolutionPagesController> logger)
    {
        _db = db;
        _presentationStore = presentationStore;
        _solutionPageService = solutionPageService;
        _pdfService = pdfService;
        _storage = storage;
        _logger = logger;
    }

    public sealed class CreateSolutionPageApiRequest
    {
        public string Kind { get; set; } = "blank";
        public int? SourceSlideIndex { get; set; }
        public int? OrderIndex { get; set; }
    }

    [HttpPost("solutions")]
    public async Task<IActionResult> CreateSolutionPage(Guid sessionId, [FromBody] CreateSolutionPageApiRequest? request)
    {
        if (!TryAuthorizePresenter(sessionId, out var token))
            return Unauthorized(new { error = "presenter token required" });

        var sessionExists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId);
        if (!sessionExists)
            return NotFound(new { error = "session not found" });

        request ??= new CreateSolutionPageApiRequest();

        var created = await _solutionPageService.CreateSolutionPageAsync(
            sessionId,
            new CreateSolutionPageServiceRequest
            {
                Kind = request.Kind,
                SourceSlideIndex = request.SourceSlideIndex,
                OrderIndex = request.OrderIndex
            });

        _logger.LogInformation(
            "Solution page created: session={SessionId} page={SolutionPageId} kind={Kind} sourceSlide={SourceSlide} order={OrderIndex} tokenPrefix={TokenPrefix}",
            sessionId,
            created.SolutionPageId,
            created.Kind,
            created.SourceSlideIndex,
            created.OrderIndex,
            token?.Length > 8 ? token[..8] : token);

        return Ok(ToApiResponse(created, imageUrl: null));
    }

    [HttpPut("solutions/{solutionPageId}")]
    public async Task<IActionResult> UpdateSolutionPage(
        Guid sessionId,
        string solutionPageId,
        [FromQuery] bool hasInk = false,
        [FromQuery] string? kind = null,
        [FromQuery] int? sourceSlideIndex = null,
        [FromQuery] int? orderIndex = null)
    {
        if (!TryAuthorizePresenter(sessionId, out _))
            return Unauthorized(new { error = "presenter token required" });

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var pngBytes = ms.ToArray();

        if (pngBytes.Length == 0)
            return BadRequest(new { error = "empty image data" });

        var updated = await _solutionPageService.UpdateSolutionArtifactAsync(
            sessionId,
            solutionPageId,
            pngBytes,
            hasInk,
            new CreateSolutionPageServiceRequest
            {
                Kind = kind ?? "blank",
                SourceSlideIndex = sourceSlideIndex,
                OrderIndex = orderIndex
            });
        if (updated == null)
            return NotFound(new { error = "solution page not found" });

        var signedUrl = await _solutionPageService.TryGetSignedImageUrlAsync(updated, 900);
        return Ok(ToApiResponse(updated, signedUrl));
    }

    [Authorize]
    [HttpGet("solutions")]
    public async Task<IActionResult> ListSolutionPages(Guid sessionId)
    {
        try
        {
            _logger.LogInformation("List solution pages requested for session={SessionId}", sessionId);

            if (!await AuthorizeReadAccessAsync(sessionId))
                return Forbid();

            var items = await _solutionPageService.ListSolutionPagesAsync(sessionId);
            var payload = new List<object>(items.Count);
            foreach (var item in items)
            {
                var signedUrl = await _solutionPageService.TryGetSignedImageUrlAsync(item, 900);
                payload.Add(ToApiResponse(item, signedUrl));
            }

            return Ok(new { items = payload });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "List solution pages failed for session={SessionId}", sessionId);
            return StatusCode(500, new { error = "failed to list solution pages" });
        }
    }

    [Authorize]
    [HttpGet("solutions/{solutionPageId}")]
    public async Task<IActionResult> GetSolutionPage(Guid sessionId, string solutionPageId)
    {
        try
        {
            if (!await AuthorizeReadAccessAsync(sessionId))
                return Forbid();

            var item = await _solutionPageService.GetSolutionPageAsync(sessionId, solutionPageId);
            if (item == null)
                return NotFound(new { error = "solution page not found" });

            var signedUrl = await _solutionPageService.TryGetSignedImageUrlAsync(item, 900);
            return Ok(ToApiResponse(item, signedUrl));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get solution page failed for session={SessionId} solutionPageId={SolutionPageId}", sessionId, solutionPageId);
            return StatusCode(500, new { error = "failed to get solution page" });
        }
    }

    [Authorize]
    [HttpPost("exports/with-solutions")]
    public async Task<IActionResult> GenerateExportWithSolutions(Guid sessionId)
    {
        if (!await AuthorizeReadAccessAsync(sessionId))
            return Forbid();

        var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session == null)
            return NotFound(new { error = "session not found" });

        if (!session.CourseId.HasValue)
            return BadRequest(new { error = "session is not linked to a course" });

        var baseDeckPdf = await _pdfService.GetAnnotatedPdfBytesAsync(sessionId.ToString("N"));
        if (baseDeckPdf == null || baseDeckPdf.Length == 0)
            return NotFound(new { error = "base deck PDF not found" });

        var mergedBytes = await _solutionPageService.AppendSolutionsToDeckPdfAsync(sessionId, baseDeckPdf, skipEmptyPages: true);

        var storagePath = $"{session.CourseId:D}/{sessionId:D}/inked-with-solutions.pdf";
        await using (var stream = new MemoryStream(mergedBytes, writable: false))
        {
            await _storage.UploadToBucketAsync(stream, "slides", storagePath, "application/pdf");
        }

        var downloadFileName = BuildWithSolutionsFileName(session.PresentationTitle);
        var signedUrl = await _storage.GetSignedDownloadUrlAsync("slides", storagePath, downloadFileName, 7200);

        return Ok(new
        {
            sessionId,
            fileName = downloadFileName,
            storagePath,
            url = signedUrl
        });
    }

    private bool TryAuthorizePresenter(Guid sessionId, out string? presenterToken)
    {
        presenterToken = null;
        if (Request.Headers.TryGetValue("X-Presenter-Token", out var tokenValues))
            presenterToken = tokenValues.ToString();

        if (string.IsNullOrWhiteSpace(presenterToken))
            return false;

        return _presentationStore.ValidateToken(sessionId.ToString("D"), presenterToken, out _)
            || _presentationStore.ValidateToken(sessionId.ToString("N"), presenterToken, out _);
    }

    private async Task<bool> AuthorizeReadAccessAsync(Guid sessionId)
    {
        if (TryAuthorizePresenter(sessionId, out _))
            return true;

        if (User?.Identity?.IsAuthenticated != true)
            return false;

        var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session == null)
            return false;

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
            return false;

        var role = User.FindFirstValue(ClaimTypes.Role);
        bool isTeacherRole = string.Equals(role, "Teacher", StringComparison.OrdinalIgnoreCase);

        if (!session.CourseId.HasValue)
            return isTeacherRole;

        var course = await _db.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == session.CourseId.Value);
        var isInstructor = course != null && course.TeacherId == userId;

        if (isTeacherRole && isInstructor)
            return true;

        if (isInstructor)
            return true;

        return await _db.Enrollments.AsNoTracking().AnyAsync(e =>
            e.CourseId == session.CourseId.Value &&
            e.StudentId == userId &&
            e.Status == EnrollmentStatus.Enrolled);
    }

    private static object ToApiResponse(SolutionPageMetadata metadata, string? imageUrl)
    {
        return new
        {
            sessionId = metadata.SessionId,
            solutionPageId = metadata.SolutionPageId,
            orderIndex = metadata.OrderIndex,
            kind = metadata.Kind,
            sourceSlideIndex = metadata.SourceSlideIndex,
            storagePath = metadata.StoragePath,
            hasInk = metadata.HasInk,
            createdAt = metadata.CreatedAt,
            updatedAt = metadata.UpdatedAt,
            imageUrl
        };
    }

    private static string BuildWithSolutionsFileName(string? presentationTitle)
    {
        var baseName = Path.GetFileNameWithoutExtension(presentationTitle);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "presentation";

        return baseName + "_inked_with_solutions.pdf";
    }
}
