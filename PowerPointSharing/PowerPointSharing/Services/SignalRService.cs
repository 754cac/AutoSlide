using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace PowerPointSharing
{
    /// <summary>
    /// Owns the SignalR hub connection, reconnect logic, and all send methods.
    /// </summary>
    public class SignalRService
    {
        private readonly string _hubUrl;
        private HubConnection? _connection;
        private bool _isConnected;

        public bool IsConnected => _isConnected;

        /// <summary>
        /// Raised when a viewer requests current ink state (late joiner).
        /// Parameter is the connectionId of the requesting client.
        /// </summary>
        public event Action<string>? InkStateRequested;

        public SignalRService(string backendBaseUrl)
        {
            _hubUrl = backendBaseUrl.TrimEnd('/') + "/hubs/presentation";
        }

        public async Task ConnectAsync(string sessionId)
        {
            try
            {
                if (_connection != null && _isConnected)
                {
                    System.Diagnostics.Debug.WriteLine("[SignalR] Already connected");
                    return;
                }

                // Build connection into local var, then assign to field (Bug 3 fix)
                var conn = new HubConnectionBuilder()
                    .WithUrl(_hubUrl)
                    .WithAutomaticReconnect()
                    .Build();
                _connection = conn;

                conn.Reconnected += connectionId =>
                {
                    System.Diagnostics.Debug.WriteLine($"[SignalR] Reconnected: {connectionId}");
                    _isConnected = true;
                    // Use captured local ref, not field (Bug 3 fix)
                    return conn.InvokeAsync("JoinAsPresenter", sessionId);
                };

                conn.Closed += async (error) =>
                {
                    System.Diagnostics.Debug.WriteLine($"[SignalR] Closed: {error?.Message}");
                    _isConnected = false;
                    await Task.Delay(2000);
                    try { await conn.StartAsync(); }
                    catch { }
                };

                conn.On<string>("InkStateRequested", (connectionId) =>
                {
                    System.Diagnostics.Debug.WriteLine($"[SignalR] Ink state requested by {connectionId}");
                    InkStateRequested?.Invoke(connectionId);
                });

                await conn.StartAsync();
                _isConnected = true;
                await conn.InvokeAsync("JoinAsPresenter", sessionId);

                System.Diagnostics.Debug.WriteLine($"[SignalR] Connected as presenter for session {sessionId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SignalR] ConnectAsync error: {ex}");
                _isConnected = false;
            }
        }

        /// <summary>
        /// Disconnect and dispose the hub connection.
        /// Uses local-capture pattern to prevent null reference on cleanup (Bug 3 fix).
        /// </summary>
        public async Task DisconnectAsync()
        {
            var conn = _connection;
            _connection = null;
            _isConnected = false;

            if (conn == null) return;

            try
            {
                await conn.StopAsync();
                await conn.DisposeAsync();
                System.Diagnostics.Debug.WriteLine("[SignalR] Disconnected cleanly.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SignalR] Disconnect error: {ex.Message}");
            }
        }

        public async Task BroadcastInkStrokeAsync(string sessionId, int slideIndex, InkStrokeData strokeData)
        {
            var conn = _connection;
            if (conn == null || !_isConnected)
            {
                System.Diagnostics.Debug.WriteLine("[SignalR] Not connected, cannot broadcast stroke");
                return;
            }

            try
            {
                var payload = new
                {
                    strokeId = strokeData.StrokeId,
                    points = strokeData.Points,
                    color = strokeData.Color,
                    width = strokeData.Width,
                    opacity = strokeData.Opacity,
                    timestamp = strokeData.Timestamp.ToString("o")
                };

                await conn.InvokeAsync("BroadcastInkStroke", sessionId, slideIndex, payload);
                System.Diagnostics.Debug.WriteLine($"[SignalR] Broadcasted stroke {strokeData.StrokeId} for slide {slideIndex} ({strokeData.Points.Count} points)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SignalR] BroadcastInkStroke error: {ex.Message}");
            }
        }

        public async Task BroadcastClearInkAsync(string sessionId, int slideIndex)
        {
            var conn = _connection;
            if (conn == null || !_isConnected)
            {
                System.Diagnostics.Debug.WriteLine("[SignalR] Not connected, cannot broadcast clear");
                return;
            }

            try
            {
                await conn.InvokeAsync("BroadcastClearInk", sessionId, slideIndex);
                System.Diagnostics.Debug.WriteLine($"[SignalR] Broadcasted clear ink for slide {slideIndex}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SignalR] BroadcastClearInk error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends full ink state to a specific client (late joiner).
        /// </summary>
        public async Task<bool> SendInkStateTo(string connectionId, Dictionary<int, List<InkStrokeData>> allSlides)
        {
            var conn = _connection;
            if (conn == null || !_isConnected) return false;

            try
            {
                var payload = new Dictionary<int, object[]>();
                foreach (var kvp in allSlides)
                {
                    payload[kvp.Key] = kvp.Value.ConvertAll(s => (object)new
                    {
                        strokeId = s.StrokeId,
                        points = s.Points,
                        color = s.Color,
                        width = s.Width,
                        opacity = s.Opacity,
                        timestamp = s.Timestamp.ToString("o")
                    }).ToArray();
                }

                int totalStrokes = payload.Values.Sum(arr => arr.Length);
                await conn.InvokeAsync("SendInkStateTo", connectionId, payload);
                System.Diagnostics.Debug.WriteLine($"[SignalR] Sent ink state ({totalStrokes} strokes across {payload.Count} slides) to {connectionId}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SignalR] SendInkStateTo error: {ex.Message}");
                return false;
            }
        }

        public async Task SendInkStateToGroupAsync(string groupId, int frameIndex, List<InkStrokeData> strokes)
        {
            var conn = _connection;
            if (conn == null || !_isConnected) return;

            try
            {
                var payloadStrokes = strokes.ConvertAll(s => (object)new
                {
                    strokeId = s.StrokeId,
                    points = s.Points,
                    color = s.Color,
                    width = s.Width,
                    opacity = s.Opacity,
                    timestamp = s.Timestamp.ToString("o")
                }).ToArray();

                var syncData = new Dictionary<int, object[]>
                {
                    { frameIndex, payloadStrokes }
                };

                await conn.InvokeAsync("BroadcastBulkInkState", groupId, syncData);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SignalR] SendInkStateToGroupAsync error: {ex.Message}");
            }
        }
    }
}
