using BackendServer.Features.Classroom;
using BackendServer.Features.Classroom.Hubs;
using BackendServer.Shared.Data;
using BackendServer.Shared.Data.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BackendServer.Extensions;

public static class WebApplicationExtensions
{
    /// <summary>
    /// Calls <see cref="Supabase.Client.InitializeAsync"/> on the singleton instance.
    /// Must be awaited before the app starts accepting requests.
    /// </summary>
    public static async Task InitializeSupabaseAsync(this WebApplication app)
    {
        var client = app.Services.GetRequiredService<Supabase.Client>();
        await client.InitializeAsync();
        app.Logger.LogInformation("Supabase client initialized.");
    }

    /// <summary>
    /// Retries <see cref="Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.CanConnectAsync"/>
    /// up to 30 times (1 second apart) before throwing.
    /// </summary>
    public static async Task VerifyDatabaseConnectionAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        const int maxAttempts = 30;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (await db.Database.CanConnectAsync())
            {
                log.LogInformation("Database connection verified successfully.");
                return;
            }

            log.LogWarning("Database not ready, waiting 1 s (attempt {attempt}/{max})", attempt + 1, maxAttempts);
            await Task.Delay(1000);
        }

        log.LogError("Database connection failed after {max} attempts.", maxAttempts);
        throw new Exception("Database connection failed after retries.");
    }

    /// <summary>
    /// Runs EF Core migrations (or EnsureCreated for non-relational providers),
    /// applies idempotent schema patches, and recovers stuck 'Active' sessions
    /// left over from a previous unclean shutdown.
    /// </summary>
    public static async Task RunMigrationsAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        // ── Migrations ────────────────────────────────────────────────────────
        if (db.Database.IsRelational())
            await db.Database.MigrateAsync();
        else
            await db.Database.EnsureCreatedAsync();

        // ── Idempotent column patches ─────────────────────────────────────────
        if (db.Database.IsRelational())
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE sessions ADD COLUMN IF NOT EXISTS ""CurrentSlideIndex"" integer NOT NULL DEFAULT 0;
                    ALTER TABLE sessions ADD COLUMN IF NOT EXISTS ""TotalSlides"" integer NOT NULL DEFAULT 0;
                    ALTER TABLE sessions ADD COLUMN IF NOT EXISTS ""AllowStudentDownload"" boolean NOT NULL DEFAULT false;
                    ALTER TABLE sessions ADD COLUMN IF NOT EXISTS ""DownloadAvailableAt"" timestamp with time zone;
                ");
                log.LogInformation("Session schema migration completed (slide index columns).");
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Session schema migration skipped (columns may already exist).");
            }
        }

        // ── Startup recovery: close sessions stuck as 'Active' after a crash ──
        try
        {
            var stuckSessions = db.Sessions
                .Where(s => s.Status == SessionStatus.Active)
                .ToList();

            if (stuckSessions.Count > 0)
            {
                log.LogWarning("Found {count} stuck active sessions — marking them as Ended.", stuckSessions.Count);

                foreach (var s in stuckSessions)
                {
                    s.Status = SessionStatus.Ended;
                    s.EndedAt = DateTime.UtcNow;

                    var course = db.Courses.FirstOrDefault(c => c.ActiveSessionId == s.Id);
                    if (course != null)
                        course.ActiveSessionId = null;
                }

                db.SaveChanges();

                // Best-effort: notify course dashboards via SignalR
                try
                {
                    var hubContext = scope.ServiceProvider.GetService<IHubContext<PresentationHub>>();
                    if (hubContext != null)
                    {
                        foreach (var s in stuckSessions)
                        {
                            var course = db.Courses.FirstOrDefault(c => c.Id == s.CourseId);
                            if (course != null)
                            {
                                await hubContext.Clients
                                    .Group($"Course_{course.Id}")
                                    .SendAsync("SessionEnded", new { courseId = course.Id, sessionId = s.Id });
                            }
                        }
                    }
                }
                catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to perform startup recovery for stuck sessions.");
        }
    }
}
