namespace BackendServer.Configuration;

public sealed class SessionStorageOptions
{
    public const string SectionName = "SessionStorage";

    public string BucketName { get; set; } = "uploads";
    public string TranscriptPrefix { get; set; } = "transcripts";
    public string SummaryPrefix { get; set; } = "summaries";

    public string BuildTranscriptArchivePath(string presentationId)
    {
        return $"{NormalizePrefix(TranscriptPrefix)}/{presentationId}_archive.json";
    }

    public string BuildSummaryArtifactPath(Guid sessionId)
    {
        return $"{NormalizePrefix(SummaryPrefix)}/{sessionId:N}_summary.json";
    }

    private static string NormalizePrefix(string prefix)
    {
        return string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim().Trim('/');
    }
}