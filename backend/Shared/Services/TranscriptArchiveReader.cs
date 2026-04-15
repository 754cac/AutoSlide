using System.Text.Json;
using BackendServer.Features.Classroom.DTOs;

namespace BackendServer.Shared.Services;

public interface ITranscriptArchiveReader
{
    Task<TranscriptArchiveDto?> LoadAsync(string transcriptStoragePath, CancellationToken cancellationToken = default);
    string BuildSummaryTranscript(IEnumerable<TranscriptEntryDto> transcriptEntries);
}

public sealed class TranscriptArchiveReader : ITranscriptArchiveReader
{
    private readonly HttpClient _httpClient;
    private readonly IStorageService _storage;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TranscriptArchiveReader(HttpClient httpClient, IStorageService storage)
    {
        _httpClient = httpClient;
        _storage = storage;
    }

    public async Task<TranscriptArchiveDto?> LoadAsync(string transcriptStoragePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcriptStoragePath))
        {
            return null;
        }

        try
        {
            var signedUrl = await _storage.GetSignedUrlAsync(transcriptStoragePath, expirySeconds: 300);
            if (string.IsNullOrWhiteSpace(signedUrl))
            {
                return null;
            }

            using var response = await _httpClient.GetAsync(signedUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<TranscriptArchiveDto>(stream, _jsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public string BuildSummaryTranscript(IEnumerable<TranscriptEntryDto> transcriptEntries)
    {
        return string.Join(
            Environment.NewLine,
            transcriptEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Text))
                .OrderBy(entry => entry.Timestamp)
                .Select(entry => $"[{entry.Timestamp:O}] {entry.Text}"));
    }
}
