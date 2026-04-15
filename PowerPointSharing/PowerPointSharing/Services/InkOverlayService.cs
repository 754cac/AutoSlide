using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace PowerPointSharing
{
    /// <summary>
    /// Owns the WPF Ink Overlay lifecycle, per-slide stroke backup/restore, and ink capture.
    /// </summary>
    public class InkOverlayService
    {
        private enum OverlayLifecycleState
        {
            Detached,
            WaitingForSlideshowWindow,
            AttachedHidden,
            AttachedVisible
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private volatile InkOverlayWindow? _window;
        private Thread? _thread;
        private readonly FrameStore _frameStore = new FrameStore();

        // ---------- foreground-window hook support ----------
        // Delegate instance kept alive by the service so it outlives the
        // overlay window itself (resolves CallbackOnCollectedDelegate race).
        private InkOverlayWindow.WinEventDelegate? _winEventDelegate;
        private IntPtr _winEventHook = IntPtr.Zero;
        private volatile IntPtr _slideshowHwnd = IntPtr.Zero;
        private volatile OverlayLifecycleState _overlayState = OverlayLifecycleState.Detached;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc, InkOverlayWindow.WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
        private const uint GA_ROOT = 2;

        public FrameStore FrameStore => _frameStore;

        /// <summary>Re-raised from InkOverlayWindow when a stroke is completed.</summary>
        public event EventHandler<InkStrokeData>? StrokeCompleted;

        /// <summary>Re-raised from InkOverlayWindow when ink is cleared.</summary>
        public event EventHandler? InkCleared;

        /// <summary>
        /// Re-raised when user erase/clear actions changed the full stroke set.
        /// </summary>
        public event EventHandler? InkStateChanged;

        /// <summary>
        /// Re-raised when overlay toolbar requests creating a blank solution page.
        /// </summary>
        public event EventHandler? BlankSolutionRequested;

        /// <summary>
        /// Re-raised when overlay toolbar requests creating a current-slide solution page.
        /// </summary>
        public event EventHandler? CurrentSlideSolutionRequested;

        /// <summary>
        /// Re-raised when overlay toolbar requests saving the active solution draft.
        /// </summary>
        public event EventHandler? SaveSolutionDraftRequested;

        /// <summary>
        /// Re-raised when overlay toolbar requests discarding the active solution draft.
        /// </summary>
        public event EventHandler? DiscardSolutionDraftRequested;

        /// <summary>
        /// Creates and shows the WPF Ink Overlay on a dedicated STA thread,
        /// then snaps it to the specified slideshow window handle.
        /// </summary>
        public void AttachToWindow(IntPtr hwnd, double slideW, double slideH)
        {
            if (hwnd == IntPtr.Zero)
                return;

            var existing = _window;
            if (existing != null && !existing.Dispatcher.HasShutdownStarted && !existing.Dispatcher.HasShutdownFinished)
            {
                existing._slideshowHwnd = hwnd;
                EnsureForegroundHook(existing, hwnd);

                UpdateOverlayVisibilityForSlideshowState(hwnd, slideW, slideH, "rebind");

                System.Diagnostics.Debug.WriteLine($"[InkOverlayService] Reused overlay window for hwnd={hwnd}.");
                return;
            }

            var ready = new ManualResetEventSlim(false);
            _overlayState = OverlayLifecycleState.WaitingForSlideshowWindow;
            System.Diagnostics.Debug.WriteLine($"[InkOverlayService] Overlay state -> {_overlayState} (creating hidden overlay)");

            _thread = new Thread(() =>
            {
                try
                {
                    var win = new InkOverlayWindow();
                    _window = win;
                    win.StrokeCompleted += (s, data) => StrokeCompleted?.Invoke(this, data);
                    win.InkCleared += (s, e) => InkCleared?.Invoke(this, e);
                    win.InkStateChanged += (s, e) => InkStateChanged?.Invoke(this, e);
                    win.BlankSolutionRequested += (s, e) => BlankSolutionRequested?.Invoke(this, e);
                    win.CurrentSlideSolutionRequested += (s, e) => CurrentSlideSolutionRequested?.Invoke(this, e);
                    win.SaveSolutionDraftRequested += (s, e) => SaveSolutionDraftRequested?.Invoke(this, e);
                    win.DiscardSolutionDraftRequested += (s, e) => DiscardSolutionDraftRequested?.Invoke(this, e);
                    win.Closed += (s, e) =>
                    {
                        // window closed; Detach() already unhooks on the COM thread
                        _winEventHook = IntPtr.Zero;
                        _winEventDelegate = null;
                        _window = null;
                        _slideshowHwnd = IntPtr.Zero;
                        System.Diagnostics.Debug.WriteLine("[InkOverlayService] Overlay closed");
                        System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                    };
                    ready.Set();
                    System.Diagnostics.Debug.WriteLine("[InkOverlayService] Overlay thread initialized");
                    win.Show();
                    win.Hide();
                    _overlayState = OverlayLifecycleState.AttachedHidden;
                    System.Diagnostics.Debug.WriteLine("[InkOverlayService] Overlay created hidden");

                    System.Windows.Threading.Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InkOverlayService] Overlay thread error: {ex}");
                }
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(2)))
                System.Diagnostics.Debug.WriteLine("[InkOverlayService] Timed out waiting for overlay readiness.");

            var window = _window;
            if (window == null)
                return;

            EnsureForegroundHook(window, hwnd);

            UpdateOverlayVisibilityForSlideshowState(hwnd, slideW, slideH, "attach");

            System.Diagnostics.Debug.WriteLine($"[InkOverlayService] Overlay attached to window hwnd={hwnd}.");
        }

        private void UpdateOverlayVisibilityForSlideshowState(IntPtr hwnd, double slideW, double slideH, string source)
        {
            var win = _window;
            if (win == null)
            {
                TransitionOverlayState(OverlayLifecycleState.Detached, $"{source}: overlay missing");
                return;
            }

            if (slideW <= 0 || slideH <= 0)
            {
                HideOverlayWindow(win, $"{source}: invalid slide dimensions");
                return;
            }

            if (!TryGetUsableSlideShowWindow(hwnd, out var reason))
            {
                HideOverlayWindow(win, $"{source}: {reason}");
                return;
            }

            try
            {
                win.SnapToWindow(hwnd, slideW, slideH);
            }
            catch (Exception ex)
            {
                HideOverlayWindow(win, $"{source}: snap failed ({ex.Message})");
                return;
            }

            try
            {
                win.Dispatcher.Invoke(() =>
                {
                    if (win.Visibility != Visibility.Visible)
                        win.Show();

                    win.EnforceWin32TopMost();
                });

                TransitionOverlayState(OverlayLifecycleState.AttachedVisible, $"{source}: shown");
            }
            catch (Exception ex)
            {
                HideOverlayWindow(win, $"{source}: show failed ({ex.Message})");
            }
        }

        private bool TryGetUsableSlideShowWindow(IntPtr hwnd, out string reason)
        {
            reason = "";

            if (hwnd == IntPtr.Zero)
            {
                reason = "missing hwnd";
                return false;
            }

            if (!GetWindowRect(hwnd, out var rect))
            {
                reason = "bounds unavailable";
                return false;
            }

            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                reason = "zero bounds";
                return false;
            }

            if (!IsWindowVisible(hwnd))
            {
                reason = "slideshow not visible";
                return false;
            }

            if (IsIconic(hwnd))
            {
                reason = "slideshow minimized";
                return false;
            }

            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                reason = "no foreground window";
                return false;
            }

            var foregroundRoot = GetRootWindowHandle(foreground);
            var slideshowRoot = GetRootWindowHandle(hwnd);
            if (foregroundRoot == IntPtr.Zero || slideshowRoot == IntPtr.Zero || foregroundRoot != slideshowRoot)
            {
                reason = "slideshow not foreground";
                return false;
            }

            return true;
        }

        private static IntPtr GetRootWindowHandle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return IntPtr.Zero;

            try
            {
                var root = GetAncestor(hwnd, GA_ROOT);
                return root != IntPtr.Zero ? root : hwnd;
            }
            catch
            {
                return hwnd;
            }
        }

        private void HideOverlayWindow(InkOverlayWindow win, string reason)
        {
            if (win == null)
                return;

            try
            {
                win.Dispatcher.Invoke(() =>
                {
                    if (win.Visibility == Visibility.Visible)
                        win.Hide();
                });
            }
            catch { }

            TransitionOverlayState(OverlayLifecycleState.AttachedHidden, reason);
        }

        private void TransitionOverlayState(OverlayLifecycleState nextState, string reason)
        {
            if (_overlayState == nextState)
                return;

            _overlayState = nextState;
            System.Diagnostics.Debug.WriteLine($"[InkOverlayService] Overlay state -> {nextState} ({reason})");
        }

        private void EnsureForegroundHook(InkOverlayWindow overlay, IntPtr targetHwnd)
        {
            if (overlay == null || targetHwnd == IntPtr.Zero)
                return;

            overlay._slideshowHwnd = targetHwnd;

            if (_winEventHook != IntPtr.Zero)
                return;

            overlay.Dispatcher.Invoke(() =>
            {
                if (_winEventHook != IntPtr.Zero)
                    return;

                _winEventDelegate = overlay.OnForegroundWindowChanged;
                _winEventHook = SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, _winEventDelegate!,
                    0, 0, WINEVENT_OUTOFCONTEXT);
            });

            System.Diagnostics.Debug.WriteLine("[InkOverlayService] Foreground hook installed for overlay window.");
        }

        /// <summary>
        /// Re-positions the overlay to match the specified window handle.
        /// Safe to call from the COM thread; marshals internally.
        /// </summary>
        public void SnapToWindow(IntPtr hwnd)
        {
            var win = _window;
            if (hwnd == IntPtr.Zero || win == null) return;

            try
            {
                // inner method already marshals to dispatcher
                win.SnapToWindow(hwnd);
            }
            catch
            {
                /* best effort: ignore overlay snap failures */
            }
        }

        /// <summary>
        /// Saves the current overlay strokes for the specified slide index
        /// into the FrameStore for backtrack restoration.
        /// </summary>
        public void SaveStrokesForSlide(int slideIndex)
        {
            if (slideIndex <= 0) return;
            var win = _window;
            if (win == null) return;

            try
            {
                List<OverlayStroke>? strokes = null;
                win.Dispatcher.Invoke(() => { strokes = win.GetCurrentStrokes(); });
                var safeStrokes = strokes ?? new List<OverlayStroke>();
                _frameStore.Save(slideIndex, safeStrokes);
                System.Diagnostics.Debug.WriteLine($"[InkOverlayService] Saved {safeStrokes.Count} strokes for slide {slideIndex}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InkOverlayService] Save strokes error: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores saved strokes for the specified slide (or clears if none saved).
        /// </summary>
        public void RestoreStrokesForSlide(int slideIndex)
        {
            var win = _window;
            if (win == null) return;

            try
            {
                _frameStore.TryGet(slideIndex, out var savedStrokes);
                win.Dispatcher.Invoke(() =>
                {
                    win.ClearStrokes();
                    if (savedStrokes != null && savedStrokes.Count > 0)
                    {
                        win.LoadStrokes(savedStrokes);
                    }
                });

                if (savedStrokes != null && savedStrokes.Count > 0)
                    System.Diagnostics.Debug.WriteLine($"[InkOverlayService] Restored {savedStrokes.Count} strokes for slide {slideIndex}");
                else
                    System.Diagnostics.Debug.WriteLine($"[InkOverlayService] No saved strokes for slide {slideIndex} — overlay cleared");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InkOverlayService] Restore strokes error: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads strokes from a real-time SignalR payload (InkStrokeData) into the overlay.
        /// </summary>
        public Task LoadStrokesFromDataAsync(List<InkStrokeData> strokes)
        {
            LoadStrokesFromData(strokes);
            return Task.CompletedTask;
        }

        public void LoadStrokesFromData(List<InkStrokeData> strokes)
        {
            if (strokes == null) return;
            var win = _window;
            if (win == null) return;

            try
            {
                var overlayStrokes = BuildOverlayStrokes(strokes);

                win.Dispatcher.Invoke(() =>
                {
                    win.ClearStrokes();
                    if (overlayStrokes.Count > 0)
                        win.LoadStrokes(overlayStrokes);
                });

                System.Diagnostics.Debug.WriteLine(
                    $"[InkOverlayService] First ink/state sync completed: strokes={overlayStrokes.Count}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InkOverlayService] LoadStrokesFromData error: {ex}");
            }
        }

        private static List<OverlayStroke> BuildOverlayStrokes(List<InkStrokeData> strokes)
        {
            var overlayStrokes = new List<OverlayStroke>();
            foreach (var s in strokes)
            {
                if (s == null || s.Points == null) continue;

                var normalizedPoints = s.Points
                    .Where(p => p != null && p.Length >= 2)
                    .Select(p => new Point(p[0], p[1]))
                    .ToList();

                // Skip strokes that have no valid points after filtering to avoid creating empty WPF Strokes.
                if (normalizedPoints.Count == 0)
                    continue;

                overlayStrokes.Add(new OverlayStroke
                {
                    NormalizedPoints = normalizedPoints,
                    Color = TryParseColor(s.Color, Colors.Red),
                    Thickness = s.Width > 0 ? s.Width : 3
                });
            }

            return overlayStrokes;
        }

        /// <summary>
        /// Returns the current overlay strokes as serializable InkStrokeData.
        /// </summary>
        public List<InkStrokeData> GetCurrentStrokeData()
        {
            var win = _window;
            if (win == null) return new List<InkStrokeData>();

            List<InkStrokeData>? result = null;
            try
            {
                win.Dispatcher.Invoke(() => { result = win.GetAllStrokesData(); });
            }
            catch
            {
                result = new List<InkStrokeData>();
            }

            return result ?? new List<InkStrokeData>();
        }

        private static Color TryParseColor(string colorStr, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(colorStr)) return fallback;
            try
            {
                var result = ColorConverter.ConvertFromString(colorStr);
                return result is Color c ? c : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Captures the current ink as a transparent PNG and clears the canvas.
        /// Returns null if no ink present or overlay is unavailable.
        /// </summary>
        public byte[]? CaptureInkImageAndClear()
        {
            var win = _window;
            if (win == null)
            {
                System.Diagnostics.Debug.WriteLine("[InkOverlayService] CaptureInk: overlay is null");
                return null;
            }

            byte[]? result = null;
            bool hasInk = false;

            win.Dispatcher.Invoke(() =>
            {
                hasInk = win.HasInk();
                if (hasInk)
                {
                    result = win.GetInkImage();
                    win.ClearInk();
                }
            });

            return result;
        }

        /// <summary>
        /// Clears the overlay canvas without raising events (for shutdown/cleanup).
        /// </summary>
        public void ClearCanvas()
        {
            var win = _window;
            if (win == null) return;
            try { win.Dispatcher.Invoke(() => win.ClearStrokes()); }
            catch { }
        }

        /// <summary>
        /// Clears the ink strokes from the actual WPF InkCanvas.
        /// </summary>
        public void ClearInk()
        {
            var win = _window;
            if (win == null) return;
            try { win.Dispatcher.Invoke(() => win.ClearInk()); }
            catch { }
        }

        /// <summary>
        /// Shows a white slide-region surface in the overlay for blank solution pages.
        /// </summary>
        public void EnableBlankSolutionSurface()
        {
            var win = _window;
            if (win == null) return;
            try { win.Dispatcher.Invoke(() => win.SetBlankSolutionSurfaceEnabled(true)); }
            catch { }
        }

        /// <summary>
        /// Restores normal overlay behavior (transparent over the live slide).
        /// </summary>
        public void DisableBlankSolutionSurface()
        {
            var win = _window;
            if (win == null) return;
            try { win.Dispatcher.Invoke(() => win.SetBlankSolutionSurfaceEnabled(false)); }
            catch { }
        }

        /// <summary>
        /// Updates overlay controls according to the active solution draft lifecycle state.
        /// </summary>
        internal void SetSolutionDraftState(SolutionDraftLifecycleState state)
        {
            var win = _window;
            if (win == null) return;
            try { win.Dispatcher.Invoke(() => win.SetSolutionDraftState(state)); }
            catch { }
        }

        internal bool HideOverlayForBackgroundCapture()
        {
            var win = _window;
            if (win == null) return false;

            bool wasVisible = false;
            try
            {
                win.Dispatcher.Invoke(() =>
                {
                    wasVisible = win.Visibility == Visibility.Visible;
                    if (wasVisible)
                        win.Hide();
                });
            }
            catch
            {
                return false;
            }

            return wasVisible;
        }

        internal void RestoreOverlayAfterBackgroundCapture(bool wasVisible)
        {
            if (!wasVisible)
                return;

            var win = _window;
            if (win == null) return;

            try
            {
                win.Dispatcher.Invoke(() =>
                {
                    if (win.Visibility != Visibility.Visible)
                        win.Show();

                    win.EnforceWin32TopMost();
                });
            }
            catch { }
        }

        /// <summary>
        /// Computes a stable render size for ink snapshot export based on slide aspect ratio.
        /// </summary>
        public (int Width, int Height) GetPreferredInkRenderSize(int targetWidth = 1536)
        {
            double aspect = 16d / 9d;
            var win = _window;

            if (win != null)
            {
                try
                {
                    win.Dispatcher.Invoke(() =>
                    {
                        var resolved = win.GetSlideAspectRatio();
                        if (resolved > 0 && !double.IsNaN(resolved) && !double.IsInfinity(resolved))
                            aspect = resolved;
                    });
                }
                catch
                {
                    // Keep fallback aspect ratio.
                }
            }

            int width = Math.Max(320, targetWidth);
            int height = Math.Max(180, (int)Math.Round(width / aspect));
            return (width, height);
        }

        /// <summary>
        /// Renders an export-only ink snapshot with no slide content.
        /// Use transparentBackground=true for annotated slide compositing,
        /// and transparentBackground=false for explicit ink-only artifacts.
        /// Returns null when there are no strokes, so callers can skip page creation.
        /// </summary>
        public byte[]? RenderInkArtifactSnapshot(List<InkStrokeData> strokes, int targetWidth = 1536, bool transparentBackground = false)
        {
            if (strokes == null || strokes.Count == 0)
                return null;

            var size = GetPreferredInkRenderSize(targetWidth);
            return GeneratePngFromVectorData(strokes, size.Width, size.Height, transparentBackground);
        }

        public byte[]? GeneratePngFromVectorData(List<InkStrokeData> strokes, int width = 1536, int height = 960, bool transparentBackground = false)
        {
            if (strokes == null)
                strokes = new List<InkStrokeData>();

            byte[]? result = null;

            // Must create WPF objects on STA thread, we can use the dispatcher if window exists
            // or perform it directly if we are confident about thread state. Since this might be called
            // from COM thread, let's use dispatcher to be safe if window is alive, or spin up a quick STA task
            var win = _window;
            if (win != null)
            {
                win.Dispatcher.Invoke(() =>
                {
                    result = InternalGeneratePng(strokes, width, height, transparentBackground);
                });
            }
            else
            {
                var t = new Thread(() =>
                {
                    result = InternalGeneratePng(strokes, width, height, transparentBackground);
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
            }

            return result;
        }

        private byte[] InternalGeneratePng(List<InkStrokeData> strokes, int width, int height, bool transparentBackground)
        {
            var inkCanvas = new System.Windows.Controls.InkCanvas
            {
                Width = width,
                Height = height,
                Background = transparentBackground
                    ? System.Windows.Media.Brushes.Transparent
                    : System.Windows.Media.Brushes.White
            };

            // Reconstruct the WPF strokes from our normalized data
            foreach (var strokeData in strokes)
            {
                if (strokeData.Points == null || strokeData.Points.Count == 0) continue;

                var stylusPoints = new System.Windows.Input.StylusPointCollection();
                foreach (var p in strokeData.Points)
                {
                    // De-normalize back to pixel coordinates
                    stylusPoints.Add(new System.Windows.Input.StylusPoint(p[0] * width, p[1] * height));
                }

                var wpfStroke = new System.Windows.Ink.Stroke(stylusPoints);
                
                // Try to parse the color, default to red
                System.Windows.Media.Color strokeColor = System.Windows.Media.Colors.Red;
                try { strokeColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(strokeData.Color); } catch { }

                wpfStroke.DrawingAttributes.Color = strokeColor;
                wpfStroke.DrawingAttributes.Width = strokeData.Width;
                wpfStroke.DrawingAttributes.Height = strokeData.Width;
                wpfStroke.DrawingAttributes.FitToCurve = true;

                inkCanvas.Strokes.Add(wpfStroke);
            }

            // Render to PNG
            inkCanvas.Measure(new System.Windows.Size(width, height));
            inkCanvas.Arrange(new System.Windows.Rect(new System.Windows.Size(width, height)));

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96d, 96d, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(inkCanvas);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));

            using (var ms = new System.IO.MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Closes the overlay window and shuts down its dispatcher thread.
        /// </summary>
        public void Detach()
        {
            try
            {
                var win = _window;

                if (win != null)
                {
                    // perform unhook *and* close in single dispatcher trip to avoid
                    // races with shutdown
                    win.Dispatcher.Invoke(() =>
                    {
                        if (win.Visibility == Visibility.Visible)
                            win.Hide();

                        if (_winEventHook != IntPtr.Zero)
                        {
                            UnhookWinEvent(_winEventHook);
                            _winEventHook = IntPtr.Zero;
                        }
                        try { win.Close(); }
                        catch { }
                    });

                    _winEventDelegate = null;
                    _winEventHook = IntPtr.Zero;
                    _window = null;
                    _slideshowHwnd = IntPtr.Zero;
                    _overlayState = OverlayLifecycleState.Detached;
                }
                else
                {
                    _winEventDelegate = null;
                    _winEventHook = IntPtr.Zero;
                    _slideshowHwnd = IntPtr.Zero;
                    _overlayState = OverlayLifecycleState.Detached;
                }

                if (_thread != null && _thread.IsAlive)
                {
                    try
                    {
                        if (!_thread.Join(2000))
                        {
                            System.Diagnostics.Debug.WriteLine("[InkOverlayService] Overlay thread did not exit gracefully.");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[InkOverlayService] Error waiting for overlay thread: {ex}");
                    }
                    finally
                    {
                        _thread = null;
                    }
                }

                System.Diagnostics.Debug.WriteLine("[InkOverlayService] Overlay detached");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InkOverlayService] Detach error: {ex}");
            }
        }
    }
}
