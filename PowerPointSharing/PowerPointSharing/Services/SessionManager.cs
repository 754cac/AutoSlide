using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Configuration;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.Runtime.InteropServices;
using QRCoder;
using Microsoft.Office.Core;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;

#nullable enable

namespace PowerPointSharing
{
    /// <summary>
    /// Owns session state, HTTP communication, and ink/speech broadcast coordination.
    /// Refactored so frame progression goes through SessionReducer + DeckIndex,
    /// while preserving the existing frontend/backend contract and flattening logic.
    /// </summary>
    public class SessionManager : IDisposable
    {
        private readonly PowerPoint.Application _app;
        private readonly SignalRService _signalR;
        private readonly InkOverlayService _inkOverlay;
        private readonly SpeechService _speech;
        private SlideShowEventHandler? _slideShowHandler;

        private string? _activePresentationId;
        private string? _activeSignalRGroupId;
        private string? _presenterAuthToken;
        private string? _authToken;
        private string? _activePresentationIdentity;
        private int _sessionLifecycleVersion = 0;

        private readonly string _backendBaseUrl;
        private readonly string _frontendBaseUrl;

        private int _currentSlideIndex = -1;
        private int _currentAbsoluteFrame = -1;
        private readonly Dictionary<int, int> _lastEmittedClickBySlide = new Dictionary<int, int>();

        private int _registrationLock = 0;
        private int _sessionEndLock = 0;

        private volatile bool _suppressInkClearedBroadcast = false;

        private readonly object _inkRequestLock = new object();
        private readonly Dictionary<string, DateTime> _recentInkStateRequests = new Dictionary<string, DateTime>();
        private static readonly TimeSpan InkStateRequestDedupeWindow = TimeSpan.FromSeconds(2);
        private static readonly HttpClient SharedHttpClient = new HttpClient();

        private readonly List<Task> _inFlightAdvanceTasks = new List<Task>();
        private readonly object _taskLock = new object();

        private readonly SlideStateStore _slideStates = new SlideStateStore();
        private readonly ConcurrentDictionary<int, List<InkStrokeData>> _frameStrokeCache =
            new ConcurrentDictionary<int, List<InkStrokeData>>();

        private readonly PresentationApiGateway _apiGateway;
        private readonly SignalRSyncAdapter _signalRAdapter;
        private readonly DeckIndexBuilder _deckIndexBuilder = new DeckIndexBuilder();
        private readonly SessionReducer _sessionReducer = new SessionReducer();
        private readonly InkStateRepository _inkStateRepository;
        private readonly object _reducerLock = new object();
        private readonly object _eventQueueLock = new object();
        private Task _eventQueueTail = Task.CompletedTask;
        private readonly object _inkRoutingLock = new object();
        private int _inkRouteSlideHint = -1;
        private int _inkRouteClickHint = 0;
        private readonly object _solutionLock = new object();
        private readonly Dictionary<string, SolutionPageState> _solutionPages = new Dictionary<string, SolutionPageState>(StringComparer.OrdinalIgnoreCase);
        private readonly List<SolutionPageState> _savedSolutionPages = new List<SolutionPageState>();
        private int _nextSolutionOrder = 1;
        private string? _activeSolutionPageId;
        private SolutionDraftLifecycleState _solutionDraftState = SolutionDraftLifecycleState.NormalSlideInk;
        private List<InkStrokeData> _overlayLiveSnapshotBeforeDraft = new List<InkStrokeData>();
        private int _overlayLiveSnapshotSlideIndex = -1;
        private int _overlayLiveSnapshotFrameIndex = -1;
        private readonly SynchronizationContext? _addInContext;

        private SessionState _sessionState;
        private DeckIndex? _deckIndex;

        private Dictionary<int, List<int>>? _slideAnimationMap;
        private List<FrameDescriptor> _frameDescriptors = new List<FrameDescriptor>();

        private const string AutoSlideShapeKeyTag = "AutoSlideShapeKey";

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        private sealed class FrameDescriptor
        {
            public int FrameIndex { get; }
            public int? ExportFrameIndex { get; }
            public int OriginalSlideIndex { get; }
            public int? ClickIndex { get; }
            public bool IsBoundary { get; }
            public int? BoundaryAfterSlideIndex { get; }

            public FrameDescriptor(
                int frameIndex,
                int? exportFrameIndex,
                int originalSlideIndex,
                int? clickIndex,
                bool isBoundary,
                int? boundaryAfterSlideIndex)
            {
                FrameIndex = frameIndex;
                ExportFrameIndex = exportFrameIndex;
                OriginalSlideIndex = originalSlideIndex;
                ClickIndex = clickIndex;
                IsBoundary = isBoundary;
                BoundaryAfterSlideIndex = boundaryAfterSlideIndex;
            }
        }

        private sealed class FrameDescriptorPayload
        {
            public int FrameIndex { get; set; }
            public int? ExportFrameIndex { get; set; }
            public int OriginalSlideIndex { get; set; }
            public int? ClickIndex { get; set; }
            public bool IsBoundary { get; set; }
            public int? BoundaryAfterSlideIndex { get; set; }
        }

        private volatile bool _isSharing;

        public string? ActivePresentationId => _activePresentationId;
        public string? ActiveSignalRGroupId => _activeSignalRGroupId;
        public event EventHandler<bool>? SharingStateChanged;

        public bool IsSharing
        {
            get => _isSharing;
            private set
            {
                if (_isSharing == value) return;
                _isSharing = value;
                SharingStateChanged?.Invoke(this, value);
            }
        }

        public int CurrentSlideIndex
        {
            get => _currentSlideIndex;
            set
            {
                _currentSlideIndex = value;
                if (value > 0)
                    UpdateInkRoutingHint(value, 0);
            }
        }

        public bool IsAuthenticated => !string.IsNullOrEmpty(_authToken);

        public bool HasActiveSession => IsSharing && !string.IsNullOrEmpty(_activePresentationId);

        internal bool IsPresentationActive(string presentationIdentity)
        {
            return HasActiveSession &&
                !string.IsNullOrEmpty(presentationIdentity) &&
                string.Equals(_activePresentationIdentity, presentationIdentity, StringComparison.OrdinalIgnoreCase);
        }

        public bool EndSessionOnLastSlide { get; set; } = false;

        public SessionManager(
            PowerPoint.Application app,
            SignalRService signalR,
            InkOverlayService inkOverlay,
            SpeechService speech,
            string backendBaseUrl)
        {
            _app = app;
            _signalR = signalR;
            _inkOverlay = inkOverlay;
            _speech = speech;
            _addInContext = SynchronizationContext.Current;
            _backendBaseUrl = backendBaseUrl;
            _frontendBaseUrl = ReadFrontendBaseUrl();

            _apiGateway = new PresentationApiGateway(SharedHttpClient);
            _signalRAdapter = new SignalRSyncAdapter(_signalR);
            _inkStateRepository = new InkStateRepository(_slideStates, _frameStrokeCache);
            _sessionState = new SessionState(_inkStateRepository);

            _signalR.InkStateRequested += OnInkStateRequested;
            _inkOverlay.StrokeCompleted += OnInkStrokeCompleted;
            _inkOverlay.InkStateChanged += OnInkStateChanged;
            _inkOverlay.InkCleared += OnInkCleared;
            _inkOverlay.BlankSolutionRequested += OnBlankSolutionRequestedFromOverlay;
            _inkOverlay.CurrentSlideSolutionRequested += OnCurrentSlideSolutionRequestedFromOverlay;
            _inkOverlay.SaveSolutionDraftRequested += OnSaveSolutionDraftRequestedFromOverlay;
            _inkOverlay.DiscardSolutionDraftRequested += OnDiscardSolutionDraftRequestedFromOverlay;
            _speech.TranscriptReceived += OnTranscriptReceived;
            _speech.ErrorOccurred += OnTranscriptionError;
        }

        internal void RegisterEventHandler(SlideShowEventHandler handler)
        {
            _slideShowHandler = handler;
        }

        public async Task StartSession(string courseId, string authToken)
        {
            _authToken = authToken;

            if (!string.IsNullOrEmpty(_activePresentationId))
            {
                var result = MessageBox.Show(
                    $"A session is already active ({_activePresentationId}).\nStart a NEW session for course {courseId}?",
                    "Session Active",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.No)
                {
                    IsSharing = true;
                    return;
                }

                StopSharing();
            }

            IsSharing = true;
            ResetRuntimeState();

            Debug.WriteLine("[SessionManager] === STARTSESSION BEGIN ===");
            Debug.WriteLine($"[SessionManager] _activeSignalRGroupId = {_activeSignalRGroupId ?? "NULL"}");
            Debug.WriteLine($"[SessionManager] IsSharing = {IsSharing}");

            try
            {
                var pres = _app.ActivePresentation;
                string? pptPath = pres.FullName;
                var name = pres.Name;
                var presentationIdentity = pres.FullName;

                _activePresentationIdentity = presentationIdentity;
                Debug.WriteLine($"[SessionManager] Starting session for presentation: {presentationIdentity}");

                string? tempCopy = null;
                if (!string.IsNullOrEmpty(pptPath) &&
                    (pptPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     pptPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        string? safeName = Sanitize(name);
                        if (string.IsNullOrEmpty(safeName)) safeName = "presentation.pptx";
                        tempCopy = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + "_" + safeName);
                        pres.SaveCopyAs(tempCopy);
                        pptPath = tempCopy;
                        Debug.WriteLine($"[SessionManager] Saved cloud copy to {pptPath}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SessionManager] Failed to save cloud copy: {ex}");
                        MessageBox.Show("Could not download cloud presentation. Please save a local copy first.", "Error");
                        IsSharing = false;
                        return;
                    }
                }
                else if (string.IsNullOrEmpty(pptPath))
                {
                    MessageBox.Show("Please save the presentation before sharing.", "Save Required");
                    IsSharing = false;
                    return;
                }

                string? flattenedPdfPath = null;
                string? tempFlatPptx = null;
                int totalFrames;

                try
                {
                    string? safeFlatName = Sanitize(name);
                    if (string.IsNullOrEmpty(safeFlatName)) safeFlatName = "presentation";
                    tempFlatPptx = Path.Combine(
                        Path.GetTempPath(),
                        Guid.NewGuid().ToString("n") + "_flat_" + safeFlatName + ".pptx");

                    pres.SaveCopyAs(tempFlatPptx);

                    var flatPres = _app.Presentations.Open(
                        tempFlatPptx,
                        ReadOnly: MsoTriState.msoFalse,
                        Untitled: MsoTriState.msoTrue,
                        WithWindow: MsoTriState.msoFalse);

                    try
                    {
                        _slideAnimationMap = FlattenAnimations(flatPres);
                        totalFrames = flatPres.Slides.Count;
                        Debug.WriteLine($"[SessionManager] Flattened: {totalFrames} total frames");

                        flattenedPdfPath = Path.Combine(
                            Path.GetTempPath(),
                            Guid.NewGuid().ToString("n") + "_flat_" + safeFlatName + ".pdf");

                        flatPres.ExportAsFixedFormat(
                            flattenedPdfPath,
                            PowerPoint.PpFixedFormatType.ppFixedFormatTypePDF);
                    }
                    finally
                    {
                        try { flatPres.Close(); } catch { }
                        Marshal.ReleaseComObject(flatPres);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionManager] Flattening failed, falling back to original PPTX: {ex}");
                    _slideAnimationMap = null;
                    _frameDescriptors.Clear();
                    flattenedPdfPath = null;
                    totalFrames = pres.Slides.Count;
                }

                RebuildDeckIndex();
                _sessionState.Reset();
                _recentInkStateRequests.Clear();
                _inkOverlay.FrameStore.Clear();
                Debug.WriteLine("[SessionManager] Session runtime state reset for new start.");
                _inkOverlay.ClearCanvas();
                _inkOverlay.Detach();

                string pptxUploadPath = tempCopy ?? pptPath;
                string? flatPdfUploadPath =
                    !string.IsNullOrEmpty(flattenedPdfPath) && File.Exists(flattenedPdfPath)
                    ? flattenedPdfPath
                    : null;

                var sessionLifecycleVersion = Volatile.Read(ref _sessionLifecycleVersion);

                Tuple<string, string, string, string>? registrationResult = null;
                bool didRegister =
                    string.IsNullOrEmpty(_activePresentationId) &&
                    Interlocked.CompareExchange(ref _registrationLock, 1, 0) == 0;

                if (didRegister)
                {
                    try
                    {
                        registrationResult = await UploadPresentationAndRegisterAsync(
                            pptxUploadPath,
                            name,
                            totalFrames,
                            courseId,
                            flatPdfUploadPath).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _registrationLock, 0);
                    }
                }
                else
                {
                    Debug.WriteLine("[SessionManager] Registration already in progress or session active.");
                }

                if (!string.IsNullOrEmpty(tempCopy) && File.Exists(tempCopy))
                {
                    try { File.Delete(tempCopy); } catch { }
                }

                if (!string.IsNullOrEmpty(flattenedPdfPath) && File.Exists(flattenedPdfPath))
                {
                    try { File.Delete(flattenedPdfPath); } catch { }
                }

