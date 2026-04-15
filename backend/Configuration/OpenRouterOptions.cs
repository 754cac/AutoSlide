namespace BackendServer.Configuration;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    public string Provider { get; set; } = "openrouter";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "openai/gpt-oss-20b:free";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "qwen3.5:latest";
    public string[] FallbackModels { get; set; } = Array.Empty<string>();
}
