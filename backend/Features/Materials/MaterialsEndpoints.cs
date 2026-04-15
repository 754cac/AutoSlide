using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using BackendServer.Shared.Data;
using BackendServer.Shared.Data.Entities;
using BackendServer.Shared.Services;

namespace BackendServer.Features.Materials;

public static class MaterialsEndpoints
{
    public record MaterialResponse(
        Guid Id,
        Guid CourseId,
        string Title,
        string OriginalFileName,
        int Week,
        bool IsVisible,
        DateTime? ReleaseAt,
        DateTime UploadedAt);

    public record SignedUrlResponse(string Url, int ExpiresInSeconds);

    public record VisibilityRequest(bool IsVisible, DateTime? ReleaseAt);

    public static void MapMaterialsEndpoints(this WebApplication app)
    {
        var materials = app.MapGroup("/api").RequireAuthorization();

        // ── Task 3: Upload Material ──
        materials.MapPost("/courses/{courseId}/materials", async (
            Guid courseId,
            HttpRequest req,
            AppDbContext db,
            IStorageService storage,
            CancellationToken ct) =>
        {
            var userId = GetUserId(req.HttpContext);
            var role = GetRole(req.HttpContext);
            if (userId == null) return Results.Unauthorized();
            if (role != "Teacher") return Results.Forbid();

            var course = await db.Courses.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == courseId, ct);
            if (course == null) return Results.NotFound();
            if (course.TeacherId != userId.Value) return Results.Forbid();

            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await req.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "file required" });

            var title = form["title"].ToString();
            if (string.IsNullOrWhiteSpace(title))
                return Results.BadRequest(new { error = "title required" });

            if (!int.TryParse(form["week"].ToString(), out var week) || week < 0)
                return Results.BadRequest(new { error = "week must be >= 0" });

            var rawName = Path.GetFileName(file.FileName);  // strip any path components
            var safeName = Regex.Replace(rawName, @"[^\w.\-]", "_");  // replace unsafe chars
            var storagePath = $"{courseId}/{Guid.NewGuid()}/{safeName}";

            await using var stream = file.OpenReadStream();
            await storage.UploadAsync(stream, storagePath, file.ContentType ?? "application/octet-stream");

            var material = new Material
            {
                Id = Guid.NewGuid(),
                CourseId = courseId,
                Title = title,
                // store the stripped name to avoid leaking local paths
                OriginalFileName = rawName,
                StoragePath = storagePath,
                Week = week,
                IsVisible = false,
                UploadedAt = DateTime.UtcNow
            };

            db.Materials.Add(material);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/materials/{material.Id}", ToResponse(material));
        });

        // ── Task 4: Get Signed URL ──
        materials.MapGet("/materials/{materialId}/url", async (
            Guid materialId,
            HttpContext httpCtx,
            AppDbContext db,
            IStorageService storage,
            StorageAuthorizationService authz,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpCtx);
            var role = GetRole(httpCtx);
            if (userId == null) return Results.Unauthorized();

            if (!Enum.TryParse<UserRole>(role, true, out var userRole))
                return Results.Forbid();

            var canAccess = await authz.CanAccessMaterialAsync(userId.Value, userRole, materialId, ct);
            if (!canAccess) return Results.Forbid();

            var material = await db.Materials.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == materialId, ct);
            if (material == null) return Results.NotFound();

            var url = await storage.GetSignedUrlAsync(material.StoragePath, 300);

            return Results.Ok(new SignedUrlResponse(url, 300));
        });

        // ── Task 5: Toggle Visibility ──
        materials.MapPatch("/materials/{materialId}/visibility", async (
            Guid materialId,
            VisibilityRequest body,
            HttpContext httpCtx,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpCtx);
            var role = GetRole(httpCtx);
            if (userId == null) return Results.Unauthorized();
            if (role != "Teacher") return Results.Forbid();

            var material = await db.Materials
                .FirstOrDefaultAsync(m => m.Id == materialId, ct);
            if (material == null) return Results.NotFound();

            var course = await db.Courses.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == material.CourseId, ct);
            if (course == null || course.TeacherId != userId.Value)
                return Results.Forbid();

            material.IsVisible = body.IsVisible;
            material.ReleaseAt = body.ReleaseAt;
            await db.SaveChangesAsync(ct);

            return Results.Ok(ToResponse(material));
        });

        // ── Task 7: Delete Material ──
        materials.MapDelete("/materials/{materialId}", async (
            Guid materialId,
            HttpContext httpCtx,
            AppDbContext db,
            IStorageService storage,
            CancellationToken ct) =>
        {
            var userId = GetUserId(httpCtx);
            var role = GetRole(httpCtx);
            if (userId == null) return Results.Unauthorized();
            if (role != "Teacher") return Results.Forbid();

            var material = await db.Materials
                .FirstOrDefaultAsync(m => m.Id == materialId, ct);
            if (material == null) return Results.NotFound();

            var course = await db.Courses.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == material.CourseId, ct);
            if (course == null || course.TeacherId != userId.Value)
                return Results.Forbid();

            await storage.DeleteAsync(material.StoragePath);
            db.Materials.Remove(material);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });
    }

    private static Guid? GetUserId(HttpContext ctx)
    {
        var claim = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static string? GetRole(HttpContext ctx)
        => ctx.User.FindFirstValue(ClaimTypes.Role);

    private static MaterialResponse ToResponse(Material m)
        => new(m.Id, m.CourseId, m.Title, m.OriginalFileName,
               m.Week, m.IsVisible, m.ReleaseAt, m.UploadedAt);
}
