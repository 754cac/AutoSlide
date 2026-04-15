using System.Diagnostics;
using BackendServer.Configuration;
using BackendServer.Shared.Services;
using BackendServer.Extensions;
using BackendServer.Features.Classroom;
using BackendServer.Features.Classroom.DTOs;
using BackendServer.Features.Classroom.Hubs;
using BackendServer.Features.Materials;
using BackendServer.Features.Materials.Services;
using BackendServer.Shared.Data;
using BackendServer.Shared.Data.Entities;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static BackendServer.Extensions.ServiceCollectionExtensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddKestrelConfig();
builder.Services.AddDatabaseServices(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddSupabaseServices(builder.Configuration);
builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddCorsPolicies(builder.Configuration);
builder.Services.AddRouting();
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaterialUploadMaxBytes);

builder.Services.AddSingleton<BackendServer.Features.Classroom.Services.IArtifactBakingQueue, BackendServer.Features.Classroom.Services.ArtifactBakingQueue>();
builder.Services.AddHostedService<BackendServer.Features.Classroom.Services.ArtifactBakingWorker>();

var app = builder.Build();

app.Logger.LogInformation("Viewer CORS origin: {origin}",
    builder.Configuration["VIEWER_BASE_URL"] ?? "http://localhost:3000");

var openRouterApiKey = OpenRouterDiagnostics.ResolveApiKey(builder.Configuration);
var summarizerProvider = OpenRouterDiagnostics.ResolveProvider(builder.Configuration);
var openRouterModel = OpenRouterDiagnostics.ResolveModel(builder.Configuration);
var ollamaBaseUrl = OpenRouterDiagnostics.ResolveOllamaBaseUrl(builder.Configuration);
var ollamaModel = OpenRouterDiagnostics.ResolveOllamaModel(builder.Configuration);
var openRouterFallbackModels = OpenRouterDiagnostics.ResolveFallbackModels(builder.Configuration);
var openRouterSource = OpenRouterDiagnostics.ResolveSource(builder.Configuration);
var openRouterFallbackSource = OpenRouterDiagnostics.ResolveFallbackModelsSource(builder.Configuration);
if (string.Equals(summarizerProvider, "ollama", StringComparison.OrdinalIgnoreCase))
{
    app.Logger.LogInformation(
        "AI summarization enabled via Ollama. provider={Provider} baseUrl={BaseUrl} model={Model}",
        summarizerProvider,
        ollamaBaseUrl,
        ollamaModel);
}
else if (string.IsNullOrWhiteSpace(openRouterApiKey))
{
    app.Logger.LogWarning("OpenRouter API key not set. Recommended env key: OpenRouter__ApiKey.");
}
else
    app.Logger.LogInformation(
        "OpenRouter summarization enabled. provider={Provider} source={Source} keyPreview={KeyPreview} model={Model} fallbackSource={FallbackSource} fallbackModels={FallbackModels}",
        summarizerProvider,
        openRouterSource,
        OpenRouterDiagnostics.MaskSecret(openRouterApiKey),
        openRouterModel,
        openRouterFallbackSource,
        openRouterFallbackModels);

var sessionStorageOptions = builder.Configuration.GetSection(SessionStorageOptions.SectionName).Get<SessionStorageOptions>() ?? new SessionStorageOptions();
app.Logger.LogInformation(
    "Session storage configured. bucket={Bucket} transcriptPrefix={TranscriptPrefix} summaryPrefix={SummaryPrefix}",
    sessionStorageOptions.BucketName,
    sessionStorageOptions.TranscriptPrefix,
    sessionStorageOptions.SummaryPrefix);

await app.InitializeSupabaseAsync();
await app.VerifyDatabaseConnectionAsync();
await app.RunMigrationsAsync();

app.UseRouting();
app.UseCors("AllowViewer");
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapMaterialsEndpoints();
app.MapHub<PresentationHub>("/hubs/presentation");

app.MapPost("/api/upload",
    async (HttpRequest req, UploadService svc) => await svc.HandleUploadAsync(req));

app.MapPost("/api/presentations/upload",
    () => Results.Redirect("/api/upload", permanent: true, preserveMethod: true));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/info/{presentationId}", (HttpRequest req, string presentationId, PresentationStore store) =>
{
    var uploads = Path.Combine(AppContext.BaseDirectory, "uploads");
    string? chosen = null;
    if (File.Exists(Path.Combine(uploads, presentationId + ".pdf"))) chosen = presentationId + ".pdf";
    else if (File.Exists(Path.Combine(uploads, presentationId + ".summary.pdf"))) chosen = presentationId + ".summary.pdf";
    if (chosen == null) return Results.NotFound(new { error = "pdf not found" });

    var host = req.Scheme + "://" + req.Host.Value;
    var url = host + "/uploads/" + chosen;
    var pres = store.GetPresentationInfo(presentationId);
    return pres == null
        ? Results.Ok(new { presentationId, pdf = url, name = (string?)null, sessionId = (string?)null })
        : Results.Ok(new { presentationId, pdf = url, name = pres.Name, sessionId = pres.SessionId });
});

