using Microsoft.AspNetCore.Mvc;
using BackendServer.Features.Classroom;
using BackendServer.Features.Materials.Services;

namespace BackendServer.Features.Classroom.Controllers;

/// <summary>
/// Handles ink annotation upload/download endpoints.
/// Extracted from Program.cs minimal-API handlers.
/// </summary>
[ApiController]
[Route("api/sessions")]
public class InkController : ControllerBase
{
    private readonly PresentationStore _store;
    private readonly PdfService _pdfService;
    private readonly PptxService _pptxService;
    private readonly ILogger<InkController> _logger;

    public InkController(
        PresentationStore store,
        PdfService pdfService,
        PptxService pptxService,
        ILogger<InkController> logger)
    {
        _store = store;
        _pdfService = pdfService;
        _pptxService = pptxService;
        _logger = logger;
    }

    // ============================================================================
    // INK OVERLAY UPLOAD ENDPOINTS
    // Receives PNG ink annotations from the WPF overlay.
    // Legacy upload routes below are used for real-time/replay ink overlays.
    // Ink artifact routes are slide-keyed and store one snapshot per source slide.
    // ============================================================================
    [HttpPost("{presentationId}/slides/{slideIndex}/ink")]
    public async Task<IActionResult> UploadInk(string presentationId, int slideIndex)
    {
        return await SaveInkAsync(presentationId, slideIndex, isFrameIndex: false);
    }

    [HttpPost("{presentationId}/frames/{frameIndex}/ink")]
    public async Task<IActionResult> UploadInkByFrame(string presentationId, int frameIndex)
    {
        return await SaveInkAsync(presentationId, frameIndex, isFrameIndex: true);
    }

    [HttpPost("{sessionId:guid}/exports/ink-artifacts/slides/{slideIndex:int}")]
    public async Task<IActionResult> UploadInkArtifactBySlide(string sessionId, int slideIndex)
    {
        if (slideIndex < 1)
            return BadRequest(new { error = "invalid slide index" });

        string? token = null;
        if (Request.Headers.TryGetValue("X-Presenter-Token", out var t)) token = t.ToString();

        if (string.IsNullOrEmpty(token) || !_store.ValidateToken(sessionId, token, out var _))
        {
            _logger.LogWarning(
                "ink artifact upload unauthorized: session={sessionId} slide={slideIndex}",
                sessionId,
                slideIndex);
            return Unauthorized();
        }

        var pres = _store.GetPresentationInfo(sessionId);
        if (pres == null)
            return NotFound(new { error = "session not found" });
        if (slideIndex > pres.TotalSlides)
            return BadRequest(new { error = "invalid slide index" });

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var inkBytes = ms.ToArray();

        if (inkBytes.Length == 0)
            return BadRequest(new { error = "empty ink artifact data" });

        if (!Guid.TryParse(sessionId, out var sessionGuid))
            return BadRequest(new { error = "invalid session id" });
        var safeSessionId = sessionGuid.ToString("D");
        var baseDir = Path.Combine(AppContext.BaseDirectory, "uploads", safeSessionId, "ink-artifacts");
        Directory.CreateDirectory(baseDir);

        var fileName = $"slide_{slideIndex:D3}.png";
        var fullPath = Path.Combine(baseDir, fileName);
        await System.IO.File.WriteAllBytesAsync(fullPath, inkBytes);

        _logger.LogInformation(
            "Ink artifact saved for session {sessionId} slide {slideIndex}: {filePath} ({bytes} bytes)",
            sessionId,
            slideIndex,
            fullPath,
            inkBytes.Length);

        return Ok(new
        {
            status = "ok",
            sessionId,
            slideIndex,
            file = fileName,
            bytes = inkBytes.Length
        });
    }

    [HttpDelete("{sessionId:guid}/exports/ink-artifacts/slides/{slideIndex:int}")]
    public IActionResult DeleteInkArtifactBySlide(string sessionId, int slideIndex)
    {
        if (slideIndex < 1)
            return BadRequest(new { error = "invalid slide index" });

        string? token = null;
        if (Request.Headers.TryGetValue("X-Presenter-Token", out var t)) token = t.ToString();

        if (string.IsNullOrEmpty(token) || !_store.ValidateToken(sessionId, token, out var _))
        {
            _logger.LogWarning(
                "ink artifact delete unauthorized: session={sessionId} slide={slideIndex}",
                sessionId,
                slideIndex);
            return Unauthorized();
        }

        if (!Guid.TryParse(sessionId, out var sessionGuid))
            return BadRequest(new { error = "invalid session id" });
        var safeSessionId = sessionGuid.ToString("D");
        var baseDir = Path.Combine(AppContext.BaseDirectory, "uploads", safeSessionId, "ink-artifacts");
        var fullPath = Path.Combine(baseDir, $"slide_{slideIndex:D3}.png");

        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { error = "ink artifact not found" });