                if (!string.IsNullOrEmpty(tempFlatPptx) && File.Exists(tempFlatPptx))
                {
                    try { File.Delete(tempFlatPptx); } catch { }
                }

                if (sessionLifecycleVersion != Volatile.Read(ref _sessionLifecycleVersion) || !IsSharing)
                {
                    Debug.WriteLine("[SessionManager] Ignoring stale start-session registration result.");
                    return;
                }

                if (registrationResult != null)
                {
                    _activePresentationId = registrationResult.Item1;
                    _presenterAuthToken = registrationResult.Item2;
                    var viewerUrl = registrationResult.Item3;
                    _activeSignalRGroupId = _activePresentationId;

                    IsSharing = !string.IsNullOrEmpty(_activeSignalRGroupId);

                    if (!string.IsNullOrEmpty(viewerUrl))
                        PresentViewerLink(BuildViewerUrl(viewerUrl));

                    await StartSessionOnServerAsync(_activePresentationId, _presenterAuthToken).ConfigureAwait(false);

                    await UpdateSlideAsync(_activePresentationId, _presenterAuthToken, 1).ConfigureAwait(false);
                    await AdvanceSlideOnServer(_activePresentationId, _presenterAuthToken, 1).ConfigureAwait(false);

                    RebindLiveSlideShowIfRunning();
                }
                else
                {
                    if (string.IsNullOrEmpty(_activePresentationId))
                    {
                        _activePresentationIdentity = null;
                        IsSharing = false;
                        MessageBox.Show("Failed to upload/register presentation.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] StartSession error: {ex}");
                _activePresentationIdentity = null;
                ResetRuntimeState();
                _inkOverlay.ClearCanvas();
                _inkOverlay.Detach();
                IsSharing = false;
                MessageBox.Show("Failed to start session: " + ex.Message);
            }
        }

        public void StopSharing()
        {
            if (!IsSharing) return;

            InvalidateSessionLifecycle();

            try
            {
                TryExportAnnotatedPptx(_app.ActivePresentation);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] Annotated PPTX export during StopSharing failed: {ex}");
            }

            IsSharing = false;
            Debug.WriteLine("[SessionManager] Slide publishing disabled.");

            _speech.Stop();

            var sessionIdToEnd = _activePresentationId;
            var tokenForCleanup = _presenterAuthToken;

            _activePresentationId = null;
            _activeSignalRGroupId = null;
            _presenterAuthToken = null;
            Interlocked.Exchange(ref _registrationLock, 0);
            Interlocked.Exchange(ref _sessionEndLock, 0);

            _slideStates.ClearAll();
            _frameStrokeCache.Clear();
            _inkOverlay.FrameStore.Clear();
            _inkOverlay.ClearCanvas();
            _inkOverlay.Detach();
            ResetRuntimeState();

