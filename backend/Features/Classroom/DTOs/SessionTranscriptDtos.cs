using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackendServer.Features.Classroom.DTOs;

public sealed class TranscriptEntryDto
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public sealed class TranscriptArchiveDto
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("transcript")]
    public List<TranscriptEntryDto> Transcript { get; set; } = [];

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
}

public sealed class SessionSummaryArtifactDto
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("presentationTitle")]
    public string PresentationTitle { get; set; } = string.Empty;

    [JsonPropertyName("generatedAtUtc")]
    public DateTime GeneratedAtUtc { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("transcriptStoragePath")]
    public string TranscriptStoragePath { get; set; } = string.Empty;
}
