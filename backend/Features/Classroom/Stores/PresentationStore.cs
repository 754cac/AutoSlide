using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BackendServer.Features.Classroom;

// ── Supporting model types ────────────────────────────────────────────────
public class Presentation
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int TotalSlides { get; set; }
    public string PresenterToken { get; set; } = string.Empty;
    public DateTime TokenExpires { get; set; }
    public string State { get; set; } = "idle";
    public string UploadedFile { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public HashSet<int> UnlockedSlides { get; set; } = new HashSet<int> { 1 };
    public int CurrentSlide { get; set; } = 1;
    public List<TranscriptEntry> TranscriptHistory { get; set; } = new();
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    /// <summary>True progressive unlock: check if a specific slide is unlocked</summary>
    public bool IsSlideUnlocked(int slideNumber)
    {
        lock (UnlockedSlides)
        {
            return UnlockedSlides.Contains(slideNumber);
        }
    }

    /// <summary>Get sorted list of all unlocked slides</summary>
    public List<int> GetUnlockedSlidesList()
    {
        lock (UnlockedSlides)
        {
            return UnlockedSlides.OrderBy(s => s).ToList();
        }
    }
}

public record TranscriptEntry(DateTime Timestamp, string Text);
public record PresenterToken { public string Token { get; init; } = string.Empty; public DateTime Expires { get; init; } }
public record PresentationTokenInfo { public string PresentationId { get; init; } = string.Empty; public DateTime Expires { get; init; } }

// ── PresentationStore ─────────────────────────────────────────────────────
public class PresentationStore
{
    private readonly ConcurrentDictionary<string, Presentation> _presentations = new();

    public sealed record SessionState(string PresentationId, string SessionId, string Title, int CurrentSlide, int TotalSlides, List<int> UnlockedSlides);

    public PresenterToken RegisterPresentation(string id, string name, int totalSlides, string filePath)
    {
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var expires = DateTime.UtcNow.AddHours(6);
        var sessionId = Guid.NewGuid().ToString();
        var info = new Presentation { Id = id, Name = name, TotalSlides = totalSlides, PresenterToken = token, TokenExpires = expires, UploadedFile = filePath, SessionId = sessionId };
        _presentations[id] = info;
        return new PresenterToken { Token = token, Expires = expires };
    }

    public bool ValidateToken(string presentationId, string token, out PresentationTokenInfo? info)
    {
        info = null;
        if (!_presentations.TryGetValue(presentationId, out var p)) return false;
        if (p.PresenterToken != token) return false;
        if (p.TokenExpires < DateTime.UtcNow) return false;
        info = new PresentationTokenInfo { PresentationId = p.Id, Expires = p.TokenExpires };
        return true;
    }

    public void MarkRunning(string presentationId)
    {
        if (_presentations.TryGetValue(presentationId, out var p)) p.State = "running";
    }

    public void MarkEnded(string presentationId)
    {
        if (_presentations.TryGetValue(presentationId, out var p)) p.State = "ended";
    }

    public void UnlockSlide(string presentationId, int slide)
    {
        if (_presentations.TryGetValue(presentationId, out var p))
        {
            lock (p.UnlockedSlides)
            {
                p.UnlockedSlides.Add(slide);
            }
        }
    }

    private readonly ConcurrentDictionary<string, ConcurrentBag<WebSocket>> _sockets = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _fingerprints = new();

    public async Task RegisterSocketAsync(string presentationId, WebSocket socket, string? initialToken)
    {
        _sockets.TryAdd(presentationId, new ConcurrentBag<WebSocket>());
        _sockets[presentationId].Add(socket);

        var pres = _presentations.GetValueOrDefault(presentationId);
        ProgramLogEvent("ws_connect", presentationId, pres?.SessionId, null);

        if (!string.IsNullOrEmpty(initialToken)) ValidateToken(presentationId, initialToken, out var _);

        var buffer = new byte[4 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            try
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var ack = JsonSerializer.Serialize(new { type = "ack", message = "received" });
                var bytes = Encoding.UTF8.GetBytes(ack);
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                break;
            }
        }

        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); } catch { }
        ProgramLogEvent("ws_disconnect", presentationId, pres?.SessionId, null);
    }

    public Presentation? GetPresentationInfo(string id)
    {
        _presentations.TryGetValue(id, out var p);
        return p;
    }

    public Presentation? GetBySessionId(string sessionId)
    {
        foreach (var kv in _presentations)
        {
            if (kv.Value.SessionId == sessionId) return kv.Value;
        }
        return null;
    }

    public SessionState? GetSessionState(string sessionId)
    {
        var pres = GetBySessionId(sessionId);
        if (pres == null) return null;

        var unlocked = pres.GetUnlockedSlidesList();

        return new SessionState(
            PresentationId: pres.Id,
            SessionId: pres.SessionId,
            Title: pres.Name,
            CurrentSlide: pres.CurrentSlide,
            TotalSlides: pres.TotalSlides,
            UnlockedSlides: unlocked);
    }

    public async Task BroadcastAsync(string presentationId, string message)
    {
        if (!_sockets.TryGetValue(presentationId, out var bag)) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        var toRemove = new List<WebSocket>();
        foreach (var ws in bag)
        {
            if (ws.State != WebSocketState.Open) { toRemove.Add(ws); continue; }
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
            catch { toRemove.Add(ws); }
        }
        // cleanup — ConcurrentBag has no Remove; closed sockets are left to be GC'd
    }

    // Helper to write a structured event to the file-log (append as JSON lines)
    public static readonly object _logFileLock = new();
    public static void ProgramLogEvent(string eventType, string presentationId, string? sessionId, object? payload)
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            var entry = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                eventType,
                presentationId,
                sessionId,
                payload
            };
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            lock (_logFileLock)
            {
                File.AppendAllText(Path.Combine(logDir, "events.log"), line);
            }
        }
        catch { }
    }

    public void SetCurrentSlide(string presentationId, int slide)
    {
        if (_presentations.TryGetValue(presentationId, out var p))
        {
            p.CurrentSlide = slide;
        }
    }

    public void AppendTranscript(string presentationId, TranscriptEntry entry)
    {
        if (_presentations.TryGetValue(presentationId, out var p))
        {
            lock (p.TranscriptHistory)
            {
                p.TranscriptHistory.Add(entry);
            }
        }
    }

    public string GetTranscript(string presentationId)
    {
        if (_presentations.TryGetValue(presentationId, out var p))
        {
            lock (p.TranscriptHistory)
            {
                return string.Join(" ", p.TranscriptHistory.Select(x => x.Text));
            }
        }
        return string.Empty;
    }

    public bool IsDuplicate(string presentationId, string? fingerprint)
    {
        if (fingerprint == null) return false;
        var dict = _fingerprints.GetOrAdd(presentationId, _ => new ConcurrentDictionary<string, bool>());
        return dict.ContainsKey(fingerprint);
    }

    public void AddFingerprint(string presentationId, string? fingerprint)
    {
        if (fingerprint == null) return;
        var dict = _fingerprints.GetOrAdd(presentationId, _ => new ConcurrentDictionary<string, bool>());
        dict[fingerprint] = true;
    }
}
