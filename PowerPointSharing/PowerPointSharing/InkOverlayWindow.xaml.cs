using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerPointSharing
{
    /// <summary>
    /// WPF Ink Overlay Window - provides a transparent InkCanvas overlay for annotations.
    /// 
    /// KEY DESIGN: 
    /// - The Window is ALWAYS hit-test visible (so the toolbar buttons work).
    /// - The InkCanvas toggles IsHitTestVisible for click-through vs draw mode.
    /// - This avoids focus/input issues with PowerPoint stealing focus.
    /// - REAL-TIME STREAMING: Strokes are captured and sent to viewers via SignalR.
    /// </summary>
    public partial class InkOverlayWindow : Window
    {
        // Win32 interop used for window snapping and topmost re‑assertion
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint GA_ROOT = 2;
        private const uint SWP_NOMOVE     = 0x0002;
        private const uint SWP_NOSIZE     = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int  GWL_EXSTYLE      = -20;
        private const int  WS_EX_NOACTIVATE = 0x08000000;
        private const int  WS_EX_TOOLWINDOW = 0x00000080;

        private bool _isDrawMode = false;
        
        // ---------- foreground-window hook for visibility tracking ----------
        // Delegate type exposed so service can register the hook and manage its
        // lifetime. Previously the delegate lived on the window, causing a
        // race if the window was GC'd before the Win32 hook was unregistered.
        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        // Constants used by the hook; not strictly required by the window anymore,
        // but leaving them public makes it simple for the service to reuse them.
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT   = 0x0000;

        // lifetime-managed by InkOverlayService now, so no fields here
        internal volatile IntPtr _slideshowHwnd;     // updated by SnapToWindow

        // cached slide dimensions (points) so that SnapToWindow can recompute
        // the layout even when subsequent calls omit the values.
        private double _slideWidthPt;
        private double _slideHeightPt;

        // Slide content rect within the overlay window (excludes letterbox bars)
        private Rect _slideRect = Rect.Empty;
        private bool _blankSolutionSurfaceEnabled = false;

        // normalization helpers that respect letterboxing
        private double NormW  => _slideRect.IsEmpty ? ActualWidth  : _slideRect.Width;
        private double NormH  => _slideRect.IsEmpty ? ActualHeight : _slideRect.Height;
        private double NormOX => _slideRect.IsEmpty ? 0            : _slideRect.X;
        private double NormOY => _slideRect.IsEmpty ? 0            : _slideRect.Y;

        /// <summary>
        /// Shared work: recompute slideRect based on overlay dip size and slide aspect.
        /// Called by SnapToWindow when slide dims are available.
        /// </summary>
        private void UpdateSlideRect(double winW, double winH,
                                      double slideWidthPt, double slideHeightPt)
        {
            if (slideHeightPt <= 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[InkOverlay] UpdateSlideRect called with non-positive slideHeightPt; aborting layout.");
                return;
            }
            double slideAspect = slideWidthPt / slideHeightPt;
            double winAspect   = winW / winH;
            double contentW, contentH, offsetX, offsetY;

            if (slideAspect > winAspect)
            {
                contentW = winW;
                contentH = winW / slideAspect;
                offsetX  = 0;
                offsetY  = (winH - contentH) / 2.0;
            }
            else
            {
                contentH = winH;
                contentW = winH * slideAspect;
                offsetX  = (winW - contentW) / 2.0;
                offsetY  = 0;
            }

            _slideRect = new Rect(offsetX, offsetY, contentW, contentH);
            inkCanvas.Clip = new RectangleGeometry(_slideRect);
            UpdateBlankSolutionMaskLayout();

            System.Diagnostics.Debug.WriteLine(
                $"[InkOverlay] SlideRect=({offsetX:F1},{offsetY:F1}) " +
                $"{contentW:F1}×{contentH:F1} " +
                $"(win={winW:F1}×{winH:F1} dip, aspect={slideAspect:F3})");
        }

        // the hook lives in InkOverlayService; the window only exposes the
        // callback method.  (DllImports removed here.)

        internal void OnForegroundWindowChanged(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            var slideshowRoot = GetRootWindowHandle(_slideshowHwnd);
            var foregroundRoot = GetRootWindowHandle(hwnd);
            bool hasValidBounds = _slideshowHwnd != IntPtr.Zero
                && GetWindowRect(_slideshowHwnd, out var rect)
                && rect.Right > rect.Left
                && rect.Bottom > rect.Top;

            bool slideshowIsActive = slideshowRoot != IntPtr.Zero
                && foregroundRoot != IntPtr.Zero
                && foregroundRoot == slideshowRoot
                && hasValidBounds
                && IsWindowVisible(_slideshowHwnd)
                && !IsIconic(_slideshowHwnd);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (slideshowIsActive)
                {
                    if (Visibility != Visibility.Visible)
                    {
                        Show();
                        EnforceWin32TopMost();
                    }
                }
                else
                {
                    if (Visibility == Visibility.Visible)
                        Hide();
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[InkOverlay] Foreground changed → {(slideshowIsActive ? "SHOW" : "HIDE")}");
            }));
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

        /// <summary>
        /// Event fired when a stroke is completed and ready for transmission.
        /// </summary>
        public event EventHandler<InkStrokeData>? StrokeCompleted;
        
        /// <summary>
        /// Event fired when ink is cleared.
        /// </summary>
        public event EventHandler? InkCleared;

        /// <summary>
        /// Event fired when user actions (erase/clear) changed the full stroke set.
        /// </summary>
        public event EventHandler? InkStateChanged;

        /// <summary>
        /// Event fired when presenter requests a blank solution page from the overlay toolbar.
        /// </summary>
        public event EventHandler? BlankSolutionRequested;

        /// <summary>
        /// Event fired when presenter requests a current-slide solution page from the overlay toolbar.
        /// </summary>
        public event EventHandler? CurrentSlideSolutionRequested;

        /// <summary>
        /// Event fired when presenter saves the active solution draft.
        /// </summary>
        public event EventHandler? SaveSolutionDraftRequested;

        /// <summary>
        /// Event fired when presenter discards the active solution draft.
        /// </summary>
        public event EventHandler? DiscardSolutionDraftRequested;

        private int _suppressInkStateChangedDepth = 0;

        public InkOverlayWindow()
        {
            InitializeComponent();
            
            // Subscribe to stroke completed event for real-time streaming
            inkCanvas.StrokeCollected += InkCanvas_StrokeCollected;
            inkCanvas.Strokes.StrokesChanged += InkCanvas_StrokesChanged;
            
            // Start in Click-Through mode (InkCanvas not hit-testable)
            EnableClickThroughMode();
        }

        // ensure focus/activation behavior and initial topmost state
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;

            // make overlay non-activating and hide from Alt+Tab
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

            // initial topmost with show flag (only once); subsequent calls (e.g., EnforceWin32TopMost)
            // use SWP_NOACTIVATE instead to avoid stealing focus while still enforcing topmost state
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }
        
        /// <summary>
        /// Handle stroke collection - serialize and raise event for transmission.
        /// </summary>
        private void InkCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            try
            {
                var stroke = e.Stroke;
                var strokeData = SerializeStroke(stroke);
                
                if (strokeData != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[InkOverlay] Stroke captured: {strokeData.Points.Count} points, color={strokeData.Color}");
                    StrokeCompleted?.Invoke(this, strokeData);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InkOverlay] StrokeCollected error: {ex.Message}");
            }
        }

        private void InkCanvas_StrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
        {
            if (_suppressInkStateChangedDepth > 0) return;

            // We only need this callback for erase/clear paths. Adds are already handled
            // by StrokeCollected and should remain low-latency incremental broadcasts.
            if (e?.Removed == null || e.Removed.Count <= 0) return;

            InkStateChanged?.Invoke(this, EventArgs.Empty);
        }
        
        /// <summary>
        /// Serialize a WPF Stroke to InkStrokeData with normalized coordinates (0-1).
        /// </summary>
        private InkStrokeData? SerializeStroke(Stroke stroke)
        {
            if (stroke == null || stroke.StylusPoints.Count == 0) return null;

            var data = new InkStrokeData
            {
                StrokeId  = Guid.NewGuid().ToString(),
                Color     = stroke.DrawingAttributes.Color.ToString(),
                Width     = stroke.DrawingAttributes.Width,
                Opacity   = stroke.DrawingAttributes.IsHighlighter ? 0.5 : 1,
                Timestamp = DateTime.UtcNow
            };

            foreach (var pt in stroke.StylusPoints)
                data.Points.Add(new double[]
                {
                    (pt.X - NormOX) / NormW,
                    (pt.Y - NormOY) / NormH
                });

            return data;
        }

        /// <summary>
        /// When the window is activated, force focus to the canvas if in draw mode.
        /// </summary>
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            
            if (_isDrawMode)
            {
                // Force focus to the canvas so it receives mouse/touch events
                inkCanvas.Focus();
            }
            else
            {
                RestorePowerPointFocus();
            }
        }

        /// <summary>
        /// Update overlay geometry to match the specified window handle.
        /// Call this from the PowerPoint thread; the method marshals to the
        /// overlay dispatcher before touching WPF properties.
        /// </summary>
        public void SnapToWindow(IntPtr targetHwnd, double slideWidthPt = 0, double slideHeightPt = 0)
        {
            if (targetHwnd == IntPtr.Zero) return;
            if (!GetWindowRect(targetHwnd, out RECT r)) return;

            double physW = r.Right  - r.Left;
            double physH = r.Bottom - r.Top;

            Dispatcher.Invoke(() =>
            {
                var source = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                double dipW = physW / dpiX;
                double dipH = physH / dpiY;

                Left   = r.Left / dpiX;
                Top    = r.Top  / dpiY;
                Width  = dipW;
                Height = dipH;

                // cache any provided slide dimensions; they may be omitted in
                // later snaps when only the window position/size changes.
                if (slideWidthPt > 0) _slideWidthPt = slideWidthPt;
                if (slideHeightPt > 0) _slideHeightPt = slideHeightPt;

                if (_slideWidthPt > 0 && _slideHeightPt > 0)
                    UpdateSlideRect(dipW, dipH, _slideWidthPt, _slideHeightPt);

                EnforceWin32TopMost();
            });

            // just update the handle; service owns the hook now
            _slideshowHwnd = targetHwnd;
        }

        // re‑assert topmost without activating or repainting
        public void EnforceWin32TopMost()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        protected override void OnClosed(EventArgs e)
        {
            // overlay service will unhook before closing; nothing needed here
            base.OnClosed(e);
        }

        // ============================================================
        // MODE TOGGLING - The core fix for focus/input issues
        // ============================================================

        /// <summary>
        /// Toggle between Draw Mode and Click-Through Mode.
        /// </summary>
        private void BtnToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isDrawMode)
            {
                EnableClickThroughMode();
            }
            else
            {
                EnableDrawMode();
            }
        }

        /// <summary>
        /// Enable Draw Mode - InkCanvas captures mouse input.
        /// </summary>
        public void EnableDrawMode()
        {
            _isDrawMode = true;
            
            // Make InkCanvas receive mouse events
            inkCanvas.IsHitTestVisible = true;
            inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            
            // Almost transparent background (fixes some click detection issues)
            inkCanvas.Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
            
            // Steal focus from PowerPoint
            this.Focus();
            inkCanvas.Focus();
            
            // Update UI
            btnToggle.Content = "🖱️ Click";
            btnToggle.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
            txtModeStatus.Text = "🖊️ Draw Mode - Drawing on overlay";
            
            System.Diagnostics.Debug.WriteLine("[InkOverlay] Switched to DRAW MODE");
        }

        /// <summary>
        /// Enable Click-Through Mode - Mouse events pass to PowerPoint.
        /// </summary>
        public void EnableClickThroughMode()
        {
            _isDrawMode = false;
            
            // Make InkCanvas transparent to clicks
            inkCanvas.IsHitTestVisible = false;
            inkCanvas.EditingMode = InkCanvasEditingMode.None;
            inkCanvas.Background = Brushes.Transparent;
            
            // Update UI
            btnToggle.Content = "🖊️ Draw";
            btnToggle.Background = new SolidColorBrush(Color.FromRgb(33, 150, 243)); // Blue
            txtModeStatus.Text = "🖱️ Click Mode - Clicks pass to PowerPoint";

            RestorePowerPointFocus();
            
            System.Diagnostics.Debug.WriteLine("[InkOverlay] Switched to CLICK-THROUGH MODE");
        }

        private void RestorePowerPointFocus()
        {
            if (_slideshowHwnd == IntPtr.Zero)
                return;

            Keyboard.ClearFocus();
            SetForegroundWindow(_slideshowHwnd);
        }

        /// <summary>
        /// Check if currently in draw mode.
        /// </summary>
        public bool IsDrawMode => _isDrawMode;

        /// <summary>
        /// Toggles white blank-solution surface visibility within the computed slide clip region.
        /// When enabled, presenter can draw over a white canvas instead of seeing the live slide.
        /// </summary>
        public void SetBlankSolutionSurfaceEnabled(bool enabled)
        {
            _blankSolutionSurfaceEnabled = enabled;
            UpdateBlankSolutionMaskLayout();

            if (enabled)
            {
                EnableDrawMode();
                txtModeStatus.Text = "🧾 Blank Solution Mode - Draw on white surface";
            }
        }

        internal void SetSolutionDraftState(SolutionDraftLifecycleState state)
        {
            bool isDraft = state == SolutionDraftLifecycleState.DraftBlankSolution
                || state == SolutionDraftLifecycleState.DraftCurrentSlideSolution
                || state == SolutionDraftLifecycleState.SavingSolution;

            bool canEditDraft = state == SolutionDraftLifecycleState.DraftBlankSolution
                || state == SolutionDraftLifecycleState.DraftCurrentSlideSolution;

            btnBlankSolution.IsEnabled = state == SolutionDraftLifecycleState.NormalSlideInk;
            btnCurrentSlideSolution.IsEnabled = state == SolutionDraftLifecycleState.NormalSlideInk;

            btnSaveSolution.Visibility = isDraft ? Visibility.Visible : Visibility.Collapsed;
            btnDiscardSolution.Visibility = isDraft ? Visibility.Visible : Visibility.Collapsed;

            btnSaveSolution.IsEnabled = canEditDraft;
            btnDiscardSolution.IsEnabled = canEditDraft;

            btnToggle.IsEnabled = state != SolutionDraftLifecycleState.SavingSolution;
            btnEraser.IsEnabled = state != SolutionDraftLifecycleState.SavingSolution;
            btnClear.IsEnabled = state != SolutionDraftLifecycleState.SavingSolution;
            cmbColor.IsEnabled = state != SolutionDraftLifecycleState.SavingSolution;
            sliderWidth.IsEnabled = state != SolutionDraftLifecycleState.SavingSolution;

            if (state == SolutionDraftLifecycleState.SavingSolution)
            {
                _isDrawMode = false;
                inkCanvas.IsHitTestVisible = false;
                inkCanvas.EditingMode = InkCanvasEditingMode.None;
                inkCanvas.Background = Brushes.Transparent;
                txtModeStatus.Text = "💾 Saving solution draft...";
                return;
            }

            if (state == SolutionDraftLifecycleState.DraftBlankSolution)
            {
                EnableDrawMode();
                txtModeStatus.Text = "🧾 Draft Blank Solution - Draw, then Save or Discard";
                return;
            }

            if (state == SolutionDraftLifecycleState.DraftCurrentSlideSolution)
            {
                EnableDrawMode();
                txtModeStatus.Text = "🖼️ Draft Slide Solution - Draw, then Save or Discard";
            }
        }

        private void UpdateBlankSolutionMaskLayout()
        {
            if (blankSolutionMask == null)
                return;

            if (!_blankSolutionSurfaceEnabled || _slideRect.IsEmpty || _slideRect.Width <= 0 || _slideRect.Height <= 0)
            {
                blankSolutionMask.Visibility = Visibility.Collapsed;
                return;
            }

            blankSolutionMask.Visibility = Visibility.Visible;
            blankSolutionMask.Margin = new Thickness(_slideRect.X, _slideRect.Y, 0, 0);
            blankSolutionMask.Width = _slideRect.Width;
            blankSolutionMask.Height = _slideRect.Height;
        }

        // ============================================================
        // TOOLBAR BUTTON HANDLERS
        // ============================================================

        private void BtnEraser_Click(object sender, RoutedEventArgs e)
        {
            // Switch to eraser mode (and ensure draw mode is active)
            _isDrawMode = true;
            inkCanvas.IsHitTestVisible = true;
            inkCanvas.EditingMode = InkCanvasEditingMode.EraseByStroke;
            inkCanvas.Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
            
            btnToggle.Content = "🖱️ Click";
            btnToggle.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            txtModeStatus.Text = "🧹 Eraser Mode - Click strokes to erase";
            
            this.Focus();
            inkCanvas.Focus();
            
            System.Diagnostics.Debug.WriteLine("[InkOverlay] Switched to ERASER MODE");
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            ClearInk();
        }

        private void BtnBlankSolution_Click(object sender, RoutedEventArgs e)
        {
            BlankSolutionRequested?.Invoke(this, EventArgs.Empty);
            System.Diagnostics.Debug.WriteLine("[InkOverlay] Blank solution page requested from overlay toolbar");
        }

        private void BtnCurrentSlideSolution_Click(object sender, RoutedEventArgs e)
        {
            CurrentSlideSolutionRequested?.Invoke(this, EventArgs.Empty);
            System.Diagnostics.Debug.WriteLine("[InkOverlay] Current-slide solution page requested from overlay toolbar");
        }

        private void BtnSaveSolution_Click(object sender, RoutedEventArgs e)
        {
            SaveSolutionDraftRequested?.Invoke(this, EventArgs.Empty);
            System.Diagnostics.Debug.WriteLine("[InkOverlay] Save solution draft requested from overlay toolbar");
        }

        private void BtnDiscardSolution_Click(object sender, RoutedEventArgs e)
        {
            DiscardSolutionDraftRequested?.Invoke(this, EventArgs.Empty);
            System.Diagnostics.Debug.WriteLine("[InkOverlay] Discard solution draft requested from overlay toolbar");
        }

        private void CmbColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbColor.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                try
                {
                    var colorName = item.Tag.ToString();
                    var color = (Color)ColorConverter.ConvertFromString(colorName);
                    var attrs = inkCanvas.DefaultDrawingAttributes.Clone();
                    attrs.Color = color;
                    inkCanvas.DefaultDrawingAttributes = attrs;
                    System.Diagnostics.Debug.WriteLine($"[InkOverlay] Color changed to {colorName}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InkOverlay] Color change error: {ex.Message}");
                }
            }
        }

        private void SliderWidth_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (inkCanvas != null)
            {
                var attrs = inkCanvas.DefaultDrawingAttributes.Clone();
                attrs.Width = e.NewValue;
                attrs.Height = e.NewValue;
                inkCanvas.DefaultDrawingAttributes = attrs;
            }
        }

        // ============================================================
        // PUBLIC METHODS FOR ThisAddIn INTEGRATION
        // ============================================================

        /// <summary>
        /// Clear all ink strokes from the canvas.
        /// </summary>
        public void ClearInk()
        {
            _suppressInkStateChangedDepth++;
            try
            {
                inkCanvas.Strokes.Clear();
            }
            finally
            {
                _suppressInkStateChangedDepth--;
            }
            System.Diagnostics.Debug.WriteLine("[InkOverlay] Ink cleared");
            
            // Raise event to notify listeners (for SignalR broadcast)
            InkCleared?.Invoke(this, EventArgs.Empty);
        }
        
        /// <summary>
        /// Get all current strokes as serialized data (for late-joining viewers).
        /// </summary>
        public List<InkStrokeData> GetAllStrokesData()
        {
            var strokesData = new List<InkStrokeData>();
            
            foreach (var stroke in inkCanvas.Strokes)
            {
                var data = SerializeStroke(stroke);
                if (data != null)
                {
                    strokesData.Add(data);
                }
            }
            
            return strokesData;
        }

        // ============================================================
        // PER-SLIDE STROKE STORAGE / RESTORE METHODS
        // ============================================================

        /// <summary>
        /// Returns a snapshot of all current strokes as OverlayStroke objects.
        /// Strokes are stored using normalized coordinates so that resizing the
        /// overlay (e.g. when the slideshow window moves between monitors) does
        /// not corrupt the saved geometry.
        /// </summary>
        public List<OverlayStroke> GetCurrentStrokes()
        {
            var result = new List<OverlayStroke>();
            foreach (var stroke in inkCanvas.Strokes)
            {
                result.Add(new OverlayStroke
                {
                    NormalizedPoints = stroke.StylusPoints
                        .Select(sp => new Point(
                            (sp.X - NormOX) / NormW,
                            (sp.Y - NormOY) / NormH))
                        .ToList(),
                    Color     = stroke.DrawingAttributes.Color,
                    Thickness = stroke.DrawingAttributes.Width
                });
            }
            return result;
        }

        /// <summary>
        /// Clears all strokes from the overlay canvas (for per-slide restore flow).
        /// </summary>
        public void ClearStrokes()
        {
            _suppressInkStateChangedDepth++;
            try
            {
                inkCanvas.Strokes.Clear();
            }
            finally
            {
                _suppressInkStateChangedDepth--;
            }
            System.Diagnostics.Debug.WriteLine("[InkOverlay] Strokes cleared (per-slide restore)");
        }

        /// <summary>
        /// Redraws a saved stroke list onto the canvas.
        /// Called when the presenter backtracks to a previously visited slide.
        /// </summary>
        public void LoadStrokes(List<OverlayStroke> strokes)
        {
            _suppressInkStateChangedDepth++;
            try
            {
                foreach (var s in strokes)
                {
                    var points = new StylusPointCollection(
                        s.NormalizedPoints.Select(p =>
                            new StylusPoint(
                                p.X * NormW + NormOX,
                                p.Y * NormH + NormOY)));

                    var stroke = new Stroke(points)
                    {
                        DrawingAttributes = new DrawingAttributes
                        {
                            Color     = s.Color,
                            Width     = s.Thickness,
                            Height    = s.Thickness,
                            StylusTip = StylusTip.Ellipse
                        }
                    };
                    inkCanvas.Strokes.Add(stroke);
                }
            }
            finally
            {
                _suppressInkStateChangedDepth--;
            }
            System.Diagnostics.Debug.WriteLine(
                $"[InkOverlay] Loaded {strokes.Count} strokes (per-slide restore)");
        }

        /// <summary>
        /// Check if there are any ink strokes on the canvas.
        /// </summary>
        public bool HasInk()
        {
            return inkCanvas.Strokes.Count > 0;
        }

        /// <summary>
        /// Returns the best-known slide aspect ratio for ink export rendering.
        /// </summary>
        public double GetSlideAspectRatio()
        {
            if (_slideWidthPt > 0 && _slideHeightPt > 0)
                return _slideWidthPt / _slideHeightPt;

            if (!_slideRect.IsEmpty && _slideRect.Height > 0)
                return _slideRect.Width / _slideRect.Height;

            if (ActualHeight > 0)
                return ActualWidth / ActualHeight;

            return 16d / 9d;
        }

        /// <summary>
        /// Capture the current ink strokes as a transparent PNG image.
        /// Returns null if no ink is present.
        /// </summary>
        /// <returns>PNG image bytes or null</returns>
        public byte[]? GetInkImage()
        {
            if (inkCanvas.Strokes.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[InkOverlay] No ink to capture");
                return null;
            }

            try
            {
                // Use the full window size for the image (to match slide dimensions)
                int width = (int)this.ActualWidth;
                int height = (int)this.ActualHeight;

                if (width <= 0 || height <= 0)
                {
                    width = (int)SystemParameters.PrimaryScreenWidth;
                    height = (int)SystemParameters.PrimaryScreenHeight;
                }

                // Create a DrawingVisual to render the strokes
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    // Transparent background
                    context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
                    
                    // Draw each stroke
                    foreach (var stroke in inkCanvas.Strokes)
                    {
                        stroke.Draw(context);
                    }
                }

                // Render to bitmap
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);

                // Encode as PNG
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    var bytes = stream.ToArray();
                    System.Diagnostics.Debug.WriteLine($"[InkOverlay] Captured ink image: {bytes.Length} bytes ({width}x{height})");
                    return bytes;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InkOverlay] GetInkImage error: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Save the current ink as a PNG file to the specified path.
        /// </summary>
        public bool SaveInkToFile(string filePath)
        {
            var bytes = GetInkImage();
            if (bytes == null) return false;

            try
            {
                File.WriteAllBytes(filePath, bytes);
                System.Diagnostics.Debug.WriteLine($"[InkOverlay] Saved ink to {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InkOverlay] SaveInkToFile error: {ex}");
                return false;
            }
        }
    }
}
