using Microsoft.Extensions.Configuration;

namespace BackendServer.Shared.Services;

public static class OpenRouterDiagnostics
{
    private static readonly char[] FallbackModelSeparators = { ',', ';', '\n', '\r' };

    public static string ResolveProvider(IConfiguration configuration)
    {
        var envDoubleUnderscore = Environment.GetEnvironmentVariable("OpenRouter__Provider");
        if (!string.IsNullOrWhiteSpace(envDoubleUnderscore))
        {
            return envDoubleUnderscore;
        }

        var envColon = Environment.GetEnvironmentVariable("OpenRouter:Provider");
        if (!string.IsNullOrWhiteSpace(envColon))
        {
            return envColon;
        }

        return configuration["OpenRouter:Provider"] ?? "openrouter";
    }

    public static string ResolveApiKey(IConfiguration configuration)
    {
        var envDoubleUnderscore = Environment.GetEnvironmentVariable("OpenRouter__ApiKey");
        if (!string.IsNullOrWhiteSpace(envDoubleUnderscore))
        {
            return envDoubleUnderscore;
        }

        var envColon = Environment.GetEnvironmentVariable("OpenRouter:ApiKey");
        if (!string.IsNullOrWhiteSpace(envColon))
        {
            return envColon;
        }

        return configuration["OpenRouter:ApiKey"] ?? string.Empty;
    }

    public static string ResolveModel(IConfiguration configuration)
    {
        var envDoubleUnderscore = Environment.GetEnvironmentVariable("OpenRouter__Model");
        if (!string.IsNullOrWhiteSpace(envDoubleUnderscore))
        {
            return envDoubleUnderscore;
        }

        var envColon = Environment.GetEnvironmentVariable("OpenRouter:Model");
        if (!string.IsNullOrWhiteSpace(envColon))
        {
            return envColon;
        }

        return configuration["OpenRouter:Model"] ?? "openai/gpt-oss-20b:free";
    }

    public static string ResolveOllamaBaseUrl(IConfiguration configuration)
    {
        var envDoubleUnderscore = Environment.GetEnvironmentVariable("OpenRouter__OllamaBaseUrl");
        if (!string.IsNullOrWhiteSpace(envDoubleUnderscore))
        {
            return envDoubleUnderscore;
        }

        var envColon = Environment.GetEnvironmentVariable("OpenRouter:OllamaBaseUrl");
        if (!string.IsNullOrWhiteSpace(envColon))
        {
            return envColon;
        }

        return configuration["OpenRouter:OllamaBaseUrl"] ?? "http://localhost:11434";
    }

    public static string ResolveOllamaModel(IConfiguration configuration)
    {
        var envDoubleUnderscore = Environment.GetEnvironmentVariable("OpenRouter__OllamaModel");
        if (!string.IsNullOrWhiteSpace(envDoubleUnderscore))
        {
            return envDoubleUnderscore;
        }

        var envColon = Environment.GetEnvironmentVariable("OpenRouter:OllamaModel");
        if (!string.IsNullOrWhiteSpace(envColon))
        {
            return envColon;
        }

        return configuration["OpenRouter:OllamaModel"] ?? "qwen3.5:latest";
    }

    public static string[] ResolveFallbackModels(IConfiguration configuration)
    {
        var envDoubleUnderscore = Environment.GetEnvironmentVariable("OpenRouter__FallbackModels");
        var parsedFromDoubleUnderscore = ParseFallbackModels(envDoubleUnderscore);
        if (parsedFromDoubleUnderscore.Length > 0)
        {
            return parsedFromDoubleUnderscore;
        }

        var envColon = Environment.GetEnvironmentVariable("OpenRouter:FallbackModels");
        var parsedFromColon = ParseFallbackModels(envColon);
        if (parsedFromColon.Length > 0)
        {
            return parsedFromColon;
        }

        var configuredArray = configuration.GetSection("OpenRouter:FallbackModels").Get<string[]>();
        if (configuredArray is { Length: > 0 })
        {
            return configuredArray
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var configuredScalar = configuration["OpenRouter:FallbackModels"];
        return ParseFallbackModels(configuredScalar);
    }

    public static string ResolveSource(IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenRouter__ApiKey")))
        {
            return "environment:OpenRouter__ApiKey";
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenRouter:ApiKey")))
        {
            return "environment:OpenRouter:ApiKey";
        }

        if (!string.IsNullOrWhiteSpace(configuration["OpenRouter:ApiKey"]))
        {
            return "configuration:OpenRouter:ApiKey";
        }

        return "missing";
    }

    public static string ResolveFallbackModelsSource(IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenRouter__FallbackModels")))
        {
            return "environment:OpenRouter__FallbackModels";
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OpenRouter:FallbackModels")))
        {
            return "environment:OpenRouter:FallbackModels";
        }

        var configuredArray = configuration.GetSection("OpenRouter:FallbackModels").Get<string[]>();
        if (configuredArray is { Length: > 0 })
        {
            return "configuration:OpenRouter:FallbackModels[]";
        }

        if (!string.IsNullOrWhiteSpace(configuration["OpenRouter:FallbackModels"]))
        {
            return "configuration:OpenRouter:FallbackModels";
        }

        return "missing";
    }

    public static string MaskSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<missing>";
        }

        if (value.Length <= 8)
        {
            return new string('*', value.Length);
        }

        return $"{value[..4]}...{value[^4..]}";
    }

    private static string[] ParseFallbackModels(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(FallbackModelSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}