        System.IO.File.Delete(fullPath);
        _logger.LogInformation("Ink artifact deleted for session {sessionId} slide {slideIndex}", sessionId, slideIndex);

        return Ok(new
        {
            status = "ok",
            sessionId,
            slideIndex,
            deleted = true
        });
    }

    private async Task<IActionResult> SaveInkAsync(string presentationId, int index, bool isFrameIndex)
    {
        string indexLabel = isFrameIndex ? "frame" : "slide";

        string? token = null;
        if (Request.Headers.TryGetValue("X-Presenter-Token", out var t)) token = t.ToString();

        if (string.IsNullOrEmpty(token) || !_store.ValidateToken(presentationId, token, out var _))
        {
            _logger.LogWarning(
                "ink upload unauthorized: presentation={presentationId} {indexLabel}={index}",
                presentationId,
                indexLabel,
                index);
            return Unauthorized();
        }

        var pres = _store.GetPresentationInfo(presentationId);
        if (pres == null) return NotFound(new { error = "presentation not found" });

        if (index < 1)
        {
            return BadRequest(new { error = $"invalid {indexLabel} index" });
        }

        if (!isFrameIndex && index > pres.TotalSlides)
        {
            return BadRequest(new { error = "invalid slide index" });
        }

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var inkBytes = ms.ToArray();

        if (inkBytes.Length == 0)
        {
            return BadRequest(new { error = "empty ink data" });
        }

        var uploads = Path.Combine(AppContext.BaseDirectory, "uploads");
        Directory.CreateDirectory(uploads);

        var keyPrefix = isFrameIndex ? "frame" : "slide";
        var inkFileName = $"{presentationId}_{keyPrefix}{index}_ink.png";
        var inkFilePath = Path.Combine(uploads, inkFileName);

        await System.IO.File.WriteAllBytesAsync(inkFilePath, inkBytes);

        _logger.LogInformation(
            "Ink saved for presentation {presentationId} {indexLabel} {index}: {inkFileName} ({bytes} bytes)",
            presentationId,
            indexLabel,
            index,
            inkFileName,
            inkBytes.Length);

        return Ok(new
        {
            status = "ok",
            keyType = isFrameIndex ? "frame" : "slide",
            index,
            inkFile = inkFileName,
            bytes = inkBytes.Length
        });
    }

    // ============================================================================
    // ANNOTATED PDF DOWNLOAD ENDPOINT
    // Merges ink overlay PNGs onto the original PDF and returns the combined file.
    // Students can download this to get the presentation with teacher's annotations.
    // ============================================================================
    [HttpGet("{id}/download-annotated")]
    public async Task<IActionResult> DownloadAnnotated(string id)
    {
        // id can be presentationId or sessionId
        var pres = _store.GetPresentationInfo(id) ?? _store.GetBySessionId(id);
        if (pres == null)
        {
            return NotFound(new { error = "Presentation not found" });
        }

        byte[]? mergedPdf;
        try
        {
            mergedPdf = await _pdfService.GetAnnotatedPdfBytesAsync(pres.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating annotated PDF for presentation {id}", pres.Id);
            return StatusCode(500);
        }

        if (mergedPdf == null || mergedPdf.Length == 0)
        {
            return NotFound(new { error = "PDF file not found" });
        }

        // Generate filename
        var safeName = pres.Name?.Replace(" ", "_") ?? "presentation";
        var downloadName = $"{safeName}_Annotated.pdf";

        return File(mergedPdf, "application/pdf", downloadName);
    }

    // ============================================================================
    // ANNOTATED PPTX DOWNLOAD ENDPOINT
    // Merges ink overlay PNGs into the original PPTX file structure.
    // Students can download this to get an editable PowerPoint with annotations.
    // ============================================================================
    [HttpGet("{id}/download-annotated-pptx")]
    public async Task<IActionResult> DownloadAnnotatedPptx(string id)
    {
        // id can be presentationId or sessionId
        var pres = _store.GetPresentationInfo(id) ?? _store.GetBySessionId(id);
        if (pres == null)
        {
            return NotFound(new { error = "Presentation not found" });
        }

        byte[]? mergedPptx;
        try
        {
            mergedPptx = await _pptxService.GetAnnotatedPptxBytesAsync(pres.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating annotated PPTX for presentation {id}", pres.Id);
            return StatusCode(500);
        }

        if (mergedPptx == null || mergedPptx.Length == 0)
        {
            return NotFound(new { error = "PPTX file not found" });
        }

        // Generate filename
        var safeName = pres.Name?.Replace(" ", "_") ?? "presentation";
        var downloadName = $"{safeName}_Annotated.pptx";

        return File(mergedPptx, "application/vnd.openxmlformats-officedocument.presentationml.presentation", downloadName);
    }
}
