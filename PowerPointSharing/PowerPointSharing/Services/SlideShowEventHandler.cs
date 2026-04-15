using System;
using System.Runtime.InteropServices;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;

namespace PowerPointSharing
{
    /// <summary>
    /// Owns all PowerPoint SlideShow* event handlers.
    /// Hooks both SlideShowNextSlide (slide transitions) and
    /// SlideShowNextClick / SlideShowNextBuild (animation clicks)
    /// to broadcast the correct absolute frame index.
    /// </summary>
    public class SlideShowEventHandler
    {
        private readonly PowerPoint.Application _app;
        private readonly SessionManager _session;
        private readonly InkOverlayService _inkOverlay;

        // Track the last broadcast (slideIndex, clickIndex) to avoid duplicate signals
        private int _lastBroadcastSlide = -1;
        private int _lastBroadcastClick = -1;
        private int _lastObservedSlideIndex = -1;
        private readonly object _broadcastLock = new object();
        private System.Threading.Timer? _pollTimer;

        public SlideShowEventHandler(PowerPoint.Application app,
                                     SessionManager session,
                                     InkOverlayService inkOverlay)
        {
            _app = app;
            _session = session;
            _inkOverlay = inkOverlay;
        }

        public void OnSlideShowBegin(PowerPoint.SlideShowWindow Wn)
        {
            StartLiveSlideShow(Wn);
        }

        internal void StartLiveSlideShow(PowerPoint.SlideShowWindow Wn)
        {
            if (Wn == null)
                return;

            try
            {
                System.Diagnostics.Debug.WriteLine("[SlideShowEventHandler] Slideshow started.");

                // Defensive unhook before wiring — prevents double subscription on slideshow restart.
                try { _app.SlideShowNextBuild -= OnSlideShowNextBuild; } catch { }
                try { _app.SlideShowNextClick -= OnSlideShowNextClick; } catch { }

                try { _pollTimer?.Dispose(); } catch { }
                _pollTimer = null;

                _lastBroadcastSlide = -1;
                _lastBroadcastClick = -1;
                _lastObservedSlideIndex = -1;

                if (!IsForActivePresentation(Wn))
                {
                    System.Diagnostics.Debug.WriteLine("[SlideShowEventHandler] Live slideshow handshake deferred until the active session is ready.");
                    return;
                }

                _session.CurrentSlideIndex = Wn.View.Slide.SlideIndex;
                _lastObservedSlideIndex = _session.CurrentSlideIndex;
                _session.UpdateInkRoutingHint(_lastObservedSlideIndex, 0);

                _inkOverlay.FrameStore.Clear();

                IntPtr hwnd = (IntPtr)Wn.HWND;
                var setup = Wn.Presentation.PageSetup;
                _inkOverlay.AttachToWindow(hwnd, setup.SlideWidth, setup.SlideHeight);
                System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] Overlay attached to window {hwnd}.");

                _session.InitializeCurrentSlideOverlay(Wn);
                System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] Current slide initialized: slide={_session.CurrentSlideIndex}.");

