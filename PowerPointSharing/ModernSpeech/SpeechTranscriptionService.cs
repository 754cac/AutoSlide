using System;
using System.Threading.Tasks;
using Windows.Media.SpeechRecognition;
using Windows.Globalization;
using System.Diagnostics;

namespace ModernSpeech
{
    /// <summary>
    /// Handles real-time speech-to-text transcription using Windows Speech Recognition API.
    /// Provides continuous recognition with automatic restart on unexpected session termination.
    /// </summary>
    public class SpeechTranscriptionService : IDisposable
    {
        private SpeechRecognizer? _speechRecognizer;
        private volatile bool _shouldBeListening; // desired operational state
        private volatile System.Threading.CancellationTokenSource? _cts; // controls scheduled restarts
        private int _isSessionRunning;

        // Backoff parameters remain for logging/diagnostics, but restart logic simplified
        // incremented on background callback thread; use Interlocked for safety
        private volatile int _restartCount = 0;
        private long _sessionStartedUtcTicks;
        private const int MaxRestarts = 15; // ~1‑hour lectures
        private const int StableSessionResetSeconds = 45;

        // guard against concurrent Start/Stop invocations
        private readonly System.Threading.SemaphoreSlim _startStopLock = new(1,1);

        public event EventHandler<string>? TranscriptReceived;

        /// <summary>
        /// Fired when a speech recognition error occurs (e.g., privacy settings, microphone access).
        /// </summary>
        public event EventHandler<string>? ErrorOccurred;

        /// <summary>
        /// Optional BCP-47 language tag (e.g., "en-US", "zh-HK") for recognition.
        /// Must be set before calling StartAsync() to take effect.
        /// </summary>
        public string? LanguageTag { get; set; }

        /// <summary>
        /// Starts continuous speech recognition.
        /// </summary>
        public async Task StartAsync()
        {
            await _startStopLock.WaitAsync();
            try
            {
                if (_shouldBeListening) return; // already running
                _shouldBeListening = true;

                // cancel any pending restarts but don't dispose the token until
                // after we've created a fresh one so background tasks don't observe
                // a disposed token.
                _cts?.Cancel();
                var oldCts = _cts;
                _cts = new System.Threading.CancellationTokenSource();
                oldCts?.Dispose();

                // reset counter via Interlocked for clear memory ordering
                System.Threading.Interlocked.Exchange(ref _restartCount, 0);
                System.Threading.Interlocked.Exchange(ref _sessionStartedUtcTicks, 0);

                try
                {
                    await CreateAndStartRecognizerAsync();
                }
                catch (Exception ex)
                {
                    _shouldBeListening = false;
                    Debug.WriteLine($"[SpeechTranscription] Failed to start: {ex.Message}");
                    try { ErrorOccurred?.Invoke(this, ex.Message); }
                    catch (Exception handlerEx)
                    {
                        Debug.WriteLine($"[SpeechTranscription] ErrorOccurred handler threw: {handlerEx}");
                    }
                }
            }
            finally { _startStopLock.Release(); }
        }

        /// <summary>
        /// Stops continuous speech recognition.
        /// </summary>
        public async Task StopAsync()
        {
            await _startStopLock.WaitAsync();
            try
            {
                _shouldBeListening = false;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                System.Threading.Interlocked.Exchange(ref _isSessionRunning, 0);
                System.Threading.Interlocked.Exchange(ref _sessionStartedUtcTicks, 0);
                await TeardownRecognizerAsync();
                Debug.WriteLine("[SpeechTranscription] Transcription stopped.");
            }
            finally { _startStopLock.Release(); }
        }

        // Creates a fresh recognizer instance and starts the continuous session.
        private async Task CreateAndStartRecognizerAsync()
        {
            await TeardownRecognizerAsync(); // ensure clean slate
            if (!_shouldBeListening) return; // start may have been canceled mid-teardown

            var languageCode = !string.IsNullOrEmpty(LanguageTag) ? LanguageTag : System.Globalization.CultureInfo.CurrentCulture.Name;
            var recognitionLanguage = new Language(languageCode);

            _speechRecognizer = new SpeechRecognizer(recognitionLanguage);
            await _speechRecognizer.CompileConstraintsAsync();
            if (!_shouldBeListening) return; // stop could have been called during compile

            _speechRecognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromDays(1);
            _speechRecognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromDays(1);
            _speechRecognizer.Timeouts.BabbleTimeout = TimeSpan.FromDays(1);

            _speechRecognizer.ContinuousRecognitionSession.ResultGenerated += OnRecognitionResultGenerated;
            _speechRecognizer.ContinuousRecognitionSession.Completed += OnRecognitionSessionCompleted;

            await _speechRecognizer.ContinuousRecognitionSession.StartAsync();
            System.Threading.Interlocked.Exchange(ref _isSessionRunning, 1);
            System.Threading.Interlocked.Exchange(ref _sessionStartedUtcTicks, DateTime.UtcNow.Ticks);
            Debug.WriteLine("[SpeechTranscription] Transcription started.");
        }

        private async Task TeardownRecognizerAsync()
        {
            if (_speechRecognizer == null) return;
            try
            {
                _speechRecognizer.ContinuousRecognitionSession.ResultGenerated -= OnRecognitionResultGenerated;
                _speechRecognizer.ContinuousRecognitionSession.Completed -= OnRecognitionSessionCompleted;
                if (System.Threading.Interlocked.Exchange(ref _isSessionRunning, 0) == 1)
                {
                    await _speechRecognizer.ContinuousRecognitionSession.StopAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpeechTranscription] Teardown warning: {ex.Message}");
            }
            finally
            {
                try { _speechRecognizer.Dispose(); } catch { }
                _speechRecognizer = null;
                System.Threading.Interlocked.Exchange(ref _isSessionRunning, 0);
                System.Threading.Interlocked.Exchange(ref _sessionStartedUtcTicks, 0);
            }
        }

