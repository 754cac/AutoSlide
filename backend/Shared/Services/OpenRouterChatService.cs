using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using BackendServer.Configuration;
using Microsoft.Extensions.Options;

namespace BackendServer.Shared.Services;

public sealed class OpenRouterChatService : IOpenRouterChatService
{
    private static readonly TimeSpan[] RateLimitBackoffSchedule =
    {
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(25),
        TimeSpan.FromSeconds(35),
        TimeSpan.FromSeconds(45)
    };

    private readonly HttpClient _httpClient;
    private readonly OpenRouterOptions _options;
    private readonly ILogger<OpenRouterChatService> _logger;

    public OpenRouterChatService(
        HttpClient httpClient,
        IOptions<OpenRouterOptions> options,
        ILogger<OpenRouterChatService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(_options.Provider, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            return await GenerateWithOllamaAsync(systemPrompt, userPrompt, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogInformation("OpenRouter API key is missing. Skipping summarization.");
            return null;
        }

        var modelCandidates = BuildModelCandidates();
        var fallbackModels = modelCandidates.Skip(1).ToArray();
        var maxAttempts = RateLimitBackoffSchedule.Length + 1;

        _logger.LogInformation(
            "Sending OpenRouter summary request using model {Model}. FallbackModels={FallbackModels}. API key configured: {ApiKeyConfigured}. MaxAttempts={MaxAttempts}",
            _options.Model,
            fallbackModels,
            true,
            maxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var modelForAttempt = modelCandidates[(attempt - 1) % modelCandidates.Length];
            var payload = new
            {
                model = modelForAttempt,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.2
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    {
                        _logger.LogWarning("OpenRouter response did not contain any choices.");
                        return null;
                    }

                    var firstChoice = choices[0];
                    if (!firstChoice.TryGetProperty("message", out var message) ||
                        !message.TryGetProperty("content", out var contentElement))
                    {
                        _logger.LogWarning("OpenRouter response did not contain assistant content.");
                        return null;
                    }

                    string? content = contentElement.ValueKind switch
                    {
                        JsonValueKind.String => contentElement.GetString(),
                        JsonValueKind.Array => string.Join(
                            Environment.NewLine,
                            contentElement.EnumerateArray()
                                .Select(item =>
                                    item.ValueKind == JsonValueKind.String
                                        ? item.GetString()
                                        : item.ValueKind == JsonValueKind.Object
                                            && item.TryGetProperty("text", out var textElement)
                                            && textElement.ValueKind == JsonValueKind.String
                                            ? textElement.GetString()
                                            : null)
                                .Where(text => !string.IsNullOrWhiteSpace(text))
                                .Select(text => text!.Trim())),
                        _ => null
                    };

                    return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse OpenRouter response.");
                    return null;
                }
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxAttempts)
            {
                var retryDelay = ResolveRetryDelay(response, attempt);
                var nextModel = modelCandidates[attempt % modelCandidates.Length];
                _logger.LogWarning(
                    "OpenRouter request rate-limited (429) on attempt {Attempt}/{MaxAttempts} for model {Model}. Retrying in {RetryDelaySeconds}s using model {NextModel}. Response body: {Body}",
                    attempt,
                    maxAttempts,
                    modelForAttempt,
                    Math.Round(retryDelay.TotalSeconds),
                    nextModel,
                    responseBody);

                await Task.Delay(retryDelay, cancellationToken);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound && attempt < maxAttempts)
            {
                var nextModel = modelCandidates[attempt % modelCandidates.Length];
                _logger.LogWarning(
                    "OpenRouter model {Model} returned 404 on attempt {Attempt}/{MaxAttempts}. Treating model as unavailable/deprecated and switching immediately to model {NextModel}. Response body: {Body}",
                    modelForAttempt,
                    attempt,
                    maxAttempts,
                    nextModel,
                    responseBody);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.BadRequest
                && attempt < maxAttempts
                && responseBody.Contains("Developer instruction is not enabled", StringComparison.OrdinalIgnoreCase))
            {
                var nextModel = modelCandidates[attempt % modelCandidates.Length];
                _logger.LogWarning(
                    "OpenRouter model {Model} rejected system/developer instruction on attempt {Attempt}/{MaxAttempts}. Switching immediately to model {NextModel}. Response body: {Body}",
                    modelForAttempt,
                    attempt,
                    maxAttempts,
                    nextModel,
                    responseBody);
                continue;
            }

            if ((response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden))
            {
                _logger.LogWarning(
                    "OpenRouter request failed with {StatusCode} for model {Model} on attempt {Attempt}/{MaxAttempts}. Authentication/authorization issue; retries skipped. Response body: {Body}",
                    response.StatusCode,
                    modelForAttempt,
                    attempt,
                    maxAttempts,
                    responseBody);
                return null;
            }

            if (attempt < maxAttempts)
            {
                var transientDelay = TimeSpan.FromSeconds(5);
                _logger.LogWarning(
                    "OpenRouter request failed with {StatusCode} for model {Model} on attempt {Attempt}/{MaxAttempts}. Retrying in {RetryDelaySeconds}s. Response body: {Body}",
                    response.StatusCode,
                    modelForAttempt,
                    attempt,
                    maxAttempts,
                    Math.Round(transientDelay.TotalSeconds),
                    responseBody);

                await Task.Delay(transientDelay, cancellationToken);
                continue;
            }

            _logger.LogWarning(
                "OpenRouter request failed with {StatusCode} for model {Model} on attempt {Attempt}/{MaxAttempts}. Response body: {Body}",
                response.StatusCode,
                modelForAttempt,
                attempt,
                maxAttempts,
                responseBody);
            return null;
        }

        _logger.LogWarning("OpenRouter request exhausted retry attempts without a successful response.");
        return null;
    }

    private async Task<string?> GenerateWithOllamaAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.OllamaBaseUrl))
        {
            _logger.LogWarning("Ollama base URL is missing. Skipping summarization.");
            return null;
        }

        var model = string.IsNullOrWhiteSpace(_options.OllamaModel)
            ? _options.Model
            : _options.OllamaModel;

        if (string.IsNullOrWhiteSpace(model))
        {
            _logger.LogWarning("Ollama model is missing. Skipping summarization.");
            return null;
        }

        var endpoint = $"{_options.OllamaBaseUrl.TrimEnd('/')}/api/chat";

        _logger.LogInformation(
            "Sending Ollama summary request. baseUrl={BaseUrl} model={Model}",
            _options.OllamaBaseUrl,
            model);

        var payload = new
        {
            model,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            options = new
            {
                temperature = 0.2
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Ollama request failed with {StatusCode}. Response body: {Body}",
                response.StatusCode,
                responseBody);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.Object
                && messageElement.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                var content = contentElement.GetString();
                return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
            }

            if (doc.RootElement.TryGetProperty("response", out var responseElement)
                && responseElement.ValueKind == JsonValueKind.String)
            {
                var content = responseElement.GetString();
                return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
            }

            _logger.LogWarning("Ollama response did not contain assistant content.");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Ollama response.");
            return null;
        }
    }

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
            {
                return until;
            }
        }

        var scheduleIndex = Math.Clamp(attempt - 1, 0, RateLimitBackoffSchedule.Length - 1);
        return RateLimitBackoffSchedule[scheduleIndex];
    }

    private string[] BuildModelCandidates()
    {
        var configuredModels = new[] { _options.Model }
            .Concat(_options.FallbackModels ?? Array.Empty<string>())
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (configuredModels.Length > 0)
        {
            return configuredModels;
        }

        return new[] { "openai/gpt-oss-20b:free" };
    }
}
