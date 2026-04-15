using Microsoft.AspNetCore.SignalR;
using BackendServer.Features.Classroom;

namespace BackendServer.Features.Classroom.Hubs;

public class PresentationHub : Hub
{
    private readonly PresentationStore _store;

    public PresentationHub(PresentationStore store)
    {
        _store = store;
    }

    public override async Task OnConnectedAsync()
    {
        Console.WriteLine($"SignalR Connected: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Normalize any GUID-like string to D-format (with dashes) for SignalR group keys.
    /// </summary>
    private static string NormalizeGroupKey(string id) => Guid.TryParse(id, out var g) ? g.ToString("D") : id;

    public async Task JoinSession(string sessionId)
    {
        // Normalize to dashed D-format so it matches broadcast group keys
        var groupKey = NormalizeGroupKey(sessionId);
        Console.WriteLine($"[Hub] ✅ Viewer {Context.ConnectionId} joined group '{groupKey}' (raw input='{sessionId}')");
        await Groups.AddToGroupAsync(Context.ConnectionId, groupKey);

        // Sync-on-Join: try session lookup by both raw and normalized keys
        var state = _store.GetSessionState(sessionId)
                 ?? _store.GetSessionState(groupKey);
        // Also try looking up directly by presentationId (no-dash format)
        if (state == null)
        {
            var pres = _store.GetPresentationInfo(sessionId)
                    ?? _store.GetPresentationInfo(groupKey)
                    ?? _store.GetPresentationInfo(sessionId.Replace("-", ""));
            if (pres != null)
            {
                var unlocked = pres.GetUnlockedSlidesList();
                state = new PresentationStore.SessionState(
                    PresentationId: pres.Id,
                    SessionId: pres.SessionId,
                    Title: pres.Name,
                    CurrentSlide: pres.CurrentSlide,
                    TotalSlides: pres.TotalSlides,
                    UnlockedSlides: unlocked);
            }
        }
        if (state != null)
        {
            await Clients.Caller.SendAsync("SyncState", new
            {
                presentationId = state.PresentationId,
                sessionId = state.SessionId,
                presentationTitle = state.Title,
                currentSlide = state.CurrentSlide,
                totalSlides = state.TotalSlides,
                unlockedSlides = state.UnlockedSlides // True progressive: full array of unlocked slides
            });
        }
    }

    public async Task LeaveSession(string sessionId)
    {
        var groupKey = NormalizeGroupKey(sessionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupKey);
    }

    // Allow students to subscribe to course-level updates
    public async Task JoinCourseGroup(string courseId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Course_{courseId}");
    }

    public async Task LeaveCourseGroup(string courseId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Course_{courseId}");
    }

    // ============================================================
    // REAL-TIME INK STREAMING (with per-slide state)
    // ============================================================

    /// <summary>
    /// Broadcast a new ink stroke to all viewers in the session.
    /// Now includes slideIndex so clients can store ink per-slide.
    /// </summary>
    public async Task BroadcastInkStroke(string sessionId, int slideIndex, object strokeData)
    {
        var groupKey = NormalizeGroupKey(sessionId);
        Console.WriteLine($"[SignalR] Broadcasting ink stroke for slide {slideIndex} to group '{groupKey}'");
        await Clients.OthersInGroup(groupKey).SendAsync("InkStroke", slideIndex, strokeData);
    }

    /// <summary>
    /// Broadcast clear ink command for a specific slide to all viewers.
    /// </summary>
    public async Task BroadcastClearInk(string sessionId, int slideIndex)
    {
        var groupKey = NormalizeGroupKey(sessionId);
        Console.WriteLine($"[SignalR] Broadcasting clear ink for slide {slideIndex} to group '{groupKey}'");
        await Clients.OthersInGroup(groupKey).SendAsync("ClearInk", slideIndex);
    }

    /// <summary>
    /// Request all current ink strokes when joining late.
    /// The presenter will respond with the current state.
    /// </summary>
    public async Task RequestInkState(string sessionId)
    {
        var groupKey = NormalizeGroupKey(sessionId);
        Console.WriteLine($"[SignalR] Client {Context.ConnectionId} requesting ink state for session {groupKey}");
        await Clients.Group($"{groupKey}_presenter").SendAsync("InkStateRequested", Context.ConnectionId);
    }

    /// <summary>
    /// Join the presenter group (for receiving ink state requests).
    /// Also joins the main session group so broadcasts reach viewers.
    /// </summary>
    public async Task JoinAsPresenter(string sessionId)
    {
        var groupKey = NormalizeGroupKey(sessionId);
        // Join both the presenter group AND the main session group
        await Groups.AddToGroupAsync(Context.ConnectionId, $"{groupKey}_presenter");
        await Groups.AddToGroupAsync(Context.ConnectionId, groupKey);
        Console.WriteLine($"[SignalR] ✅ {Context.ConnectionId} joined as presenter for group '{groupKey}' (both groups)");
    }

    /// <summary>
    /// Send ink state to a specific client (late joiner).
    /// Now sends a dictionary: { slideIndex: [strokes] }
    /// </summary>
    public async Task SendInkStateTo(string connectionId, object allSlidesInk)
    {
        Console.WriteLine($"[SignalR] Sending all-slides ink state to {connectionId}");
        await Clients.Client(connectionId).SendAsync("InkStateSync", allSlidesInk);
    }

    /// <summary>
    /// Broadcast a bulk ink state update (e.g. copied strokes on frame change)
    /// to all viewers in the session.
    /// </summary>
    public async Task BroadcastBulkInkState(string sessionId, object bulkInkData)
    {
        var groupKey = NormalizeGroupKey(sessionId);
        Console.WriteLine($"[SignalR] Broadcasting bulk ink state to group '{groupKey}'");
        await Clients.OthersInGroup(groupKey).SendAsync("InkStateSync", bulkInkData);
    }

}