        private void OnRecognitionResultGenerated(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
        {
            if (args.Result.Status == SpeechRecognitionResultStatus.Success && !string.IsNullOrEmpty(args.Result.Text))
            {
                System.Threading.Interlocked.Exchange(ref _restartCount, 0);
                Debug.WriteLine($"[SpeechTranscription] Recognized: {args.Result.Text}");
                TranscriptReceived?.Invoke(this, args.Result.Text);
            }
        }

        private void OnRecognitionSessionCompleted(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
        {
            Debug.WriteLine($"[SpeechTranscription] Session completed: {args.Status}");

            var activeRecognizer = _speechRecognizer;
            if (activeRecognizer == null || !ReferenceEquals(sender, activeRecognizer.ContinuousRecognitionSession))
            {
                Debug.WriteLine("[SpeechTranscription] Ignoring completion from stale recognizer session.");
                return;
            }

            System.Threading.Interlocked.Exchange(ref _isSessionRunning, 0);

            // cancel guard: if StopAsync() was already invoked or token gone, bail out
            if (_cts == null || _cts.IsCancellationRequested)
            {
                Debug.WriteLine("[SpeechTranscription] Completed after cancellation; no restart.");
                return;
            }

            // Auto-restart if recognition should still be active (handles unexpected disconnects)
            if (_shouldBeListening)
            {
                // capture token source once to avoid TOCTOU with StopAsync
                var cts = _cts;
                if (cts == null) return; // service being torn down

                // Silence timeout: restart immediately, reset backoff
                if (args.Status == SpeechRecognitionResultStatus.Success)
                {
                    Debug.WriteLine("[SpeechTranscription] Session ended due to silence. Restarting immediately...");
                    System.Threading.Interlocked.Exchange(ref _restartCount, 0);
                    System.Threading.CancellationToken token;
                    try { token = cts.Token; }
                    catch (ObjectDisposedException) { return; }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(500, token);
                            if (token.IsCancellationRequested || !_shouldBeListening) return;

                            await _startStopLock.WaitAsync(token);
                            try
                            {
                                if (!_shouldBeListening) return;
                                await CreateAndStartRecognizerAsync();
                            }
                            finally { _startStopLock.Release(); }
                        }
                        catch (OperationCanceledException) { /* expected */ }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SpeechTranscription] Restart failed: {ex.Message}");
                            try { ErrorOccurred?.Invoke(this, ex.Message); }
                            catch (Exception handlerEx)
                            {
                                Debug.WriteLine($"[SpeechTranscription] ErrorOccurred handler threw: {handlerEx}");
                            }
                        }
                    });
                    return;
                }

                // error cases
                if (WasSessionStable())
                {
                    // If a session was healthy for a while, restart budget should recover.
                    System.Threading.Interlocked.Exchange(ref _restartCount, 0);
                }

                // increment count safely and recalc for backoff
                int currentCount = System.Threading.Interlocked.Increment(ref _restartCount);
                if (currentCount > MaxRestarts)
                {
                    Debug.WriteLine($"[SpeechTranscription] Max restarts ({MaxRestarts}) reached. Giving up.");
                    // terminal state: clean up so future StartAsync calls can proceed
                    _shouldBeListening = false;
                    cts.Cancel();
                    cts.Dispose();
                    _cts = null;
                    _ = TeardownRecognizerAsync();
                    try { ErrorOccurred?.Invoke(this, "Max restarts reached"); }
                    catch (Exception handlerEx)
                    {
                        Debug.WriteLine($"[SpeechTranscription] ErrorOccurred handler threw: {handlerEx}");
                    }
                    return;
                }

                var backoffSeconds = Math.Min(Math.Pow(2, currentCount), 30);
                Debug.WriteLine($"[SpeechTranscription] Error status {args.Status}. Restarting in {backoffSeconds}s (attempt {currentCount}/{MaxRestarts})...");
                System.Threading.CancellationToken token2;
                try { token2 = cts.Token; }
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(async () =>
                {
                    bool lockAcquired = false;
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), token2);
                        if (token2.IsCancellationRequested || !_shouldBeListening) return;

                        await _startStopLock.WaitAsync(token2);
                        lockAcquired = true;
                        if (!_shouldBeListening) return;
                        await CreateAndStartRecognizerAsync();
                    }
                    catch (OperationCanceledException) { /* expected */ }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SpeechTranscription] Restart failed: {ex.Message}");
                        try { ErrorOccurred?.Invoke(this, ex.Message); }
                        catch (Exception handlerEx)
                        {
                            Debug.WriteLine($"[SpeechTranscription] ErrorOccurred handler threw: {handlerEx}");
                        }
                    }
                    finally
                    {
                        if (lockAcquired) _startStopLock.Release();
                    }
                });
            }
        }

        /// <summary>
        /// Releases unmanaged resources held by the service.
        /// </summary>
        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            System.Threading.Interlocked.Exchange(ref _isSessionRunning, 0);
            System.Threading.Interlocked.Exchange(ref _sessionStartedUtcTicks, 0);
            _startStopLock.Dispose();
        }

        private bool WasSessionStable()
        {
            var sessionStartTicks = System.Threading.Interlocked.Read(ref _sessionStartedUtcTicks);
            if (sessionStartTicks <= 0)
            {
                return false;
            }

            var elapsedTicks = DateTime.UtcNow.Ticks - sessionStartTicks;
            if (elapsedTicks <= 0)
            {
                return false;
            }

            return TimeSpan.FromTicks(elapsedTicks) >= TimeSpan.FromSeconds(StableSessionResetSeconds);
        }
    }
}