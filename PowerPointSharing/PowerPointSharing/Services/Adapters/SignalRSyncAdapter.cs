using System.Collections.Generic;
using System.Threading.Tasks;

namespace PowerPointSharing
{
    internal sealed class SignalRSyncAdapter
    {
        private readonly SignalRService _signalR;

        public SignalRSyncAdapter(SignalRService signalR)
        {
            _signalR = signalR;
        }

        public bool IsConnected => _signalR.IsConnected;

        public Task ConnectAsync(string groupId)
        {
            return _signalR.ConnectAsync(groupId);
        }

        public Task DisconnectAsync()
        {
            return _signalR.DisconnectAsync();
        }

        public Task BroadcastStrokeAsync(string groupId, int frameIndex, InkStrokeData stroke)
        {
            return _signalR.BroadcastInkStrokeAsync(groupId, frameIndex, stroke);
        }

        public Task BroadcastClearAsync(string groupId, int frameIndex)
        {
            return _signalR.BroadcastClearInkAsync(groupId, frameIndex);
        }

        public Task BroadcastFullInkStateAsync(string groupId, int frameIndex, List<InkStrokeData> strokes)
        {
            return _signalR.SendInkStateToGroupAsync(groupId, frameIndex, strokes);
        }

        public Task<bool> SendInkStateToClientAsync(string connectionId, Dictionary<int, List<InkStrokeData>> absoluteFrameMap)
        {
            return _signalR.SendInkStateTo(connectionId, absoluteFrameMap);
        }
    }
}
