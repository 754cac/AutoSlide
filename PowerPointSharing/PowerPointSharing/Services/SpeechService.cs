using System;
using System.Threading.Tasks;
using ModernSpeech;

namespace PowerPointSharing
{
    /// <summary>
    /// Speech-to-text transcription start/stop wiring.
    /// </summary>
    public class SpeechService
    {
        private SpeechTranscriptionService? _handler;

        /// <summary>Raised when speech text is recognized.</summary>
        public event EventHandler<string>? TranscriptReceived;

        /// <summary>Raised when a speech recognition error occurs.</summary>
        public event EventHandler<string>? ErrorOccurred;

        public void Start()
        {
            try
            {
                if (_handler == null)
                {
                    _handler = new SpeechTranscriptionService();
                    _handler.TranscriptReceived += OnTranscript;
                    _handler.ErrorOccurred += OnError;
                }

                _ = Task.Run(async () => await _handler.StartAsync());
                System.Diagnostics.Debug.WriteLine("[SpeechService] Transcription started.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SpeechService] Failed to start: {ex.Message}");
            }
        }

        public void Stop()
        {
            try
            {
                if (_handler != null)
                {
                    _ = Task.Run(async () => await _handler.StopAsync());
                    System.Diagnostics.Debug.WriteLine("[SpeechService] Transcription stopped.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SpeechService] Failed to stop: {ex.Message}");
            }
        }

        private void OnTranscript(object sender, string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                System.Diagnostics.Debug.WriteLine($"[SpeechService] Recognized: {text}");
                TranscriptReceived?.Invoke(this, text);
            }
        }

        private void OnError(object sender, string errorMessage)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeechService] Error: {errorMessage}");
            ErrorOccurred?.Invoke(this, errorMessage);
        }
    }
}