app.MapGet("/api/info/session/{sessionId}", async (
    HttpRequest req, string sessionId, PresentationStore store, AppDbContext db) =>
{
    var backendBase = req.Scheme + "://" + req.Host.Value;
    var uploads = Path.Combine(AppContext.BaseDirectory, "uploads");

    var pres = store.GetBySessionId(sessionId);
    if (pres != null)
    {
        string? chosen = null;
        if (File.Exists(Path.Combine(uploads, pres.Id + ".pdf"))) chosen = pres.Id + ".pdf";
        else if (File.Exists(Path.Combine(uploads, pres.Id + ".summary.pdf"))) chosen = pres.Id + ".summary.pdf";
        if (chosen == null) return Results.NotFound(new { error = "pdf not found" });

        var downloadUrl = pres.State == "ended" ? $"{backendBase}/download/{pres.Id}/pdf" : null;
        return Results.Ok(new
        {
            presentationId = pres.Id,
            pdf = backendBase + "/uploads/" + chosen,
            name = pres.Name,
            sessionId = pres.SessionId,
            status = pres.State,
            downloadUrl
        });
    }

    Guid.TryParse(sessionId, out var sessionGuid);
    var dbSession = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionGuid);
    if (dbSession == null) return Results.NotFound(new { error = "presentation not found" });

    var idN = dbSession.Id.ToString("n");
    string? dbChosen = null;
    if (File.Exists(Path.Combine(uploads, idN + ".pdf"))) dbChosen = idN + ".pdf";
    else if (File.Exists(Path.Combine(uploads, idN + ".summary.pdf"))) dbChosen = idN + ".summary.pdf";
    if (dbChosen == null) return Results.NotFound(new { error = "pdf not found" });

    var normalizedStatus = dbSession.Status == SessionStatus.Ended ? "ended" : "active";
    var dbDownloadUrl = normalizedStatus == "ended" ? $"{backendBase}/download/{idN}/pdf" : null;
    return Results.Ok(new
    {
        presentationId = idN,
        pdf = backendBase + "/uploads/" + dbChosen,
        name = dbSession.PresentationTitle,
        sessionId = (string?)null,
        status = normalizedStatus,
        downloadUrl = dbDownloadUrl
    });
});

app.MapGet("/live/{presentationId}", async (
    HttpContext context, string presentationId, PresentationStore store) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    var token = context.Request.Headers["X-Presenter-Token"].ToString();
    var ws = await context.WebSockets.AcceptWebSocketAsync();
    await store.RegisterSocketAsync(presentationId, ws, token);
});

app.MapGet("/download/{id}/{type}", (string id, string type, PresentationStore store) =>
{
    var pres = store.GetPresentationInfo(id);
    if (pres == null) return Results.NotFound();

    var uploads = Path.Combine(AppContext.BaseDirectory, "uploads");
    if (type == "pptx")
    {
        if (!File.Exists(pres.UploadedFile)) return Results.NotFound();
        var name = pres.Name;
        if (!name.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".ppt", StringComparison.OrdinalIgnoreCase))
            name += Path.GetExtension(pres.UploadedFile);
        return Results.File(pres.UploadedFile,
            "application/vnd.openxmlformats-officedocument.presentationml.presentation", name);
    }
    if (type == "pdf")
    {
        var pdfPath = Path.Combine(uploads, id + ".pdf");
        if (!File.Exists(pdfPath)) return Results.NotFound();
        return Results.File(pdfPath, "application/pdf", Path.GetFileNameWithoutExtension(pres.Name) + ".pdf");
    }
    return Results.BadRequest();
});

app.MapGet("/api/debug/session-token/{id}", async (string id, PresentationStore store, AppDbContext db) =>
{
    var pres = store.GetPresentationInfo(id) ?? store.GetBySessionId(id);
    if (pres != null)
        return Results.Ok(new
        {
            presentationId = pres.Id,
            sessionId = pres.SessionId,
            presenterToken = pres.PresenterToken,
            unlocked = pres.GetUnlockedSlidesList()
        });

    Guid.TryParse(id, out var g);
    var dbSess = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == g);
    return dbSess != null
        ? Results.Ok(new { presentationId = dbSess.Id.ToString("n"), presenterToken = dbSess.PresenterToken, status = dbSess.Status.ToString(), unlocked = Array.Empty<int>() })
        : Results.NotFound();
});

