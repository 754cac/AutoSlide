using System.Text;
using BackendServer.Configuration;
using BackendServer.Features.Auth.Services;
using BackendServer.Features.Classroom;
using BackendServer.Features.Classroom.Services;
using BackendServer.Features.Materials.Services;
using BackendServer.Shared.Data;
using BackendServer.Shared.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace BackendServer.Extensions;

public static class ServiceCollectionExtensions
{
    public const long MaterialUploadMaxBytes = 5L * 1024L * 1024L;

    /// <summary>
    /// Configures Kestrel: optional HTTPS when a certificate path is present,
    /// enforces the material upload request body limit.
    /// </summary>
    public static WebApplicationBuilder AddKestrelConfig(this WebApplicationBuilder builder)
    {
        var certPathEnv = Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Path");
        var certPasswordEnv = Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Password");

        builder.WebHost.ConfigureKestrel(options =>
        {
            try
            {
                // Keep the backend aligned with the 5 MB edge limit for material uploads.
                options.Limits.MaxRequestBodySize = MaterialUploadMaxBytes;

                if (!string.IsNullOrEmpty(certPathEnv) && File.Exists(certPathEnv))
                {
                    // Bind HTTPS and HTTP
                    options.ListenAnyIP(5001, listenOptions =>
                    {
                        if (!string.IsNullOrEmpty(certPasswordEnv))
                            listenOptions.UseHttps(certPathEnv, certPasswordEnv);
                        else
                            listenOptions.UseHttps(certPathEnv);
                    });
                    options.ListenAnyIP(5000);
                }
                else
                {
                    // No cert available, bind only HTTP to avoid the dev-certs requirement
                    options.ListenAnyIP(5000);
                }
            }
            catch
            {
                // If something goes wrong configuring Kestrel, fall back to default behavior
            }
        });

        return builder;
    }

    /// <summary>
    /// Registers EF Core DbContext — InMemory for tests, Npgsql for production.
    /// </summary>
    public static IServiceCollection AddDatabaseServices(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.GetValue<bool>("UseInMemoryDatabase"))
        {
            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase("TestDb"));
        }
        else
        {
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));
        }

        return services;
    }

    /// <summary>
    /// Registers JWT Bearer authentication, reading settings from the "JwtSettings" section.
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));

        var jwtSettings = configuration.GetSection(JwtOptions.Section);
        var jwtKey = jwtSettings["Key"]
            ?? "super_secret_key_that_is_long_enough_12345_and_even_longer_to_satisfy_hmacsha512_requirement";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ValidateIssuer = true,
                    ValidIssuer = jwtSettings["Issuer"] ?? "AutoSlideBackend",
                    ValidateAudience = true,
                    ValidAudience = jwtSettings["Audience"] ?? "AutoSlideUsers",
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(5) // Allow 5 minutes clock skew
                };
            });

        return services;
    }

    /// <summary>
    /// Registers the Supabase client singleton and storage services.
    /// Throws early if SUPABASE_URL or SUPABASE_KEY are missing.
    /// </summary>
    public static IServiceCollection AddSupabaseServices(this IServiceCollection services, IConfiguration configuration)
    {
        var supabaseUrl = configuration["SUPABASE_URL"] ?? "";
        var supabaseKey = configuration["SUPABASE_KEY"] ?? "";

        if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(supabaseKey))
        {
            throw new InvalidOperationException(
                "Supabase configuration missing: ensure SUPABASE_URL and SUPABASE_KEY are set in environment or appsettings.\n" +
                "Application will not start without valid values."
            );
        }

        services.Configure<SupabaseOptions>(o =>
        {
            o.Url = supabaseUrl;
            o.Key = supabaseKey;
        });

        // Singleton: Supabase.Client is thread-safe; one instance is enough.
        // InitializeAsync is awaited once at startup (see WebApplicationExtensions).
        services.AddSingleton<Supabase.Client>(_ => new Supabase.Client(supabaseUrl, supabaseKey));
        services.AddScoped<IStorageService, StorageService>();
        services.AddScoped<StorageAuthorizationService>();

        return services;
    }

    /// <summary>
    /// Registers in-process application services: PresentationStore, PDF/PPTX processing,
    /// UploadService, and the optional OpenRouter summariser.
    /// </summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<PasswordService>();

        services.AddSingleton<PresentationStore>();
        services.AddSingleton<PdfService>();
        services.AddSingleton<PptxService>();
        services.AddSingleton<SlideSplitterService>();
        services.AddScoped<IInkArtifactExportService, InkArtifactExportService>();
        services.AddScoped<ISolutionPageService, SolutionPageService>();
        services.Configure<SessionStorageOptions>(configuration.GetSection(SessionStorageOptions.SectionName));
        services.Configure<OpenRouterOptions>(configuration.GetSection(OpenRouterOptions.SectionName));
        services.PostConfigure<OpenRouterOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.Provider))
            {
                options.Provider = OpenRouterDiagnostics.ResolveProvider(configuration);
            }

            options.Provider = options.Provider.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                options.ApiKey = OpenRouterDiagnostics.ResolveApiKey(configuration);
            }

            if (string.IsNullOrWhiteSpace(options.Model))
            {
                options.Model = OpenRouterDiagnostics.ResolveModel(configuration);
            }

            if (string.IsNullOrWhiteSpace(options.OllamaBaseUrl))
            {
                options.OllamaBaseUrl = OpenRouterDiagnostics.ResolveOllamaBaseUrl(configuration);
            }

            if (string.IsNullOrWhiteSpace(options.OllamaModel))
            {
                options.OllamaModel = OpenRouterDiagnostics.ResolveOllamaModel(configuration);
            }

            options.OllamaBaseUrl = options.OllamaBaseUrl.Trim().TrimEnd('/');
            options.OllamaModel = options.OllamaModel.Trim();

            if (options.FallbackModels == null || options.FallbackModels.Length == 0)
            {
                options.FallbackModels = OpenRouterDiagnostics.ResolveFallbackModels(configuration);
            }

            options.FallbackModels = (options.FallbackModels ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Where(value => !string.Equals(value, options.Model, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        });
        services.AddHttpClient<ITranscriptArchiveReader, TranscriptArchiveReader>();
        services.AddHttpClient<IOpenRouterChatService, OpenRouterChatService>();

        // UploadService is scoped because it takes a scoped AppDbContext
        services.AddScoped<UploadService>();

        return services;
    }

    /// <summary>
    /// Registers the CORS policy that allows the frontend viewer origin.
    /// </summary>
    public static IServiceCollection AddCorsPolicies(this IServiceCollection services, IConfiguration configuration)
    {
        var viewerOrigin = configuration["VIEWER_BASE_URL"] ?? "http://localhost:3000";

        services.Configure<AppCorsOptions>(o => o.ViewerBaseUrl = viewerOrigin);

        services.AddCors(options =>
        {
            options.AddPolicy("AllowViewer", policy => policy
                .WithOrigins(viewerOrigin) // Explicit origin required when using AllowCredentials
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()); // Required for SignalR
        });

        return services;
    }
}