            if (!string.IsNullOrEmpty(sessionIdToEnd))
            {
                var presentationIdToEnd = sessionIdToEnd!;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await EndSessionAsync(presentationIdToEnd, tokenForCleanup).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SessionManager] EndSessionAsync error during StopSharing: {ex}");
                    }
                });
            }
        }

        public void CreateBlankSolutionPage()
        {
            if (!CanCreateSolutionPage())
                return;

            try
            {
                var preferred = _inkOverlay.GetPreferredInkRenderSize();
                var background = CreateSolidBackgroundPng(preferred.Width, preferred.Height, Color.White);
                if (background == null || background.Length == 0)
                    throw new InvalidOperationException("Unable to initialize blank solution page background.");

                ActivateNewSolutionPage(SolutionPageKind.Blank, null, background, preferred.Width, preferred.Height);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] CreateBlankSolutionPage error: {ex}");
                MessageBox.Show("Failed to create blank solution page: " + ex.Message, "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public void CreateCurrentSlideSolutionPage()
        {
            if (!CanCreateSolutionPage())
                return;

            try
            {
                var sourceSlideIndex = ResolveCurrentSlideIndex();
                if (sourceSlideIndex <= 0)
                {
                    MessageBox.Show("No active slide is available to capture.", "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var capture = CaptureCurrentSlideBackgroundPng();
                if (capture == null || capture.Length == 0)
                {
                    MessageBox.Show("Unable to capture current slide snapshot.", "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var dimensions = GetPngDimensions(capture);
                if (dimensions.Width <= 0 || dimensions.Height <= 0)
                {
                    MessageBox.Show("Captured slide snapshot has invalid dimensions.", "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                ActivateNewSolutionPage(
                    SolutionPageKind.CurrentSlide,
                    sourceSlideIndex,
                    capture,
                    dimensions.Width,
                    dimensions.Height);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] CreateCurrentSlideSolutionPage error: {ex}");
                MessageBox.Show("Failed to create current-slide solution page: " + ex.Message, "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public void OnSessionBegin(PowerPoint.SlideShowWindow Wn)
        {
            var pres = Wn.Presentation;
            _activePresentationIdentity = pres.FullName;

            if (string.IsNullOrEmpty(_activeSignalRGroupId))
            {
                Debug.WriteLine("[SessionManager] SlideShowBegin ignored because no active session group.");
                return;
            }

            _speech.Start();

            var name = Path.GetFileNameWithoutExtension(pres.Name ?? "presentation");
            Debug.WriteLine($"[SessionManager] Session started for: {name}");

            if (!string.IsNullOrEmpty(_activePresentationId))
            {
                var slideNow = Wn.View.Slide;
                if (slideNow != null)
                {
                    var presentationId = _activePresentationId;
                    var presenterToken = _presenterAuthToken;
                    if (!string.IsNullOrEmpty(presentationId))
                    {
                        var nonNullPresentationId = presentationId!;
                        _ = Task.Run(async () => await UpdateSlideAsync(nonNullPresentationId, presenterToken, slideNow.SlideIndex));
                    }
                }

                return;
            }

            try
            {
                var pptPath = pres.FullName;
                string? tempCopy = null;
                bool createdTemp = false;

                if (!string.IsNullOrEmpty(pptPath) &&
                    (pptPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     pptPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var safeName = Sanitize(name) ?? "presentation";
                        var tmpName = safeName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pptx";
                        tempCopy = Path.Combine(Path.GetTempPath(), tmpName);
                        pres.SaveCopyAs(tempCopy);
                        pptPath = tempCopy;
                        createdTemp = true;
                    }
                    catch
                    {
                        pptPath = null;
                    }
                }

                int total = pres.Slides.Count;

                if (!string.IsNullOrEmpty(pptPath))
                {
                    var uploadPath = pptPath;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var sessionLifecycleVersion = Volatile.Read(ref _sessionLifecycleVersion);

                            bool shouldRegister = string.IsNullOrEmpty(_activePresentationId)
                                && Interlocked.CompareExchange(ref _registrationLock, 1, 0) == 0;

                            if (!shouldRegister)
                                return;

                            try
                            {
                                var bgResult = await UploadPresentationAndRegisterAsync(uploadPath, name, total, null, null).ConfigureAwait(false);
                                if (bgResult != null)
                                {
                                    if (sessionLifecycleVersion != Volatile.Read(ref _sessionLifecycleVersion) || !IsSharing)
                                    {
                                        Debug.WriteLine("[SessionManager] Ignoring stale background registration result.");
                                        return;
                                    }

                                    _activePresentationId = bgResult.Item1;
                                    _presenterAuthToken = bgResult.Item2;
                                    var viewerUrl = bgResult.Item3;
                                    _activeSignalRGroupId = _activePresentationId;

                                    if (!string.IsNullOrEmpty(viewerUrl))
                                        PresentViewerLink(BuildViewerUrl(viewerUrl));

                                    await StartSessionOnServerAsync(_activePresentationId, _presenterAuthToken).ConfigureAwait(false);
                                    await UpdateSlideAsync(_activePresentationId, _presenterAuthToken, 1).ConfigureAwait(false);
                                    await AdvanceSlideOnServer(_activePresentationId, _presenterAuthToken, 1).ConfigureAwait(false);

                                    RebindLiveSlideShowIfRunning();

                                    var slideNow = Wn.View.Slide;
                                    if (slideNow != null)
                                        await UpdateSlideAsync(_activePresentationId, _presenterAuthToken, slideNow.SlideIndex).ConfigureAwait(false);
                                }
                            }
                            finally
                            {
                                Interlocked.Exchange(ref _registrationLock, 0);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SessionManager] Background registration error: {ex}");
                        }
                        finally
                        {
                            if (createdTemp && !string.IsNullOrEmpty(tempCopy))
                            {
                                try { File.Delete(tempCopy); } catch { }
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] Registration error: {ex}");
            }
        }

        public void OnSlideChanged(int previousSlideIndex, int newSlideIndex, int totalSlides)
        {
            DeactivateSolutionContext();
            UpdateInkRoutingHint(newSlideIndex, 0);

            _ = EnqueueSerializedEventAsync(async () =>
            {
                var result = ReduceEvent(new SlideChangedSessionEvent(
                    previousSlideIndex,
                    newSlideIndex,
                    totalSlides,
                    EndSessionOnLastSlide));

                SyncLegacyCursorFromSessionState();
                await ExecuteNetworkActionsAsync(result.NetworkActions).ConfigureAwait(false);

                if (result.ResetBroadcastDedup)
                    _slideShowHandler?.ResetBroadcastDedup();

                Debug.WriteLine($"[SessionManager] Slide transition {previousSlideIndex} -> {newSlideIndex}");
                await Task.CompletedTask;
            });
        }

        public async void OnFrameAdvanced(int slideIndex, int clickIndex, bool isBackfill = false)
        {
            DeactivateSolutionContext();
            UpdateInkRoutingHint(slideIndex, clickIndex);

            await EnqueueSerializedEventAsync(async () =>
            {
                int previousSlideIndex = _currentSlideIndex;
                int previousAbsoluteFrame = _currentAbsoluteFrame;

                var result = ReduceEvent(new FrameAdvancedSessionEvent(slideIndex, clickIndex, isBackfill));
                var decision = result.FrameDecision;
                if (decision == null)
                    return;

                if (previousAbsoluteFrame > 0)
                {
                    if (decision.PhysicalSlideChanged)
                    {
                        CaptureAndUploadInk(previousSlideIndex, previousAbsoluteFrame, clearCanvas: true);

                        var strokesForNewSlide = _inkStateRepository.GetStrokesForSlide(decision.SlideIndex);
                        if (strokesForNewSlide.Count > 0)
                        {
                            await _inkOverlay.LoadStrokesFromDataAsync(strokesForNewSlide);
                        }

                        _inkStateRepository.SetFrameStrokes(decision.ExportFrameIndex, strokesForNewSlide);

                        var groupId = _activeSignalRGroupId;
                        if (!string.IsNullOrEmpty(groupId))
                        {
                            _ = Task.Run(async () =>
                                await _signalRAdapter.BroadcastFullInkStateAsync(groupId, decision.ExportFrameIndex, strokesForNewSlide));
                        }
                    }
                    else
                    {
                        if (!_inkStateRepository.TryGetFrameStrokes(decision.ExportFrameIndex, out var strokesForCurrentSlide))
                            strokesForCurrentSlide = _inkStateRepository.GetStrokesForSlide(decision.SlideIndex);

                        _inkStateRepository.SetFrameStrokes(decision.ExportFrameIndex, strokesForCurrentSlide);

                        var groupId = _activeSignalRGroupId;
                        if (!string.IsNullOrEmpty(groupId))
                        {
                            _ = Task.Run(async () =>
                                await _signalRAdapter.BroadcastFullInkStateAsync(groupId, decision.ExportFrameIndex, strokesForCurrentSlide));
                        }
                    }
                }

                SyncLegacyCursorFromSessionState();
                await ExecuteNetworkActionsAsync(result.NetworkActions).ConfigureAwait(false);

                Debug.WriteLine(
                    $"[SessionManager] ServerProgression exportFrame={decision.ExportFrameIndex} from slide={decision.SlideIndex} click={decision.ResolvedClickIndex}" +
                    (decision.IsBackfill ? " [BACKFILL]" : "") +
                    (decision.IsBackwardNavigation ? " [BACKWARD]" : ""));
            });
        }

        private Task EnqueueSerializedEventAsync(Func<Task> work)
        {
            lock (_eventQueueLock)
            {
                _eventQueueTail = _eventQueueTail.ContinueWith(async _ =>
                {
                    try
                    {
                        await work().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SessionManager] Serialized event processing error: {ex}");
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

                return _eventQueueTail;
            }
        }

        public void OnSessionEnd(PowerPoint.Presentation Pres)
        {
            Debug.WriteLine($"[SessionManager] Session ended for: {Pres?.Name}");

            InvalidateSessionLifecycle();

            if (Interlocked.CompareExchange(ref _registrationLock, 0, 0) != 0)
                Debug.WriteLine("[SessionManager] Session end requested while registration is still running; continuing cleanup.");

            if (_currentSlideIndex > 0 && !string.IsNullOrEmpty(_activeSignalRGroupId))
                CaptureAndUploadInk(_currentSlideIndex, _currentAbsoluteFrame, true);

            try
            {
                TryExportAnnotatedPptx(Pres);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] Failed to export annotated presentation on session end: {ex}");
            }

            _currentSlideIndex = -1;
            _currentAbsoluteFrame = -1;
            _speech.Stop();

            if (!string.IsNullOrEmpty(_activePresentationId))
            {
                var presentationId = _activePresentationId;
                var authToken = _presenterAuthToken;
                var nonNullPresentationId = presentationId!;
                _ = Task.Run(async () => await EndSessionAsync(nonNullPresentationId, authToken));
            }

            IsSharing = false;
            Interlocked.Exchange(ref _registrationLock, 0);

            _activePresentationId = null;
            _activeSignalRGroupId = null;
            _presenterAuthToken = null;
            _slideStates.ClearAll();
            _frameStrokeCache.Clear();
            _inkOverlay.FrameStore.Clear();
            _inkOverlay.ClearCanvas();
            _inkOverlay.Detach();
            ResetRuntimeState();
            Interlocked.Exchange(ref _sessionEndLock, 0);
        }

        private void OnInkStrokeCompleted(object sender, InkStrokeData strokeData)
        {
            if (string.IsNullOrEmpty(_activeSignalRGroupId))
                return;

            if (IsDraftOrSavingSolutionState())
            {
                AppendDraftStroke(strokeData);
                return;
            }

            GetInkRoutingHint(out var routedSlideHint, out var routedClickHint);

            int slideIndex = routedSlideHint > 0
                ? routedSlideHint
                : (_currentSlideIndex > 0 ? _currentSlideIndex : 1);

            int fallbackFrame = _currentAbsoluteFrame > 0 ? _currentAbsoluteFrame : slideIndex;
            int frameIndex = ResolveImmediateExportFrameForInk(slideIndex, routedClickHint, fallbackFrame);

            Debug.WriteLine($"[SessionManager] InkIdentifiers liveBroadcastFrame={frameIndex} sourceSlide={slideIndex} click={routedClickHint}");

            _inkStateRepository.AddStroke(slideIndex, strokeData);
            var updatedStrokes = _inkStateRepository.GetStrokesForSlide(slideIndex);

            PropagateSlideInkChange(slideIndex, updatedStrokes, frameIndex, broadcastToGroup: false);
            UploadInkSnapshotForSlide(slideIndex, updatedStrokes);

            var groupId = _activeSignalRGroupId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _signalRAdapter.BroadcastStrokeAsync(groupId, frameIndex, strokeData);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionManager] Broadcast stroke failed: {ex.Message}");
                }
            });

            BroadcastFullStateToNonActiveFrames(slideIndex, frameIndex, updatedStrokes);
        }

        private void OnInkStateChanged(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_activeSignalRGroupId)) return;
            if (_suppressInkClearedBroadcast) return;

            if (IsDraftOrSavingSolutionState())
            {
                RefreshDraftStrokeBufferFromOverlay();
                return;
            }

            GetInkRoutingHint(out var routedSlideHint, out var routedClickHint);

            int slideIndex = routedSlideHint > 0
                ? routedSlideHint
                : (_currentSlideIndex > 0 ? _currentSlideIndex : 1);

            int fallbackFrame = _currentAbsoluteFrame > 0 ? _currentAbsoluteFrame : slideIndex;
            int frameIndex = ResolveImmediateExportFrameForInk(slideIndex, routedClickHint, fallbackFrame);

            var updatedStrokes = _inkOverlay.GetCurrentStrokeData();
            PropagateSlideInkChange(slideIndex, updatedStrokes, frameIndex, broadcastToGroup: true);
            UploadInkSnapshotForSlide(slideIndex, updatedStrokes);
        }

        private void OnInkCleared(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_activeSignalRGroupId))
                return;

            if (_suppressInkClearedBroadcast)
                return;

            if (IsDraftOrSavingSolutionState())
            {
                RefreshDraftStrokeBufferFromOverlay();
                return;
            }

            GetInkRoutingHint(out var routedSlideHint, out var routedClickHint);

            int slideIndex = routedSlideHint > 0
                ? routedSlideHint
                : (_currentSlideIndex > 0 ? _currentSlideIndex : 1);

            int fallbackFrame = _currentAbsoluteFrame > 0 ? _currentAbsoluteFrame : slideIndex;
            int frameIndex = ResolveImmediateExportFrameForInk(slideIndex, routedClickHint, fallbackFrame);

            PropagateSlideInkChange(slideIndex, new List<InkStrokeData>(), frameIndex, broadcastToGroup: true);
            UploadInkSnapshotForSlide(slideIndex, new List<InkStrokeData>());

            var groupId = _activeSignalRGroupId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _signalRAdapter.BroadcastClearAsync(groupId, frameIndex);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionManager] Broadcast clear failed: {ex.Message}");
                }
            });
        }

        private void OnBlankSolutionRequestedFromOverlay(object sender, EventArgs e)
        {
            ExecuteOnAddInContext(() => CreateBlankSolutionPage());
        }

        private void OnCurrentSlideSolutionRequestedFromOverlay(object sender, EventArgs e)
        {
            ExecuteOnAddInContext(() => CreateCurrentSlideSolutionPage());
        }

        private void OnSaveSolutionDraftRequestedFromOverlay(object sender, EventArgs e)
        {
            ExecuteOnAddInContext(() => SaveActiveSolutionDraft());
        }

        private void OnDiscardSolutionDraftRequestedFromOverlay(object sender, EventArgs e)
        {
            ExecuteOnAddInContext(() => DeactivateSolutionContext());
        }

        private void ExecuteOnAddInContext(Action action)
        {
            if (action == null)
                return;

            var context = _addInContext;
            if (context == null)
            {
                action();
                return;
            }

            context.Post(_ =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionManager] Overlay action failed: {ex}");
                }
            }, null);
        }

        private static bool IsDraftState(SolutionDraftLifecycleState state)
        {
            return state == SolutionDraftLifecycleState.DraftBlankSolution
                || state == SolutionDraftLifecycleState.DraftCurrentSlideSolution;
        }

        private bool IsDraftOrSavingSolutionState()
        {
            lock (_solutionLock)
            {
                return IsDraftState(_solutionDraftState)
                    || _solutionDraftState == SolutionDraftLifecycleState.SavingSolution;
            }
        }

        private void CaptureLiveOverlaySnapshotForDraft()
        {
            GetInkRoutingHint(out var routedSlideHint, out var routedClickHint);

            int slideIndex = routedSlideHint > 0
                ? routedSlideHint
                : (_currentSlideIndex > 0 ? _currentSlideIndex : 1);

            int fallbackFrame = _currentAbsoluteFrame > 0 ? _currentAbsoluteFrame : slideIndex;
            int frameIndex = ResolveImmediateExportFrameForInk(slideIndex, routedClickHint, fallbackFrame);
            var liveStrokes = CloneStrokeList(_inkOverlay.GetCurrentStrokeData());

            lock (_solutionLock)
            {
                _overlayLiveSnapshotBeforeDraft = liveStrokes;
                _overlayLiveSnapshotSlideIndex = slideIndex;
                _overlayLiveSnapshotFrameIndex = frameIndex;
            }
        }

        private void RestoreLiveOverlaySnapshotAfterDraft()
        {
            List<InkStrokeData> snapshot;

            lock (_solutionLock)
                snapshot = CloneStrokeList(_overlayLiveSnapshotBeforeDraft);

            _suppressInkClearedBroadcast = true;
            try
            {
                _inkOverlay.LoadStrokesFromDataAsync(snapshot).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] RestoreLiveOverlaySnapshotAfterDraft failed: {ex}");
            }
            finally
            {
                _suppressInkClearedBroadcast = false;
            }
        }

        private void ClearLiveOverlayDraftSnapshot()
        {
            lock (_solutionLock)
            {
                _overlayLiveSnapshotBeforeDraft = new List<InkStrokeData>();
                _overlayLiveSnapshotSlideIndex = -1;
                _overlayLiveSnapshotFrameIndex = -1;
            }
        }

        private void AppendDraftStroke(InkStrokeData strokeData)
        {
            if (strokeData == null)
                return;

            lock (_solutionLock)
            {
                var activeSolutionId = _activeSolutionPageId;
                if (string.IsNullOrEmpty(activeSolutionId) ||
                    !_solutionPages.TryGetValue(activeSolutionId!, out var active))
                    return;

                var draftStrokes = CloneStrokeList(active.LatestStrokes);
                draftStrokes.Add(strokeData);
                active.LatestStrokes = draftStrokes;
                active.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        private void RefreshDraftStrokeBufferFromOverlay()
        {
            lock (_solutionLock)
            {
                var activeSolutionId = _activeSolutionPageId;
                if (string.IsNullOrEmpty(activeSolutionId) ||
                    !_solutionPages.TryGetValue(activeSolutionId!, out var active))
                    return;

                active.LatestStrokes = CloneStrokeList(_inkOverlay.GetCurrentStrokeData());
                active.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        private void ApplySolutionDraftUiState()
        {
            SolutionDraftLifecycleState state;
            bool useBlankMask = false;

            lock (_solutionLock)
            {
                state = _solutionDraftState;
                var activeSolutionId = _activeSolutionPageId;
                if (!string.IsNullOrEmpty(activeSolutionId) &&
                    _solutionPages.TryGetValue(activeSolutionId!, out var active))
                {
                    useBlankMask = active.Kind == SolutionPageKind.Blank;
                }
            }

            _inkOverlay.SetSolutionDraftState(state);

            if (useBlankMask && state != SolutionDraftLifecycleState.NormalSlideInk)
                _inkOverlay.EnableBlankSolutionSurface();
            else
                _inkOverlay.DisableBlankSolutionSurface();
        }

        private void SaveActiveSolutionDraft()
        {
            if (!IsSharing || string.IsNullOrEmpty(_activePresentationId) || string.IsNullOrEmpty(_presenterAuthToken))
            {
                MessageBox.Show("Start a live session before saving solution pages.", "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SolutionPageState snapshot;

            lock (_solutionLock)
            {
                if (_solutionDraftState == SolutionDraftLifecycleState.SavingSolution)
                    return;

                if (!IsDraftState(_solutionDraftState) ||
                    string.IsNullOrEmpty(_activeSolutionPageId) ||
                    !_solutionPages.TryGetValue(_activeSolutionPageId!, out var active))
                {
                    MessageBox.Show("No active solution draft to save.", "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var currentStrokes = CloneStrokeList(_inkOverlay.GetCurrentStrokeData());
                active.LatestStrokes = currentStrokes;
                active.UpdatedAtUtc = DateTime.UtcNow;
                snapshot = CloneSolutionState(active);
                _solutionDraftState = SolutionDraftLifecycleState.SavingSolution;
            }

            ApplySolutionDraftUiState();

            var sessionId = _activePresentationId;
            var presenterToken = _presenterAuthToken;
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(presenterToken))
            {
                lock (_solutionLock)
                {
                    _solutionDraftState = snapshot.Kind == SolutionPageKind.Blank
                        ? SolutionDraftLifecycleState.DraftBlankSolution
                        : SolutionDraftLifecycleState.DraftCurrentSlideSolution;
                }
                ApplySolutionDraftUiState();
                MessageBox.Show("Session is no longer active.", "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var artifactBytes = ComposeSolutionArtifact(snapshot);
                    if (artifactBytes == null || artifactBytes.Length == 0)
                        throw new InvalidOperationException("Generated artifact is empty.");

                    var kind = snapshot.Kind == SolutionPageKind.CurrentSlide ? "currentSlide" : "blank";
                    var response = await _apiGateway.UploadSolutionArtifactAsync(
                        _backendBaseUrl,
                        sessionId,
                        presenterToken,
                        snapshot.SolutionPageId,
                        artifactBytes,
                        snapshot.HasInk,
                        kind,
                        snapshot.SourceSlideIndex,
                        snapshot.OrderIndex).ConfigureAwait(false);

                    if (response == null || !response.Success)
                        throw new InvalidOperationException("Server rejected solution page save request.");

                    ExecuteOnAddInContext(() =>
                    {
                        string? activeSolutionId;
                        var persistedSnapshot = CloneSolutionState(snapshot);
                        lock (_solutionLock)
                        {
                            if (!string.IsNullOrEmpty(response.SolutionPageId) &&
                                !string.Equals(response.SolutionPageId, snapshot.SolutionPageId, StringComparison.OrdinalIgnoreCase))
                            {
                                RenameSolutionPageStateId(snapshot.SolutionPageId, response.SolutionPageId);
                                persistedSnapshot.SolutionPageId = response.SolutionPageId;
                            }

                            activeSolutionId = _activeSolutionPageId;
                        }

                        AddOrUpdateSavedSolutionPage(persistedSnapshot);

                        RestoreLiveOverlaySnapshotAfterDraft();

                        lock (_solutionLock)
                        {
                            if (!string.IsNullOrEmpty(activeSolutionId))
                                _solutionPages.Remove(activeSolutionId!);

                            _activeSolutionPageId = null;
                            _solutionDraftState = SolutionDraftLifecycleState.NormalSlideInk;
                        }

                        ClearLiveOverlayDraftSnapshot();
                        ApplySolutionDraftUiState();
                        Debug.WriteLine($"[SessionManager] Saved solution draft {snapshot.SolutionPageId} bytes={artifactBytes.Length} hasInk={snapshot.HasInk}");
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionManager] SaveActiveSolutionDraft failed: {ex}");
                    ExecuteOnAddInContext(() =>
                    {
                        lock (_solutionLock)
                        {
                            if (_solutionDraftState == SolutionDraftLifecycleState.SavingSolution)
                            {
                                _solutionDraftState = snapshot.Kind == SolutionPageKind.Blank
                                    ? SolutionDraftLifecycleState.DraftBlankSolution
                                    : SolutionDraftLifecycleState.DraftCurrentSlideSolution;
                            }
                        }

                        ApplySolutionDraftUiState();
                        MessageBox.Show("Failed to save solution draft. The draft is still open. " + ex.Message, "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
            });
        }

        private async void OnInkStateRequested(string connectionId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(connectionId))
                    return;

                if (!ShouldServeInkStateRequest(connectionId))
                    return;

                var absoluteFrameMap = LateJoinerInkStateBuilder.BuildAbsoluteFrameMap(
                    _inkStateRepository,
                    _deckIndex);

                var sent = await _signalRAdapter.SendInkStateToClientAsync(connectionId, absoluteFrameMap).ConfigureAwait(false);
                if (!sent)
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    await _signalRAdapter.SendInkStateToClientAsync(connectionId, absoluteFrameMap).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] InkStateRequested error: {ex.Message}");
            }
        }

        private bool ShouldServeInkStateRequest(string connectionId)
        {
            var now = DateTime.UtcNow;
            lock (_inkRequestLock)
            {
                var stale = _recentInkStateRequests
                    .Where(kvp => now - kvp.Value > InkStateRequestDedupeWindow)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in stale)
                    _recentInkStateRequests.Remove(key);

                if (_recentInkStateRequests.TryGetValue(connectionId, out var last) &&
                    now - last <= InkStateRequestDedupeWindow)
                {
                    return false;
                }

                _recentInkStateRequests[connectionId] = now;
                return true;
            }
        }

        private void OnTranscriptReceived(object sender, string text)
        {
            _ = Task.Run(async () => await SendTranscriptAsync(text));
        }

        private void OnTranscriptionError(object sender, string errorMessage)
        {
            Debug.WriteLine($"[SessionManager] Transcription error: {errorMessage}");
        }

        internal void CaptureAndUploadInk(int slideIndex, int _frameIndex, bool clearCanvas)
        {
            try
            {
                var vectorStrokesSnapshot = _inkStateRepository.GetStrokesForSlide(slideIndex);
                Debug.WriteLine($"[SessionManager] CaptureAndUploadInk sourceSlide={slideIndex} strokes={vectorStrokesSnapshot.Count}");
                UploadInkSnapshotForSlide(slideIndex, vectorStrokesSnapshot);

                if (clearCanvas)
                {
                    _suppressInkClearedBroadcast = true;
                    _inkOverlay.ClearInk();
                    _suppressInkClearedBroadcast = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] CaptureAndUploadInk error: {ex}");
            }
        }

        private bool CanCreateSolutionPage()
        {
            if (!IsSharing || string.IsNullOrEmpty(_activePresentationId) || string.IsNullOrEmpty(_presenterAuthToken))
            {
                MessageBox.Show("Start a live session before creating solution pages.", "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            lock (_solutionLock)
            {
                if (_solutionDraftState == SolutionDraftLifecycleState.SavingSolution)
                {
                    MessageBox.Show("A solution save is currently in progress.", "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                if (IsDraftState(_solutionDraftState))
                {
                    MessageBox.Show("Save or discard the current draft before creating a new solution page.", "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
            }

            return true;
        }

        private int ResolveCurrentSlideIndex()
        {
            try
            {
                if (_app.SlideShowWindows != null && _app.SlideShowWindows.Count > 0)
                {
                    var currentSlide = _app.SlideShowWindows[1]?.View?.Slide;
                    if (currentSlide != null && currentSlide.SlideIndex > 0)
                        return currentSlide.SlideIndex;
                }
            }
            catch
            {
                // Fallback to session cursor below.
            }

            return _currentSlideIndex > 0 ? _currentSlideIndex : 1;
        }

        private byte[]? CaptureCurrentSlideBackgroundPng()
        {
            var preferred = _inkOverlay.GetPreferredInkRenderSize();

            if (TryCaptureCurrentResolvedFrameBackgroundPng(preferred.Width, preferred.Height, out var framePng) &&
                framePng != null && framePng.Length > 0)
            {
                return framePng;
            }

            int slideIndex = ResolveCurrentSlideIndex();
            if (slideIndex <= 0)
                return null;

            string tempPath = Path.Combine(Path.GetTempPath(), "autoslide_solution_" + Guid.NewGuid().ToString("N") + ".png");

            PowerPoint.Slide? slide = null;
            try
            {
                var pres = _app.ActivePresentation;
                if (pres == null || slideIndex > pres.Slides.Count)
                    return null;

                slide = pres.Slides[slideIndex];
                slide.Export(tempPath, "PNG", preferred.Width, preferred.Height);

                if (!File.Exists(tempPath))
                    return null;

                return File.ReadAllBytes(tempPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] CaptureCurrentSlideBackgroundPng error: {ex}");
                return null;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }

                try
                {
                    if (slide != null)
                        Marshal.ReleaseComObject(slide);
                }
                catch { }
            }
        }

        private bool TryCaptureCurrentResolvedFrameBackgroundPng(int outputWidth, int outputHeight, out byte[]? pngBytes)
        {
            pngBytes = null;

            PowerPoint.SlideShowWindow? slideShowWindow = null;
            bool overlayWasVisible = false;

            try
            {
                if (_app.SlideShowWindows == null || _app.SlideShowWindows.Count <= 0)
                    return false;

                slideShowWindow = _app.SlideShowWindows[1];
                int hwndValue = slideShowWindow.HWND;
                if (hwndValue == 0)
                    return false;

                if (!GetWindowRect((IntPtr)hwndValue, out var rect))
                    return false;

                int windowWidth = rect.Right - rect.Left;
                int windowHeight = rect.Bottom - rect.Top;
                if (windowWidth <= 0 || windowHeight <= 0)
                    return false;

                var presentation = _app.ActivePresentation;
                double slideWidth = presentation?.PageSetup?.SlideWidth ?? 0;
                double slideHeight = presentation?.PageSetup?.SlideHeight ?? 0;
                double slideAspect = (slideWidth > 0 && slideHeight > 0)
                    ? (slideWidth / slideHeight)
                    : (16d / 9d);

                overlayWasVisible = _inkOverlay.HideOverlayForBackgroundCapture();

                using var captured = new Bitmap(windowWidth, windowHeight, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(captured))
                {
                    graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(windowWidth, windowHeight), CopyPixelOperation.SourceCopy);
                }

                double winAspect = (double)windowWidth / windowHeight;
                Rectangle sourceRect;
                if (slideAspect > winAspect)
                {
                    int contentHeight = Math.Max(1, (int)Math.Round(windowWidth / slideAspect));
                    int offsetY = Math.Max(0, (windowHeight - contentHeight) / 2);
                    sourceRect = new Rectangle(0, offsetY, windowWidth, Math.Min(contentHeight, windowHeight - offsetY));
                }
                else
                {
                    int contentWidth = Math.Max(1, (int)Math.Round(windowHeight * slideAspect));
                    int offsetX = Math.Max(0, (windowWidth - contentWidth) / 2);
                    sourceRect = new Rectangle(offsetX, 0, Math.Min(contentWidth, windowWidth - offsetX), windowHeight);
                }

                using var cropped = new Bitmap(sourceRect.Width, sourceRect.Height, PixelFormat.Format32bppArgb);
                using (var cropGraphics = Graphics.FromImage(cropped))
                {
                    cropGraphics.DrawImage(captured, new Rectangle(0, 0, cropped.Width, cropped.Height), sourceRect, GraphicsUnit.Pixel);
                }

                int targetWidth = Math.Max(1, outputWidth);
                int targetHeight = Math.Max(1, outputHeight);
                using var scaled = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
                using (var scaleGraphics = Graphics.FromImage(scaled))
                {
                    scaleGraphics.CompositingMode = CompositingMode.SourceCopy;
                    scaleGraphics.CompositingQuality = CompositingQuality.HighQuality;
                    scaleGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    scaleGraphics.SmoothingMode = SmoothingMode.HighQuality;
                    scaleGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    scaleGraphics.DrawImage(cropped, new Rectangle(0, 0, targetWidth, targetHeight));
                }

                using var output = new MemoryStream();
                scaled.Save(output, ImageFormat.Png);
                pngBytes = output.ToArray();
                return pngBytes.Length > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] TryCaptureCurrentResolvedFrameBackgroundPng failed: {ex}");
                return false;
            }
            finally
            {
                if (overlayWasVisible)
                    _inkOverlay.RestoreOverlayAfterBackgroundCapture(true);

                try
                {
                    if (slideShowWindow != null)
                        Marshal.ReleaseComObject(slideShowWindow);
                }
                catch { }
            }
        }

        private void ActivateNewSolutionPage(
            SolutionPageKind kind,
            int? sourceSlideIndex,
            byte[] backgroundBytes,
            int renderWidth,
            int renderHeight)
        {
            DeactivateSolutionContext();
            CaptureLiveOverlaySnapshotForDraft();

            SolutionPageState state;

            lock (_solutionLock)
            {
                var orderIndex = _nextSolutionOrder;
                var localId = "solution-" + orderIndex.ToString("D3");
                _nextSolutionOrder++;

                state = new SolutionPageState
                {
                    SessionId = _activePresentationId,
                    SolutionPageId = localId,
                    Kind = kind,
                    SourceSlideIndex = sourceSlideIndex,
                    OrderIndex = orderIndex,
                    RenderWidth = renderWidth,
                    RenderHeight = renderHeight,
                    BackgroundImageBytes = backgroundBytes ?? Array.Empty<byte>(),
                    LatestStrokes = new List<InkStrokeData>(),
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };

                _solutionPages[localId] = state;
                _activeSolutionPageId = localId;
                _solutionDraftState = kind == SolutionPageKind.Blank
                    ? SolutionDraftLifecycleState.DraftBlankSolution
                    : SolutionDraftLifecycleState.DraftCurrentSlideSolution;
            }

            _inkOverlay.ClearInk();
            ApplySolutionDraftUiState();

            MessageBox.Show(
                $"{state.SolutionPageId} draft started. Draw and click Save Solution when ready.",
                "Solution Page",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void RenameSolutionPageStateId(string currentId, string newId)
        {
            if (string.IsNullOrWhiteSpace(currentId) || string.IsNullOrWhiteSpace(newId) ||
                string.Equals(currentId, newId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (_solutionLock)
            {
                if (!_solutionPages.TryGetValue(currentId, out var state))
                    return;

                state.SolutionPageId = newId;
                _solutionPages.Remove(currentId);
                _solutionPages[newId] = state;

                if (string.Equals(_activeSolutionPageId, currentId, StringComparison.OrdinalIgnoreCase))
                    _activeSolutionPageId = newId;
            }
        }

        private string GetLatestSolutionPageId(string fallbackId)
        {
            lock (_solutionLock)
            {
                var activeSolutionId = _activeSolutionPageId;
                if (!string.IsNullOrEmpty(activeSolutionId) &&
                    _solutionPages.ContainsKey(activeSolutionId!) &&
                    string.Equals(fallbackId, activeSolutionId, StringComparison.OrdinalIgnoreCase))
                {
                    return activeSolutionId;
                }
            }

            return fallbackId;
        }

        private bool TryGetActiveSolutionPage(out SolutionPageState? state)
        {
            lock (_solutionLock)
            {
                var activeSolutionId = _activeSolutionPageId;
                if (!string.IsNullOrEmpty(activeSolutionId) &&
                    _solutionPages.TryGetValue(activeSolutionId!, out var active))
                {
                    state = CloneSolutionState(active);
                    return true;
                }
            }

            state = null;
            return false;
        }

        private bool TryGetSolutionPage(string solutionPageId, out SolutionPageState? state)
        {
            lock (_solutionLock)
            {
                if (!string.IsNullOrEmpty(solutionPageId) && _solutionPages.TryGetValue(solutionPageId, out var existing))
                {
                    state = CloneSolutionState(existing);
                    return true;
                }
            }

            state = null;
            return false;
        }

        private void UpdateSolutionPageStrokes(string solutionPageId, List<InkStrokeData> updatedStrokes)
        {
            lock (_solutionLock)
            {
                if (!_solutionPages.TryGetValue(solutionPageId, out var existing))
                    return;

                existing.LatestStrokes = CloneStrokeList(updatedStrokes);
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        private void AddOrUpdateSavedSolutionPage(SolutionPageState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.SolutionPageId))
                return;

            lock (_solutionLock)
            {
                _savedSolutionPages.RemoveAll(p =>
                    string.Equals(p.SolutionPageId, state.SolutionPageId, StringComparison.OrdinalIgnoreCase));
                _savedSolutionPages.Add(CloneSolutionState(state));
            }
        }

        private List<SolutionPageState> GetSavedSolutionPagesSnapshot()
        {
            lock (_solutionLock)
            {
                return _savedSolutionPages
                    .Select(CloneSolutionState)
                    .OrderBy(p => p.OrderIndex)
                    .ThenBy(p => p.CreatedAtUtc)
                    .ToList();
            }
        }

        private void UploadSolutionSnapshot(string solutionPageId)
        {
            var sessionId = _activePresentationId;
            var token = _presenterAuthToken;

            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(solutionPageId))
                return;

            if (!TryGetSolutionPage(solutionPageId, out var state) || state == null)
                return;

            var artifactBytes = ComposeSolutionArtifact(state);
            if (artifactBytes == null || artifactBytes.Length == 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _apiGateway.UploadSolutionArtifactAsync(
                        _backendBaseUrl,
                        sessionId,
                        token,
                        state.SolutionPageId,
                        artifactBytes,
                        state.HasInk,
                        state.Kind == SolutionPageKind.CurrentSlide ? "currentSlide" : "blank",
                        state.SourceSlideIndex,
                        state.OrderIndex).ConfigureAwait(false);

                    Debug.WriteLine($"[SessionManager] Uploaded solution artifact {state.SolutionPageId} bytes={artifactBytes.Length} hasInk={state.HasInk}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionManager] UploadSolutionSnapshot error for {state.SolutionPageId}: {ex}");
                }
            });
        }

        private byte[] ComposeSolutionArtifact(SolutionPageState state)
        {
            var background = state.BackgroundImageBytes ?? Array.Empty<byte>();

            if (background.Length == 0)
                background = CreateSolidBackgroundPng(state.RenderWidth, state.RenderHeight, Color.White) ?? Array.Empty<byte>();

            if (state.LatestStrokes == null || state.LatestStrokes.Count == 0)
                return background;

            if (state.Kind == SolutionPageKind.Blank)
            {
                return _inkOverlay.GeneratePngFromVectorData(
                    CloneStrokeList(state.LatestStrokes),
                    state.RenderWidth,
                    state.RenderHeight,
                    transparentBackground: false) ?? background;
            }

            var overlayBytes = _inkOverlay.GeneratePngFromVectorData(
                CloneStrokeList(state.LatestStrokes),
                state.RenderWidth,
                state.RenderHeight,
                transparentBackground: true);

            return ComposePngLayers(background, overlayBytes);
        }

        private void TryExportAnnotatedPptx(PowerPoint.Presentation? sourcePresentation)
        {
            if (sourcePresentation == null)
                return;

            string? safeName;
            try
            {
                var baseName = Path.GetFileNameWithoutExtension(sourcePresentation.Name ?? "presentation");
                safeName = Sanitize(baseName);
                if (string.IsNullOrWhiteSpace(safeName))
                    safeName = "presentation";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] Unable to inspect source presentation for annotated PPTX export: {ex}");
                return;
            }

            // Canonical live ink source for PPTX bake is sourceSlide (PowerPoint slide index).
            var liveInkBySourceSlide = _inkStateRepository
                .GetAllSlidesSnapshot()
                .Where(kvp => kvp.Key > 0 && kvp.Value != null && kvp.Value.Count > 0)
                .ToDictionary(kvp => kvp.Key, kvp => CloneStrokeList(kvp.Value));

            var savedSolutions = GetSavedSolutionPagesSnapshot();
            if (liveInkBySourceSlide.Count == 0 && savedSolutions.Count == 0)
            {
                Debug.WriteLine("[SessionManager] Skipping annotated PPTX export because no live ink artifacts or saved solution pages were found.");
                return;
            }

            var exportDirectory = Path.Combine(Path.GetTempPath(), "autoslide_exports");
            Directory.CreateDirectory(exportDirectory);

            var tempSourceCopyPath = Path.Combine(
                Path.GetTempPath(),
                Guid.NewGuid().ToString("N") + "_annot_source_" + safeName + ".pptx");

            var exportPath = Path.Combine(
                exportDirectory,
                safeName + "_annotated_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N") + ".pptx");

            PowerPoint.Presentation? workingPresentation = null;
            var stagedPngPaths = new List<string>();

            try
            {
                sourcePresentation.SaveCopyAs(tempSourceCopyPath);

                workingPresentation = _app.Presentations.Open(
                    tempSourceCopyPath,
                    ReadOnly: MsoTriState.msoFalse,
                    Untitled: MsoTriState.msoTrue,
                    WithWindow: MsoTriState.msoFalse);

                Debug.WriteLine("[SessionManager] Annotated PPTX export opened temp presentation on COM STA thread.");

                var slideWidth = workingPresentation.PageSetup?.SlideWidth ?? 0f;
                var slideHeight = workingPresentation.PageSetup?.SlideHeight ?? 0f;

                if (slideWidth <= 0f || slideHeight <= 0f)
                {
                    slideWidth = 960f;
                    slideHeight = 540f;
                }

                int initialSlideCount = workingPresentation.Slides.Count;

                int renderWidth = Math.Max(1, (int)Math.Round(slideWidth * 2f));
                int renderHeight = Math.Max(1, (int)Math.Round(slideHeight * 2f));

                foreach (var kvp in liveInkBySourceSlide.OrderBy(k => k.Key))
                {
                    int sourceSlide = kvp.Key;
                    int exportFrame = ResolveRepresentativeExportFrameForSourceSlide(sourceSlide);
                    int resolvedSlideFromExportFrame = ResolveSourceSlideFromExportFrame(exportFrame);
                    int targetPowerPointSlide = sourceSlide;

                    if (targetPowerPointSlide <= 0 || targetPowerPointSlide > workingPresentation.Slides.Count)
                    {
                        Debug.WriteLine(
                            $"[SessionManager] AnnotatedPptx live-ink skip sourceSlide={sourceSlide} exportFrame={exportFrame} resolvedSlide={resolvedSlideFromExportFrame} targetSlide={targetPowerPointSlide} reason=target-out-of-range");
                        continue;
                    }

                    var overlayPng = _inkOverlay.GeneratePngFromVectorData(
                        CloneStrokeList(kvp.Value),
                        renderWidth,
                        renderHeight,
                        transparentBackground: true);

                    if (overlayPng == null || overlayPng.Length == 0)
                    {
                        Debug.WriteLine(
                            $"[SessionManager] AnnotatedPptx live-ink skip sourceSlide={sourceSlide} exportFrame={exportFrame} resolvedSlide={resolvedSlideFromExportFrame} targetSlide={targetPowerPointSlide} reason=empty-overlay");
                        continue;
                    }

                    var overlayPath = Path.Combine(Path.GetTempPath(), "autoslide_live_ink_" + Guid.NewGuid().ToString("N") + ".png");
                    File.WriteAllBytes(overlayPath, overlayPng);
                    stagedPngPaths.Add(overlayPath);

                    bool inserted = false;
                    PowerPoint.Slide? targetSlide = null;
                    PowerPoint.Shape? shape = null;
                    try
                    {
                        targetSlide = workingPresentation.Slides[targetPowerPointSlide];
                        shape = targetSlide.Shapes.AddPicture(
                            overlayPath,
                            MsoTriState.msoFalse,
                            MsoTriState.msoTrue,
                            0f,
                            0f,
                            slideWidth,
                            slideHeight);
                        inserted = shape != null;
                    }
                    finally
                    {
                        Debug.WriteLine(
                            $"[SessionManager] AnnotatedPptx live-ink bake sourceSlide={sourceSlide} exportFrame={exportFrame} resolvedSlide={resolvedSlideFromExportFrame} targetSlide={targetPowerPointSlide} inserted={inserted}");

                        if (shape != null)
                            Marshal.ReleaseComObject(shape);
                        if (targetSlide != null)
                            Marshal.ReleaseComObject(targetSlide);
                    }
                }

                int appendedSolutions = 0;
                foreach (var solution in savedSolutions)
                {
                    var solutionArtifact = ComposeSolutionArtifact(solution);
                    if (solutionArtifact == null || solutionArtifact.Length == 0)
                        continue;

                    int sourceSlide = solution.SourceSlideIndex ?? -1;
                    int exportFrame = sourceSlide > 0 ? ResolveRepresentativeExportFrameForSourceSlide(sourceSlide) : -1;
                    int resolvedSlideFromExportFrame = exportFrame > 0 ? ResolveSourceSlideFromExportFrame(exportFrame) : -1;

                    var solutionPath = Path.Combine(Path.GetTempPath(), "autoslide_solution_slide_" + Guid.NewGuid().ToString("N") + ".png");
                    File.WriteAllBytes(solutionPath, solutionArtifact);
                    stagedPngPaths.Add(solutionPath);

                    bool inserted = false;
                    int appendedSlideIndex = -1;
                    PowerPoint.Slide? newSlide = null;
                    PowerPoint.Shape? shape = null;
                    try
                    {
                        appendedSlideIndex = workingPresentation.Slides.Count + 1;
                        newSlide = workingPresentation.Slides.Add(
                            appendedSlideIndex,
                            PowerPoint.PpSlideLayout.ppLayoutBlank);

                        shape = newSlide.Shapes.AddPicture(
                            solutionPath,
                            MsoTriState.msoFalse,
                            MsoTriState.msoTrue,
                            0f,
                            0f,
                            slideWidth,
                            slideHeight);
                        inserted = shape != null;
                        if (inserted)
                            appendedSolutions++;
                    }
                    finally
                    {
                        Debug.WriteLine(
                            $"[SessionManager] AnnotatedPptx solution-append kind={solution.Kind} sourceSlide={sourceSlide} exportFrame={exportFrame} resolvedSlide={resolvedSlideFromExportFrame} targetSlide={appendedSlideIndex} inserted={inserted}");

                        if (shape != null)
                            Marshal.ReleaseComObject(shape);
                        if (newSlide != null)
                            Marshal.ReleaseComObject(newSlide);
                    }
                }

                int finalSlideCount = workingPresentation.Slides.Count;
                Debug.WriteLine(
                    $"[SessionManager] AnnotatedPptx append-summary requestedSolutions={savedSolutions.Count} insertedSolutions={appendedSolutions} initialSlideCount={initialSlideCount} finalSlideCount={finalSlideCount}");

                workingPresentation.SaveAs(
                    exportPath,
                    PowerPoint.PpSaveAsFileType.ppSaveAsOpenXMLPresentation,
                    MsoTriState.msoFalse);

                Debug.WriteLine(
                    $"[SessionManager] AnnotatedPptx local export completed path={exportPath} inkSlides={liveInkBySourceSlide.Count} solutionsSaved={savedSolutions.Count}");

                if (string.IsNullOrWhiteSpace(_activePresentationId) || string.IsNullOrWhiteSpace(_presenterAuthToken))
                    throw new InvalidOperationException("Annotated PPTX local export succeeded, but session upload credentials are unavailable.");

                Debug.WriteLine(
                    $"[SessionManager] AnnotatedPptx upload start session={_activePresentationId} file={exportPath}");

                var uploadResponse = _apiGateway
                    .UploadAnnotatedPptxAsync(
                        _backendBaseUrl,
                        _activePresentationId,
                        _presenterAuthToken,
                        File.ReadAllBytes(exportPath))
                    .GetAwaiter()
                    .GetResult();

                if (uploadResponse == null || !uploadResponse.Success || string.IsNullOrWhiteSpace(uploadResponse.StoragePath))
                {
                    throw new InvalidOperationException(
                        "Annotated PPTX local export succeeded, but backend upload/persistence failed.");
                }

                Debug.WriteLine(
                    $"[SessionManager] AnnotatedPptx upload success session={_activePresentationId} storagePath={uploadResponse.StoragePath}");

                Debug.WriteLine(
                    $"[SessionManager] Exported annotated PPTX: {exportPath} (inkSlides={liveInkBySourceSlide.Count}, solutionsSaved={savedSolutions.Count}, insertedSolutions={appendedSolutions}, finalSlides={finalSlideCount})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] Annotated PPTX export failed: {ex}");
                try
                {
                    MessageBox.Show(
                        "Annotated PPTX export/upload failed: " + ex.Message,
                        "Annotated PPTX",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch { }
            }
            finally
            {
                if (workingPresentation != null)
                {
                    try { workingPresentation.Close(); } catch { }
                    try { Marshal.ReleaseComObject(workingPresentation); } catch { }
                }

                foreach (var path in stagedPngPaths)
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch { }
                }

                try
                {
                    if (File.Exists(tempSourceCopyPath))
                        File.Delete(tempSourceCopyPath);
                }
                catch { }
            }
        }

        private int ResolveRepresentativeExportFrameForSourceSlide(int sourceSlide)
        {
            if (sourceSlide <= 0)
                return -1;

            var exportFrames = GetExportFramesForSlide(sourceSlide);
            if (exportFrames.Count > 0)
                return exportFrames[0];

            return sourceSlide;
        }

        private int ResolveSourceSlideFromExportFrame(int exportFrame)
        {
            if (exportFrame <= 0)
                return -1;

            lock (_reducerLock)
            {
                var descriptor = (_frameDescriptors ?? new List<FrameDescriptor>())
                    .Where(fd => !fd.IsBoundary && fd.ExportFrameIndex.HasValue && fd.ExportFrameIndex.Value == exportFrame)
                    .OrderBy(fd => fd.FrameIndex)
                    .FirstOrDefault();

                if (descriptor != null && descriptor.OriginalSlideIndex > 0)
                    return descriptor.OriginalSlideIndex;

                if (_deckIndex != null)
                {
                    var range = _deckIndex.SlideRanges
                        .FirstOrDefault(r => exportFrame >= r.StartFrameIndex && exportFrame <= r.EndFrameIndex);
                    if (range != null && range.SlideIndex > 0)
                        return range.SlideIndex;
                }
            }

            return exportFrame;
        }

        private static byte[] ComposePngLayers(byte[]? backgroundPng, byte[]? overlayPng)
        {
            if (backgroundPng == null || backgroundPng.Length == 0)
                return overlayPng ?? Array.Empty<byte>();
            if (overlayPng == null || overlayPng.Length == 0)
                return backgroundPng ?? Array.Empty<byte>();

            using var bgStream = new MemoryStream(backgroundPng);
            using var overlayStream = new MemoryStream(overlayPng);
            using var backgroundSource = new Bitmap(bgStream);
            using var overlaySource = new Bitmap(overlayStream);
            using var background = new Bitmap(backgroundSource.Width, backgroundSource.Height, PixelFormat.Format32bppArgb);
            using var overlay = new Bitmap(overlaySource.Width, overlaySource.Height, PixelFormat.Format32bppArgb);
            using (var bgGraphics = Graphics.FromImage(background))
            {
                bgGraphics.DrawImage(backgroundSource, new Rectangle(0, 0, background.Width, background.Height));
            }
            using (var overlayGraphics = Graphics.FromImage(overlay))
            {
                overlayGraphics.DrawImage(overlaySource, new Rectangle(0, 0, overlay.Width, overlay.Height));
            }
            using var graphics = Graphics.FromImage(background);

            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(overlay, new Rectangle(0, 0, background.Width, background.Height));

            using var output = new MemoryStream();
            background.Save(output, ImageFormat.Png);
            return output.ToArray();
        }

        private static byte[]? CreateSolidBackgroundPng(int width, int height, Color color)
        {
            if (width <= 0 || height <= 0)
                return null;

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(color);
            }

            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }

        private static (int Width, int Height) GetPngDimensions(byte[] pngBytes)
        {
            if (pngBytes == null || pngBytes.Length == 0)
                return (0, 0);

            using var ms = new MemoryStream(pngBytes);
            using var image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            return (image.Width, image.Height);
        }

        private static List<InkStrokeData> CloneStrokeList(List<InkStrokeData>? strokes)
        {
            if (strokes == null || strokes.Count == 0)
                return new List<InkStrokeData>();

            var copy = new List<InkStrokeData>(strokes.Count);
            foreach (var stroke in strokes)
            {
                if (stroke == null)
                    continue;

                var clone = new InkStrokeData
                {
                    StrokeId = stroke.StrokeId,
                    Color = stroke.Color,
                    Width = stroke.Width,
                    Opacity = stroke.Opacity,
                    Timestamp = stroke.Timestamp,
                    Points = stroke.Points != null
                        ? stroke.Points
                            .Where(p => p != null && p.Length >= 2)
                            .Select(p => new[] { p[0], p[1] })
                            .ToList()
                        : new List<double[]>()
                };

                copy.Add(clone);
            }

            return copy;
        }

        private static SolutionPageState CloneSolutionState(SolutionPageState source)
        {
            return new SolutionPageState
            {
                SessionId = source.SessionId,
                SolutionPageId = source.SolutionPageId,
                Kind = source.Kind,
                SourceSlideIndex = source.SourceSlideIndex,
                OrderIndex = source.OrderIndex,
                CreatedAtUtc = source.CreatedAtUtc,
                UpdatedAtUtc = source.UpdatedAtUtc,
                RenderWidth = source.RenderWidth,
                RenderHeight = source.RenderHeight,
                BackgroundImageBytes = source.BackgroundImageBytes != null
                    ? source.BackgroundImageBytes.ToArray()
                    : Array.Empty<byte>(),
                LatestStrokes = CloneStrokeList(source.LatestStrokes)
            };
        }

        private void DeactivateSolutionContext()
        {
            bool hadActiveDraft;
            string? activeSolutionId;
            lock (_solutionLock)
            {
                if (_solutionDraftState == SolutionDraftLifecycleState.SavingSolution)
                    return;

                hadActiveDraft = IsDraftState(_solutionDraftState) || !string.IsNullOrEmpty(_activeSolutionPageId);
                activeSolutionId = _activeSolutionPageId;
            }

            if (!hadActiveDraft)
                return;

            RestoreLiveOverlaySnapshotAfterDraft();

            lock (_solutionLock)
            {
                if (activeSolutionId != null)
                    _solutionPages.Remove(activeSolutionId);

                _activeSolutionPageId = null;
                _solutionDraftState = SolutionDraftLifecycleState.NormalSlideInk;
            }

            ClearLiveOverlayDraftSnapshot();
            ApplySolutionDraftUiState();
        }

        private void ResetSolutionState()
        {
            lock (_solutionLock)
            {
                _solutionPages.Clear();
                _savedSolutionPages.Clear();
                _activeSolutionPageId = null;
                _solutionDraftState = SolutionDraftLifecycleState.NormalSlideInk;
                _overlayLiveSnapshotBeforeDraft = new List<InkStrokeData>();
                _overlayLiveSnapshotSlideIndex = -1;
                _overlayLiveSnapshotFrameIndex = -1;
                _nextSolutionOrder = 1;
            }

            ApplySolutionDraftUiState();
        }

        private void RebuildDeckIndex()
        {
            _deckIndex = _deckIndexBuilder.Build(
                (_frameDescriptors ?? new List<FrameDescriptor>())
                    .Select(fd => new DeckFrameDescriptor
                    {
                        FrameIndex = fd.FrameIndex,
                        ExportFrameIndex = fd.ExportFrameIndex,
                        OriginalSlideIndex = fd.OriginalSlideIndex,
                        ClickIndex = fd.ClickIndex,
                        IsBoundary = fd.IsBoundary,
                        BoundaryAfterSlideIndex = fd.BoundaryAfterSlideIndex
                    }),
                _slideAnimationMap);
        }

        private void UploadInkSnapshotForSlide(int slideIndex, List<InkStrokeData> strokes)
        {
            var pid = _activePresentationId;
            var tok = _presenterAuthToken;

            if (string.IsNullOrEmpty(pid) || string.IsNullOrEmpty(tok) || slideIndex <= 0)
                return;

            var sessionId = pid!;
            var presenterToken = tok!;

            var snapshot = strokes != null
                ? new List<InkStrokeData>(strokes)
                : new List<InkStrokeData>();

            _ = Task.Run(async () =>
            {
                try
                {
                    // Canonical per-slide artifacts for annotated PDF baking must stay transparent.
                    byte[]? inkBytes = _inkOverlay.RenderInkArtifactSnapshot(
                        snapshot,
                        transparentBackground: true);
                    if (inkBytes == null || inkBytes.Length == 0)
                    {
                        await DeleteInkArtifactAsync(sessionId, presenterToken, slideIndex).ConfigureAwait(false);
                        Debug.WriteLine(
                            $"[SessionManager] Removed ink artifact snapshot for sourceSlide={slideIndex} because no ink remains");
                        return;
                    }

                    await UploadInkArtifactAsync(sessionId, presenterToken, slideIndex, inkBytes).ConfigureAwait(false);
                    Debug.WriteLine(
                        $"[SessionManager] Uploaded ink artifact snapshot bytes={inkBytes.Length} sourceSlide={slideIndex} strokes={snapshot.Count}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionManager] Ink snapshot upload error (slide={slideIndex}): {ex}");
                }
            });
        }

        private void ResetRuntimeState()
        {
            _sessionState?.Reset();
            _inkOverlay.FrameStore.Clear();
            _recentInkStateRequests.Clear();
            _activePresentationIdentity = null;
            _activePresentationId = null;
            _activeSignalRGroupId = null;
            _presenterAuthToken = null;
            _deckIndex = null;
            _slideAnimationMap = null;
            _frameDescriptors.Clear();
            _currentSlideIndex = -1;
            _currentAbsoluteFrame = -1;
            _lastEmittedClickBySlide.Clear();

            lock (_inkRoutingLock)
            {
                _inkRouteSlideHint = -1;
                _inkRouteClickHint = 0;
            }

            ResetSolutionState();
        }

        private void InvalidateSessionLifecycle()
        {
            Interlocked.Increment(ref _sessionLifecycleVersion);
        }

        private bool TryGetCurrentSlideShowWindow(out PowerPoint.SlideShowWindow? slideShowWindow)
        {
            slideShowWindow = null;

            try
            {
                if (_app.SlideShowWindows != null && _app.SlideShowWindows.Count > 0)
                    slideShowWindow = _app.SlideShowWindows[1];
            }
            catch
            {
                slideShowWindow = null;
            }

            return slideShowWindow != null;
        }

        private void RebindLiveSlideShowIfRunning()
        {
            var context = _addInContext;
            if (context != null && SynchronizationContext.Current != context)
            {
                context.Send(_ => RebindLiveSlideShowIfRunning(), null);
                return;
            }

            if (!HasActiveSession || _slideShowHandler == null)
                return;

            if (!TryGetCurrentSlideShowWindow(out var liveWindow))
                return;

            try
            {
                Debug.WriteLine("[SessionManager] Rebinding live slideshow overlay for active session.");
                _slideShowHandler.StartLiveSlideShow(liveWindow);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] Live slideshow rebind error: {ex}");
            }
        }

        internal void InitializeCurrentSlideOverlay(PowerPoint.SlideShowWindow Wn)
        {
            if (Wn == null || !HasActiveSession)
                return;

            try
            {
                if (!IsPresentationActive(Wn.Presentation.FullName))
                    return;

                int slideIndex = 1;
                try
                {
                    if (Wn.View != null && Wn.View.Slide != null)
                        slideIndex = Wn.View.Slide.SlideIndex;
                }
                catch { }

                if (slideIndex <= 0)
                    slideIndex = 1;

                CurrentSlideIndex = slideIndex;

                var currentStrokes = _inkStateRepository.GetStrokesForSlide(slideIndex);
                _inkOverlay.LoadStrokesFromData(currentStrokes);

                Debug.WriteLine(
                    $"[SessionManager] Current slide initialized: slide={slideIndex}, strokes={currentStrokes.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] InitializeCurrentSlideOverlay error: {ex}");
            }
        }

        internal void UpdateInkRoutingHint(int slideIndex, int clickIndex)
        {
            if (slideIndex <= 0)
                return;

            if (clickIndex < 0)
                clickIndex = 0;

            lock (_inkRoutingLock)
            {
                _inkRouteSlideHint = slideIndex;
                _inkRouteClickHint = clickIndex;
            }
        }

        private void GetInkRoutingHint(out int slideIndex, out int clickIndex)
        {
            lock (_inkRoutingLock)
            {
                slideIndex = _inkRouteSlideHint;
                clickIndex = _inkRouteClickHint;
            }
        }

        private int ResolveImmediateExportFrameForInk(int slideIndex, int clickIndex, int fallbackFrame)
        {
            int fallback = fallbackFrame > 0 ? fallbackFrame : 1;
            if (slideIndex <= 0)
                return fallback;

            lock (_reducerLock)
            {
                if (_deckIndex == null)
                    RebuildDeckIndex();

                if (_deckIndex != null && _deckIndex.HasAnyFrameData)
                    return _deckIndex.ResolveExportFrameForSlideClick(slideIndex, clickIndex);
            }

            return fallback;
        }

        private SessionReductionResult ReduceEvent(SessionEvent sessionEvent)
        {
            lock (_reducerLock)
            {
                if (_sessionState == null)
                    _sessionState = new SessionState(_inkStateRepository);

                if (_deckIndex == null)
                    RebuildDeckIndex();

                var result = _sessionReducer.Reduce(_sessionState, _deckIndex, sessionEvent);
                if (result.FrameDecision != null)
                {
                    var d = result.FrameDecision;
                    Debug.WriteLine(
                        $"[SessionReducer] slide={d.SlideIndex} reqClick={d.RequestedClickIndex} resolvedClick={d.ResolvedClickIndex} content={d.ContentFrameIndex} export={d.ExportFrameIndex} prev={d.PreviousAbsoluteFrame} backfill={d.IsBackfill} backward={d.IsBackwardNavigation}");
                }

                return result;
            }
        }

        private void SyncLegacyCursorFromSessionState()
        {
            if (_sessionState == null)
                return;

            _currentSlideIndex = _sessionState.PresenterCursor.CurrentSlideIndex;
            _currentAbsoluteFrame = _sessionState.PresenterCursor.CurrentAbsoluteFrame;

            _lastEmittedClickBySlide.Clear();
            foreach (var kvp in _sessionState.PresenterCursor.LastEmittedClickBySlide)
                _lastEmittedClickBySlide[kvp.Key] = kvp.Value;

            if (_currentSlideIndex > 0)
            {
                _lastEmittedClickBySlide.TryGetValue(_currentSlideIndex, out var clickHint);
                UpdateInkRoutingHint(_currentSlideIndex, clickHint);
            }
        }

        private void TrackNetworkTask(Task task)
        {
            if (task == null)
                return;

            lock (_taskLock)
                _inFlightAdvanceTasks.Add(task);

            task.ContinueWith(_ =>
            {
                lock (_taskLock)
                    _inFlightAdvanceTasks.Remove(task);
            });
        }

        private async Task ExecuteNetworkActionsAsync(IEnumerable<SessionNetworkAction> actions)
        {
            var presentationId = _activePresentationId;
            var presenterToken = _presenterAuthToken;

            if (actions == null || string.IsNullOrEmpty(presentationId) || string.IsNullOrEmpty(presenterToken))
                return;

            var nonNullPresentationId = presentationId!;

            foreach (var action in actions)
            {
                switch (action)
                {
                    case UnlockFrameAction unlock:
                    {
                        var task = UpdateSlideAsync(nonNullPresentationId, presenterToken, unlock.FrameIndex);
                        TrackNetworkTask(task);
                        await task.ConfigureAwait(false);
                        break;
                    }
                    case AdvanceFrameAction advance:
                    {
                        if (advance.DelayForBackfill)
                            await Task.Delay(150).ConfigureAwait(false);

                        var task = AdvanceSlideOnServer(nonNullPresentationId, presenterToken, advance.FrameIndex);
                        TrackNetworkTask(task);
                        await task.ConfigureAwait(false);
                        break;
                    }
                    case EndSessionAction _:
                    {
                        await EndSessionAsync(nonNullPresentationId, presenterToken).ConfigureAwait(false);
                        break;
                    }
                }
            }
        }

        private async Task<Tuple<string, string, string, string>?> UploadPresentationAndRegisterAsync(
            string? pptPath,
            string presentationName,
            int totalSlides,
            string? courseId = null,
            string? flatPdfPath = null)
        {
            if (string.IsNullOrWhiteSpace(pptPath))
                return null;

            var response = await _apiGateway.UploadPresentationAndRegisterAsync(new UploadPresentationRequest
            {
                BackendBaseUrl = _backendBaseUrl,
                PresentationPath = pptPath,
                PresentationName = presentationName,
                TotalSlides = totalSlides,
                CourseId = courseId,
                FlatPdfPath = flatPdfPath,
                SlideAnimationMap = _slideAnimationMap,
                FrameDescriptors = BuildFrameDescriptorPayload()
                    .Select(fd => new DeckFrameDescriptor
                    {
                        FrameIndex = fd.FrameIndex,
                        ExportFrameIndex = fd.ExportFrameIndex,
                        OriginalSlideIndex = fd.OriginalSlideIndex,
                        ClickIndex = fd.ClickIndex,
                        IsBoundary = fd.IsBoundary,
                        BoundaryAfterSlideIndex = fd.BoundaryAfterSlideIndex
                    })
                    .ToList(),
                AuthToken = _authToken
            }).ConfigureAwait(false);

            if (response == null)
                return null;

            return Tuple.Create(
                response.PresentationId,
                response.PresenterToken,
                response.ViewerUrl,
                response.SessionId);
        }

        private async Task StartSessionOnServerAsync(string presentationId, string? token)
        {
            try
            {
                await _apiGateway.StartSessionAsync(_backendBaseUrl, presentationId, token).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(_activeSignalRGroupId))
                    await _signalRAdapter.ConnectAsync(_activeSignalRGroupId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] StartSession error: {ex}");
            }
        }

        private async Task UpdateSlideAsync(string presentationId, string? token, int slideIndex)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await _apiGateway.UnlockSlideAsync(_backendBaseUrl, presentationId, token, slideIndex).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (attempt == 0)
                {
                    Debug.WriteLine($"[SessionManager] UpdateSlide retry for frame {slideIndex}: {ex.Message}");
                    await Task.Delay(200).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionManager] UpdateSlide error frame {slideIndex}: {ex.Message}");
                    return;
                }
            }
        }

        private Task AdvanceSlideOnServer(string presentationId, string? token, int slideIndex)
        {
            return _apiGateway.AdvanceSlideAsync(_backendBaseUrl, presentationId, token, slideIndex);
        }

        private async Task EndSessionAsync(string presentationId, string? token)
        {
            if (string.IsNullOrEmpty(presentationId))
                return;

            if (Interlocked.CompareExchange(ref _sessionEndLock, 1, 0) != 0)
                return;

            try
            {
                Task[] pending;
                lock (_taskLock) { pending = _inFlightAdvanceTasks.ToArray(); }

                if (pending.Length > 0)
                {
                    try { await Task.WhenAll(pending).ConfigureAwait(false); }
                    catch (Exception ex) { Debug.WriteLine($"[SessionManager] Drain error: {ex.Message}"); }
                }

                await _signalRAdapter.DisconnectAsync().ConfigureAwait(false);
                await _apiGateway.EndSessionAsync(_backendBaseUrl, presentationId, token, _authToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] EndSession error: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _sessionEndLock, 0);
            }
        }

        private Task UploadInkAsync(string presentationId, string? token, int slideIndex, byte[] inkPng)
        {
            return _apiGateway.UploadInkAsync(_backendBaseUrl, presentationId, token, slideIndex, inkPng);
        }

        private Task UploadInkArtifactAsync(string sessionId, string? token, int slideIndex, byte[] inkPng)
        {
            return _apiGateway.UploadInkArtifactAsync(_backendBaseUrl, sessionId, token, slideIndex, inkPng);
        }

        private Task DeleteInkArtifactAsync(string sessionId, string? token, int slideIndex)
        {
            return _apiGateway.DeleteInkArtifactAsync(_backendBaseUrl, sessionId, token, slideIndex);
        }

        private Task SendTranscriptAsync(string transcribedText)
        {
            if (string.IsNullOrEmpty(_activePresentationId))
                return Task.CompletedTask;

            return _apiGateway.SendTranscriptAsync(
                _backendBaseUrl,
                _activePresentationId,
                _presenterAuthToken,
                transcribedText);
        }

        private List<int> GetContentFramesForSlide(int slideIndex)
        {
            return _deckIndex?.GetContentFramesForSlide(slideIndex) ?? new List<int> { slideIndex };
        }

        private List<int> GetExportFramesForSlide(int slideIndex)
        {
            return _deckIndex?.GetExportFramesForSlide(slideIndex) ?? new List<int> { slideIndex };
        }

        private void PropagateSlideInkChange(int slideIndex, List<InkStrokeData> updatedStrokes, int broadcastFrameIndex, bool broadcastToGroup)
        {
            var safeStrokes = updatedStrokes != null
                ? new List<InkStrokeData>(updatedStrokes)
                : new List<InkStrokeData>();

            _inkStateRepository.ReplaceSlideStrokes(slideIndex, safeStrokes);

            var mappedFrames = GetExportFramesForSlide(slideIndex);
            foreach (var frameIndex in mappedFrames)
                _inkStateRepository.SetFrameStrokes(frameIndex, safeStrokes);

            if (!broadcastToGroup || string.IsNullOrEmpty(_activeSignalRGroupId))
                return;

            int frameToBroadcast = broadcastFrameIndex > 0
                ? broadcastFrameIndex
                : (_currentAbsoluteFrame > 0 ? _currentAbsoluteFrame : slideIndex);

            var groupId = _activeSignalRGroupId;

            if (safeStrokes.Count == 0 && mappedFrames.Count > 0)
            {
                var framesToClear = new List<int>(mappedFrames);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        foreach (var frameIndex in framesToClear)
                            await _signalRAdapter.BroadcastFullInkStateAsync(groupId, frameIndex, new List<InkStrokeData>()).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SessionManager] Broadcast clear-to-all-frames failed: {ex.Message}");
                    }
                });
                return;
            }

            BroadcastFullStateToNonActiveFrames(slideIndex, frameToBroadcast, safeStrokes);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _signalRAdapter.BroadcastFullInkStateAsync(groupId, frameToBroadcast, safeStrokes).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionManager] Broadcast updated ink state failed: {ex.Message}");
                }
            });
        }

        private void BroadcastFullStateToNonActiveFrames(int slideIndex, int activeFrame, List<InkStrokeData> strokes)
        {
            if (string.IsNullOrEmpty(_activeSignalRGroupId))
                return;

            var mappedFrames = GetExportFramesForSlide(slideIndex);
            if (mappedFrames.Count <= 1)
                return;

            var nonActiveFrames = mappedFrames.Where(f => f != activeFrame).ToList();
            if (nonActiveFrames.Count == 0)
                return;

            var groupId = _activeSignalRGroupId;
            var snapshot = strokes != null ? new List<InkStrokeData>(strokes) : new List<InkStrokeData>();

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var frameIndex in nonActiveFrames)
                        await _signalRAdapter.BroadcastFullInkStateAsync(groupId, frameIndex, snapshot).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionManager] Sync non-active frames failed: {ex.Message}");
                }
            });
        }

        private void PresentViewerLink(string url)
        {
            try
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        Bitmap? qrBitmap = null;
                        try { qrBitmap = GenerateQrCode(url, 8); } catch { }

                        var form = new Form
                        {
                            Text = "Share Presentation",
                            Size = new Size(380, 520),
                            StartPosition = FormStartPosition.CenterScreen,
                            TopMost = true
                        };

                        Control picControl = qrBitmap != null
                            ? (Control)new PictureBox
                            {
                                Image = qrBitmap,
                                SizeMode = PictureBoxSizeMode.Zoom,
                                Location = new Point(20, 20),
                                Size = new Size(340, 340)
                            }
                            : new Label
                            {
                                Text = "QR unavailable",
                                TextAlign = ContentAlignment.MiddleCenter,
                                Location = new Point(20, 20),
                                Size = new Size(340, 340)
                            };

                        var linkLabel = new LinkLabel
                        {
                            Text = url,
                            Location = new Point(20, 370),
                            Width = 340,
                            Height = 40,
                            AutoEllipsis = true
                        };

                        linkLabel.LinkClicked += (s, ev) =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show("Cannot open link: " + ex.Message);
                            }
                        };

                        var statusLabel = new Label
                        {
                            Text = "",
                            Location = new Point(20, 420),
                            Width = 340,
                            Height = 30,
                            ForeColor = Color.Green
                        };

                        form.Controls.Add(picControl);
                        form.Controls.Add(linkLabel);
                        form.Controls.Add(statusLabel);

                        try
                        {
                            Clipboard.SetText(url);
                            statusLabel.Text = "Link copied to clipboard.";
                        }
                        catch
                        {
                            statusLabel.Text = "Auto-copy unavailable.";
                            statusLabel.ForeColor = Color.Orange;
                        }

                        System.Windows.Forms.Application.Run(form);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SessionManager] PresentViewerLink error: {ex}");
                    }
                });

                t.SetApartmentState(ApartmentState.STA);
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionManager] PresentViewerLink scheduling error: {ex}");
            }
        }

        private Bitmap GenerateQrCode(string content, int pixelsPerModule)
        {
            var generator = new QRCodeGenerator();
            var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            var code = new QRCode(data);
            return code.GetGraphic(pixelsPerModule);
        }

        private string BuildViewerUrl(string backendViewerUrl)
        {
            return ViewerUrlBuilder.Build(
                _frontendBaseUrl,
                _activePresentationId,
                _activeSignalRGroupId,
                backendViewerUrl);
        }

         private Dictionary<int, List<int>> FlattenAnimations(PowerPoint.Presentation pres)
        {
            var map = new Dictionary<int, List<int>>();
            int absoluteFrame = 1; // 1-based to match PowerPoint slide indices

            // Snapshot original slide count; we'll iterate by index and insert after.
            // Process in reverse so insertions don't shift indices of unprocessed slides.
            int originalCount = pres.Slides.Count;

            // First pass: collect per-slide animation metadata on the ORIGINAL slides.
            // Store as list of (originalIndex, clickShapeGroups) before any mutations.
            var slideInfos = new List<Tuple<int, List<List<string>>>>(); // (origIdx, list-of-click-groups where each group = shape names)
            var labelToKeys = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var allCapturedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int capturedInstanceCount = 0;
            var canonicalFrameDescriptors = new List<FrameDescriptor>();
            int canonicalFrameIndex = 1;

            for (int i = 1; i <= originalCount; i++)
            {
                PowerPoint.Slide slide = pres.Slides[i];
                var clickGroups = new List<List<string>>(); // one list of shape names per click

                try
                {
                    var seq = slide.TimeLine.MainSequence;
                    // Group effects by click number (paragraph-level sub-effects share a click).
                    var clickBuckets = new SortedDictionary<int, List<string>>();
                    int currentClick = 0;

                    Debug.WriteLine($"[Flatten] ── Slide {i}: {seq.Count} effects ──");

                    for (int e = 1; e <= seq.Count; e++)
                    {
                        var effect = seq[e];

                        string shapeName = "(unknown)";
                        try { shapeName = effect.Shape?.Name ?? "(null)"; } catch { }

                        string triggerName = "(unknown)";
                        try { triggerName = effect.Timing.TriggerType.ToString(); } catch { }

                        bool isExit = false;
                        try { isExit = (effect.Exit == MsoTriState.msoTrue); } catch { }

                        int effectTypeId = -1;
                        string effectTypeName = "(unknown)";
                        try
                        {
                            effectTypeId = (int)effect.EffectType;
                            effectTypeName = effect.EffectType.ToString();
                        }
                        catch { }

                        string category;
                        if (isExit)
                            category = "EXIT";
                        else if (effectTypeId >= 1 && effectTypeId <= 28)
                            category = "ENTRANCE";
                        else if (effectTypeId >= 29 && effectTypeId <= 54)
                            category = "EMPHASIS";
                        else if (effectTypeId >= 55)
                            category = "MOTION_PATH";
                        else if (effectTypeId == 0)
                            category = "CUSTOM(entrance?)";
                        else
                            category = "UNKNOWN";

                        // Only process entrance animations
                        if (effect.Exit != MsoTriState.msoFalse)
                        {
                            Debug.WriteLine($"[Flatten]   #{e} DROPPED(exit) shape=\"{shapeName}\" type={effectTypeName}({effectTypeId}) cat={category} trigger={triggerName}");
                            continue;
                        }
                        var cat = PowerPoint.MsoAnimEffect.msoAnimEffectCustom;
                        try { cat = effect.EffectType; } catch { }

                        // Determine click index: OnPageClick triggers increment a click step
                        if (effect.Timing.TriggerType == PowerPoint.MsoAnimTriggerType.msoAnimTriggerOnPageClick)
                            currentClick++;
                        // WithPrevious / AfterPrevious share the same click step

                        if (currentClick < 1)
                        {
                            Debug.WriteLine($"[Flatten]   #{e} DROPPED(auto-start) shape=\"{shapeName}\" type={effectTypeName}({effectTypeId}) cat={category} trigger={triggerName}");
                            continue; // effects on click 0 are auto-start, not click-triggered
                        }

                        PowerPoint.Shape? shape = null;
                        try { shape = effect.Shape; } catch { continue; }
                        if (shape == null) continue;

                        string? sn = null;
                        try { sn = shape.Name; } catch { continue; }
                        if (string.IsNullOrEmpty(sn)) continue;

                        string? shapeKey = EnsureShapeKey(shape);
                        if (string.IsNullOrEmpty(shapeKey))
                        {
                            Debug.WriteLine($"[Flatten]   #{e} DROPPED(no-key) shape=\"{sn}\" type={effectTypeName}({effectTypeId}) cat={category} trigger={triggerName}");
                            continue;
                        }

                        capturedInstanceCount++;
                        allCapturedKeys.Add(shapeKey!);

                        if (!labelToKeys.TryGetValue(sn, out var keySet))
                        {
                            keySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            labelToKeys[sn] = keySet;
                        }
                        keySet.Add(shapeKey!);

                        if (!clickBuckets.ContainsKey(currentClick))
                            clickBuckets[currentClick] = new List<string>();

                        bool isDuplicate = clickBuckets[currentClick].Contains(shapeKey);
                        if (!isDuplicate)
                            clickBuckets[currentClick].Add(shapeKey);

                        string duplicateTag = isDuplicate ? " (duplicate, skipped)" : "";
                        string miscategoryTag = (category == "EMPHASIS" || category == "MOTION_PATH")
                            ? " ⚠️ TREATED_AS_ENTRANCE"
                            : "";
                        Debug.WriteLine($"[Flatten]   #{e} CAPTURED click={currentClick} key={shapeKey} type={effectTypeName}({effectTypeId}) cat={category} trigger={triggerName}{miscategoryTag}{duplicateTag}");
                    }

                    foreach (var kvp in clickBuckets)
                        clickGroups.Add(kvp.Value);

                    if (clickGroups.Count > 0)
                    {
                        for (int g = 0; g < clickGroups.Count; g++)
                        {
                            Debug.WriteLine($"[Flatten]   Click {g + 1}: [{string.Join(", ", clickGroups[g])}]");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"[Flatten]   (no click-triggered effects)");
                    }
                }
                catch (COMException ex)
                {
                    Debug.WriteLine($"[Flatten] COMException reading animations on slide {i}: {ex.Message}");
                }

                slideInfos.Add(Tuple.Create(i, clickGroups));
            }

            int uniqueLabelCount = labelToKeys.Count;
            int uniqueKeyCount = allCapturedKeys.Count;
            Debug.WriteLine($"[Flatten] ══ SUMMARY: {originalCount} slides, captured instances = {capturedInstanceCount}, unique labels = {uniqueLabelCount}, unique keys = {uniqueKeyCount}, animated slides = {slideInfos.Count(s => s.Item2.Count > 0)} ══");

            foreach (var kvp in labelToKeys.OrderBy(k => k.Key))
            {
                if (kvp.Value.Count > 1)
                {
                    Debug.WriteLine($"[Flatten] WARNING: label \"{kvp.Key}\" maps to {kvp.Value.Count} distinct shape keys");
                }
            }

            // Second pass: duplicate slides and hide shapes.
            // Process forward; track an insertion offset so indices stay correct.
            int offset = 0;
            int boundaryCount = 0;
            foreach (var info in slideInfos)
            {
                int origIdx = info.Item1;
                var clickGroups = info.Item2; // list of groups; count = N clicks
                int N = clickGroups.Count;
                int currentSlideIdx = origIdx + offset; // adjusted index in the mutated deck

                var firstRevealClickByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var groupIndex in Enumerable.Range(0, clickGroups.Count))
                {
                    int clickNumber = groupIndex + 1;
                    foreach (var key in clickGroups[groupIndex])
                    {
                        if (!firstRevealClickByKey.TryGetValue(key, out var existing) || clickNumber < existing)
                            firstRevealClickByKey[key] = clickNumber;
                    }
                }

                var frameIndices = new List<int>();

                if (N == 0)
                {
                    // No click animations — single frame
                    canonicalFrameDescriptors.Add(new FrameDescriptor(
                        canonicalFrameIndex++,
                        absoluteFrame,
                        origIdx,
                        0,
                        false,
                        null));

                    Debug.WriteLine($"[Flatten] FrameMap[{canonicalFrameIndex - 1}] => content slide={origIdx} click=0 exportFrame={absoluteFrame}");
                        frameIndices.Add(canonicalFrameIndex - 1);
                        absoluteFrame++;
                }
                else
                {
                    // We need N+1 frames total (base + one per click).
                    // The original slide becomes the LAST frame (all shapes visible).
                    // We duplicate N times BEFORE it (inserted right after the original's position).

                    // Duplicate the slide N times (clipboard-free).
                    int successfulDups = 0;
                    for (int dup = 0; dup < N; dup++)
                    {
                        try
                        {
                            var duped = pres.Slides[currentSlideIdx].Duplicate();
                            duped.MoveTo(currentSlideIdx + 1 + successfulDups);
                            successfulDups++;
                        }
                        catch (COMException ex)
                        {
                            Debug.WriteLine($"[Flatten] COMException duplicating slide {origIdx} (dup {dup}): {ex.Message}");
                        }
                    }

                    // Now slides [currentSlideIdx .. currentSlideIdx + successfulDups] are successfulDups+1 copies.
                    // Frame 0 (base): hide ALL animated shapes.
                    // Frame k: hide shapes from clicks k+1..successfulDups, show shapes from clicks 1..k.
                    for (int frame = 0; frame <= successfulDups; frame++)
                    {
                        int slidePos = currentSlideIdx + frame;
                        var slide = pres.Slides[slidePos];

                        // Determine which unique shape keys should be HIDDEN on this frame.
                        var hiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kvp in firstRevealClickByKey)
                        {
                            if (kvp.Value > frame)
                                hiddenKeys.Add(kvp.Key);
                        }

                        Debug.WriteLine($"[Flatten]   Frame {frame} (slidePos={slidePos}, exportFrame={absoluteFrame}) hidden keys count = {hiddenKeys.Count}");

                        // Hide shapes by setting Visible = false
                        foreach (var shapeKey in hiddenKeys)
                        {
                            try
                            {
                                var shape = FindShapeByKey(slide, shapeKey);
                                if (shape != null)
                                    shape.Visible = MsoTriState.msoFalse;
                            }
                            catch (COMException) { /* shape may not exist on this copy */ }
                            catch (ArgumentException) { /* shape name not found */ }
                        }

                        // Clear animation sequences on duplicated frames to avoid
                        // leftover animation metadata in the exported PDF.
                        try
                        {
                            var seq = slide.TimeLine.MainSequence;
                            while (seq.Count > 0)
                                seq[1].Delete();
                        }
                        catch (COMException) { }

                        canonicalFrameDescriptors.Add(new FrameDescriptor(
                            canonicalFrameIndex++,
                            absoluteFrame,
                            origIdx,
                            frame,
                            false,
                            null));

                        Debug.WriteLine($"[Flatten] FrameMap[{canonicalFrameIndex - 1}] => content slide={origIdx} click={frame} exportFrame={absoluteFrame}");
                        frameIndices.Add(canonicalFrameIndex - 1);
                        absoluteFrame++;
                    }

                    if (origIdx < originalCount)
                    {
                        canonicalFrameDescriptors.Add(new FrameDescriptor(
                            canonicalFrameIndex++,
                            null,
                            origIdx,
                            null,
                            true,
                            origIdx));

                        Debug.WriteLine($"[Flatten] FrameMap[{canonicalFrameIndex - 1}] => boundary after slide {origIdx}");
                        boundaryCount++;
                    }

                    offset += successfulDups; // only actual exported content slides advance the physical deck
                }

                map[origIdx] = frameIndices;
                Debug.WriteLine($"[Flatten] RuntimeMap slide={origIdx} => [{string.Join(",", frameIndices)}]");
            }

            _frameDescriptors = canonicalFrameDescriptors;

            Debug.WriteLine($"[Flatten] Complete: {map.Count} original slides -> {absoluteFrame - 1} content frames, {boundaryCount} boundary frames, {canonicalFrameDescriptors.Count} canonical frames");
            return map;
        }

        private string? EnsureShapeKey(PowerPoint.Shape? shape)
        {
            if (shape == null) return null;

            try
            {
                var existing = shape.Tags[AutoSlideShapeKeyTag];
                if (!string.IsNullOrWhiteSpace(existing))
                    return existing;
            }
            catch { }

            var key = Guid.NewGuid().ToString("N");
            try { shape.Tags.Add(AutoSlideShapeKeyTag, key); } catch { }
            return key;
        }

        private PowerPoint.Shape? FindShapeByKey(PowerPoint.Slide? slide, string key)
        {
            if (slide == null || string.IsNullOrEmpty(key)) return null;

            try
            {
                for (int s = 1; s <= slide.Shapes.Count; s++)
                {
                    PowerPoint.Shape? shape = null;
                    try { shape = slide.Shapes[s]; } catch { continue; }
                    if (shape == null) continue;

                    string? shapeKey = null;
                    try { shapeKey = shape.Tags[AutoSlideShapeKeyTag]; } catch { }

                    if (!string.IsNullOrEmpty(shapeKey) && string.Equals(shapeKey, key, StringComparison.OrdinalIgnoreCase))
                        return shape;
                }
            }
            catch { }

            return null;
        }

        private List<FrameDescriptorPayload> BuildFrameDescriptorPayload()
        {
            if (_frameDescriptors == null || _frameDescriptors.Count == 0)
                return new List<FrameDescriptorPayload>();

            return _frameDescriptors.Select(fd => new FrameDescriptorPayload
            {
                FrameIndex = fd.FrameIndex,
                ExportFrameIndex = fd.ExportFrameIndex,
                OriginalSlideIndex = fd.OriginalSlideIndex,
                ClickIndex = fd.ClickIndex,
                IsBoundary = fd.IsBoundary,
                BoundaryAfterSlideIndex = fd.BoundaryAfterSlideIndex
            }).ToList();
        }

        private static string? ExtractJson(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return null;

            try
            {
                string pattern = "\\\"" + Regex.Escape(key) + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"";
                var m = Regex.Match(json, pattern);
                if (m.Success)
                    return m.Groups[1].Value;
            }
            catch { }

            return null;
        }

        private static string? Sanitize(string? name)
        {
            if (name == null)
                return null;

            if (name.Length == 0)
                return string.Empty;

            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name;
        }

        private static string ReadFrontendBaseUrl()
        {
            try
            {
                var url = ConfigurationManager.AppSettings["FrontendBaseUrl"];
                if (!string.IsNullOrEmpty(url))
                    return url;
            }
            catch { }

            return "http://localhost:3000";
        }

        public void Dispose()
        {
            StopSharing();

            if (_signalR != null)
                _signalR.InkStateRequested -= OnInkStateRequested;

            if (_inkOverlay != null)
            {
                _inkOverlay.StrokeCompleted -= OnInkStrokeCompleted;
                _inkOverlay.InkStateChanged -= OnInkStateChanged;
                _inkOverlay.InkCleared -= OnInkCleared;
                _inkOverlay.BlankSolutionRequested -= OnBlankSolutionRequestedFromOverlay;
                _inkOverlay.CurrentSlideSolutionRequested -= OnCurrentSlideSolutionRequestedFromOverlay;
                _inkOverlay.SaveSolutionDraftRequested -= OnSaveSolutionDraftRequestedFromOverlay;
                _inkOverlay.DiscardSolutionDraftRequested -= OnDiscardSolutionDraftRequestedFromOverlay;
            }

            if (_speech != null)
            {
                _speech.TranscriptReceived -= OnTranscriptReceived;
                _speech.ErrorOccurred -= OnTranscriptionError;
            }
        }
    }
}
    