app.MapGet("/api/debug/openrouter", (IConfiguration configuration) =>
{
    var provider = OpenRouterDiagnostics.ResolveProvider(configuration);
    var apiKey = OpenRouterDiagnostics.ResolveApiKey(configuration);
    var model = OpenRouterDiagnostics.ResolveModel(configuration);
    var resolvedOllamaBaseUrl = OpenRouterDiagnostics.ResolveOllamaBaseUrl(configuration);
    var resolvedOllamaModel = OpenRouterDiagnostics.ResolveOllamaModel(configuration);
    var fallbackModels = OpenRouterDiagnostics.ResolveFallbackModels(configuration);

    return Results.Ok(new
    {
        provider,
        source = OpenRouterDiagnostics.ResolveSource(configuration),
        fallbackSource = OpenRouterDiagnostics.ResolveFallbackModelsSource(configuration),
        keyConfigured = !string.IsNullOrWhiteSpace(apiKey),
        keyPreview = OpenRouterDiagnostics.MaskSecret(apiKey),
        model,
        ollamaBaseUrl = resolvedOllamaBaseUrl,
        ollamaModel = resolvedOllamaModel,
        fallbackModels,
        providerFromConfig = configuration["OpenRouter:Provider"],
        ollamaBaseUrlFromConfig = configuration["OpenRouter:OllamaBaseUrl"],
        ollamaModelFromConfig = configuration["OpenRouter:OllamaModel"],
        configKeyPresent = !string.IsNullOrWhiteSpace(configuration["OpenRouter:ApiKey"]),
        configProviderPresent = !string.IsNullOrWhiteSpace(configuration["OpenRouter:Provider"]),
        envDoubleUnderscorePresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenRouter__ApiKey")),
        envColonPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenRouter:ApiKey")),
        envProviderDoubleUnderscorePresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenRouter__Provider")),
        envProviderColonPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenRouter:Provider")),
        envOllamaBaseUrlDoubleUnderscorePresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenRouter__OllamaBaseUrl")),
        envOllamaModelDoubleUnderscorePresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenRouter__OllamaModel")),
        recommendedEnvKey = "OpenRouter__ApiKey",
        recommendedProviderKey = "OpenRouter__Provider",
        recommendedModelKey = "OpenRouter__Model",
        recommendedOllamaBaseUrlKey = "OpenRouter__OllamaBaseUrl",
        recommendedOllamaModelKey = "OpenRouter__OllamaModel",
        recommendedFallbackModelsKey = "OpenRouter__FallbackModels"
    });
});

app.MapGet("/api/debug/session-summary/{id}", async (
    string id,
    AppDbContext db,
    ITranscriptArchiveReader archiveReader,
    IStorageService storage,
    IOptions<SessionStorageOptions> storageOptions) =>
{
    if (!Guid.TryParse(id, out var sessionGuid))
    {
        return Results.BadRequest(new { error = "Invalid session ID" });
    }

    var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionGuid);
    if (session == null)
    {
        return Results.NotFound(new { error = "Session not found" });
    }

    var options = storageOptions.Value;
    var summaryStoragePath = options.BuildSummaryArtifactPath(sessionGuid);
    var summaryArtifactUrl = await storage.GetSignedDownloadUrlIfExistsAsync(
        options.BucketName,
        summaryStoragePath,
        $"{sessionGuid:N}_summary.json",
        expirySeconds: 300);

    TranscriptArchiveDto? transcriptArchive = null;
    if (!string.IsNullOrWhiteSpace(session.TranscriptStoragePath))
    {
        transcriptArchive = await archiveReader.LoadAsync(session.TranscriptStoragePath, CancellationToken.None);
    }

    return Results.Ok(new
    {
        sessionId = session.Id,
        presentationTitle = session.PresentationTitle,
        summaryTextInDb = session.SummaryText,
        summaryTextInArchive = transcriptArchive?.Summary,
        summaryStoragePath,
        summaryArtifactUrl,
        transcriptStoragePath = session.TranscriptStoragePath,
        transcriptArchiveSummaryPresent = !string.IsNullOrWhiteSpace(transcriptArchive?.Summary),
        sessionStorageBucket = options.BucketName,
        sessionStorageTranscriptPrefix = options.TranscriptPrefix,
        sessionStorageSummaryPrefix = options.SummaryPrefix
    });
});

app.MapGet("/api/debug/fonts", () =>
{
    try
    {
        var files = new[] { "/usr/share/fonts/truetype", "/usr/local/share/fonts" }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.ttf", SearchOption.AllDirectories))
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .OrderBy(f => f)
            .ToArray();
        return Results.Ok(new { count = files.Length, fonts = files });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/api/debug/pdffonts/{fileName}", async (string fileName) =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "uploads", fileName);
    if (!File.Exists(path)) return Results.NotFound(new { error = "file not found" });
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pdffonts",
            Arguments = $"\"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = Process.Start(psi);
        if (proc == null) return Results.Problem("pdffonts not available");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(5000);
        return Results.Ok(new { stdout, stderr });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.Run();

public partial class Program { }