                // Wire both animation click events
                try { _app.SlideShowNextBuild += OnSlideShowNextBuild; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] Failed to wire SlideShowNextBuild: {ex.Message}");
                }
                try { _app.SlideShowNextClick += OnSlideShowNextClick; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] Failed to wire SlideShowNextClick: {ex.Message}");
                }

                // Start polling fallback timer (500ms interval, dedup guard prevents spam)
                _pollTimer = new System.Threading.Timer(_ =>
                {
                    if (!IsSlideShowActive()) return;
                    try
                    {
                        var windows = _app.SlideShowWindows;
                        if (windows.Count > 0)
                            BroadcastFrame(windows[1]);
                    }
                    catch { }
                }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

                _session.OnSessionBegin(Wn);
                BroadcastFrame(Wn);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] OnSlideShowBegin error: {ex}");
            }
        }

        public void OnSlideShowNextSlide(PowerPoint.SlideShowWindow Wn)
        {
            // Guard: exit early if slideshow already ended (prevents COMException — Bug 1 fix)
            if (!IsSlideShowActive() || !IsForActivePresentation(Wn)) return;

            try
            {
                // Re-snap overlay geometry in case the slideshow window moved/resized
                try
                {
                    IntPtr hwnd = (IntPtr)Wn.HWND;
                    if (hwnd != IntPtr.Zero)
                        _inkOverlay.SnapToWindow(hwnd);
                }
                catch { /* best effort */ }

                // Track slide indices
                int newSlideIndex = 1;
                try { newSlideIndex = Wn.View.Slide.SlideIndex; }
                catch { }

                int previousSlideIndex = _lastObservedSlideIndex > 0
                    ? _lastObservedSlideIndex
                    : _session.CurrentSlideIndex;

                _lastObservedSlideIndex = newSlideIndex;
                _session.UpdateInkRoutingHint(newSlideIndex, 0);

                // Save overlay strokes for departing slide (local backtrack restoration)
                _inkOverlay.SaveStrokesForSlide(previousSlideIndex);

                // Session handles ink capture, unlock/advance, last-slide check
                int totalSlides = 0;
                try { totalSlides = Wn.Presentation.Slides.Count; }
                catch { }

                _session.OnSlideChanged(previousSlideIndex, newSlideIndex, totalSlides);

                // Restore overlay strokes for arriving slide (or clear if none saved)
                _inkOverlay.RestoreStrokesForSlide(newSlideIndex);

                // Broadcast the new frame (click resets to 0 on slide change)
                BroadcastFrame(Wn);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] OnSlideShowNextSlide error: {ex.Message}");
            }
        }

        /// <summary>
        /// Fires on each animation build (click) within a slide.
        /// </summary>
        private void OnSlideShowNextBuild(PowerPoint.SlideShowWindow Wn)
        {
            if (!IsSlideShowActive() || !IsForActivePresentation(Wn)) return;

            try
            {
                BroadcastFrame(Wn);
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] OnSlideShowNextBuild COM error: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] OnSlideShowNextBuild error: {ex.Message}");
            }
        }

        /// <summary>
        /// Fires on every presenter click regardless of whether a build animation is attached.
        /// Acts as a secondary trigger alongside SlideShowNextBuild.
        /// </summary>
        private void OnSlideShowNextClick(PowerPoint.SlideShowWindow Wn, PowerPoint.Effect nEffect)
        {
            if (!IsSlideShowActive() || !IsForActivePresentation(Wn)) return;
            try
            {
                BroadcastFrame(Wn);
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] OnSlideShowNextClick COM error: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] OnSlideShowNextClick error: {ex.Message}");
            }
        }

        public void OnSlideShowEnd(PowerPoint.Presentation Pres)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[SlideShowEventHandler] Slideshow ended.");

                // Stop polling timer first
                _pollTimer?.Dispose();
                _pollTimer = null;

                // Unhook both animation click events
                try { _app.SlideShowNextBuild -= OnSlideShowNextBuild; } catch { }
                try { _app.SlideShowNextClick -= OnSlideShowNextClick; } catch { }

                _session.OnSessionEnd(Pres);

                _inkOverlay.FrameStore.Clear();
                _inkOverlay.ClearCanvas();
                _inkOverlay.Detach();

                _lastBroadcastSlide = -1;
                _lastBroadcastClick = -1;
                _lastObservedSlideIndex = -1;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] OnSlideShowEnd error: {ex}");
            }
        }

        // ==================================================================
        // HELPERS
        // ==================================================================

        /// <summary>
        /// Reads the current (slideIndex, clickIndex) from the SlideShowWindow
        /// and forwards it to SessionManager.OnFrameAdvanced for mapping + broadcast.
        /// Deduplicates consecutive identical (slide, click) pairs.
        /// </summary>
        private void BroadcastFrame(PowerPoint.SlideShowWindow Wn)
        {
            try
            {
                if (!IsForActivePresentation(Wn)) return;

                int slideIndex = Wn.View.Slide.SlideIndex;
                int clickIndex = 0;

                try
                {
                    clickIndex = Wn.View.GetClickIndex();
                    // Single retry: GetClickIndex can return -1 before COM state settles
                    if (clickIndex < 0)
                    {
                        System.Threading.Thread.Sleep(30);
                        clickIndex = Wn.View.GetClickIndex();
                    }
                }
                catch { /* older PPT versions may not support GetClickIndex */ }

                if (clickIndex < 0) clickIndex = 0;

                bool isDuplicate;
                lock (_broadcastLock)
                {
                    isDuplicate = slideIndex == _lastBroadcastSlide && clickIndex == _lastBroadcastClick;
                    if (!isDuplicate)
                    {
                        _lastBroadcastSlide = slideIndex;
                        _lastBroadcastClick = clickIndex;
                    }
                }

                if (isDuplicate)
                    return;

                _lastObservedSlideIndex = slideIndex;
                _session.UpdateInkRoutingHint(slideIndex, clickIndex);

                System.Diagnostics.Debug.WriteLine(
                    $"[SlideShowEventHandler] BroadcastFrame: slide={slideIndex}, click={clickIndex}");

                _session.OnFrameAdvanced(slideIndex, clickIndex);
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SlideShowEventHandler] BroadcastFrame COM error: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks whether a slideshow is currently active. Returns false if the COM object is invalid.
        /// </summary>
        private bool IsSlideShowActive()
        {
            try
            {
                return _app.SlideShowWindows.Count > 0;
            }
            catch (COMException)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns false if the event is for a different open presentation.
        /// Prevents application-level events from firing against the wrong session.
        /// </summary>
        private bool IsForActivePresentation(PowerPoint.SlideShowWindow Wn)
        {
            try
            {
                return _session.IsPresentationActive(Wn.Presentation.FullName);
            }
            catch { return false; }
        }

        /// <summary>
        /// Resets the dedup guard so the next BroadcastFrame call is never suppressed.
        /// Called by SessionManager after a backfill loop completes.
        /// </summary>
        internal void ResetBroadcastDedup()
        {
            lock (_broadcastLock)
            {
                _lastBroadcastSlide = -1;
                _lastBroadcastClick = -1;
            }
        }
    }
}
