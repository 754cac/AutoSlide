namespace BackendServer.Shared.Services;

public interface IOpenRouterChatService
{
    Task<string?> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
}
