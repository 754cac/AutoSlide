import React, { useEffect, useLayoutEffect, useMemo, useState, useRef, useCallback } from 'react'
import { pdfjs } from 'react-pdf'
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr'
import { useNavigate, useParams } from 'react-router-dom';
import { getToken } from './utils/auth'
import { apiUrl } from './utils/api'
import {
  clampValue,
  getAspectRatioStyle,
  getPdfFitScale,
  PDF_VIEWER_MAX_SCALE,
  PDF_VIEWER_MIN_SCALE,
  PDF_VIEWER_MAX_ZOOM_MULTIPLIER,
  PDF_VIEWER_MIN_ZOOM_MULTIPLIER,
  PDF_VIEWER_ZOOM_STEP,
} from './utils/pdfViewer'
import { getAnnotatedPdfDownload } from './services/studentApi'

// Components
import ViewerToolbar from './components/toolbar/ViewerToolbar'
import SlideThumbnailBar from './components/sidebar/SlideThumbnailBar'
import ContentPanel from './components/sidebar/ContentPanel'
import SlideCanvas from './components/viewer/SlideCanvas'
import SolutionPageCanvas from './components/viewer/SolutionPageCanvas'
import MainLayout from './components/layout/MainLayout'
import useDocumentTitle from './hooks/useDocumentTitle'

// Configure pdf.js worker
pdfjs.GlobalWorkerOptions.workerSrc = `//unpkg.com/pdfjs-dist@${pdfjs.version}/build/pdf.worker.min.js`

const normalizeSessionId = (value) => {
  if (value === null || value === undefined) return null;
  const normalized = String(value).trim().replace(/-/g, '').toLowerCase();
  return normalized || null;
};

/**
 * LiveSessionViewer - Main component for viewing live classroom presentations.
 * Handles SignalR real-time sync, slide navigation with progressive unlock,
 * presenter ink overlay, annotations, and live transcript display.
 */
export default function LiveSessionViewer() {
  const DEBUG = import.meta.env.DEV;  // development logging toggle
  const navigate = useNavigate();
  const { presentationId: routePresentationId, sessionId: routeSessionId } = useParams();
  // --- State ---
  const [pdfFile, setPdfFile] = useState(null)
  const [presentationId, setPresentationId] = useState(null)
  const [numPages, setNumPages] = useState(null)
  const [pageNumber, setPageNumber] = useState(1)
  const [zoomMultiplier, setZoomMultiplier] = useState(1)
  const [pdfPageSize, setPdfPageSize] = useState(null)
  const [viewerViewport, setViewerViewport] = useState({ width: 0, height: 0 })
  
  // "Live" features state - TRUE PROGRESSIVE UNLOCK: use Set instead of high-water-mark
  const [unlockedSlides, setUnlockedSlides] = useState(new Set([1])) // Set of unlocked slide numbers

  // === SECURE SLIDE DELIVERY ===
  // currentSlideUrl: signed URL for the currently displayed single-page PDF.
  // When set, SlideCanvas renders this URL at pageNumber=1 instead of the full pdfFile.
  const [currentSlideUrl, setCurrentSlideUrl] = useState(null)
  const [isLive, setIsLive] = useState(true)
  const [connectionState, setConnectionState] = useState('disconnected') // connected, reconnecting, disconnected
  const [lastUpdated, setLastUpdated] = useState(new Date().toLocaleTimeString())
  const [sessionTitle, setSessionTitle] = useState("Waiting for presentation...")
  const connectionRef = useRef(null);
  const slideUrlCacheRef = useRef(new Map());
  const slideUrlInFlightRef = useRef(new Map());
  const lastInkRequestRef = useRef({ sid: null, page: null, at: 0 });
  const INK_REQUEST_COOLDOWN_MS = 1500;

  // Stable session ID for API calls (resolved once from URL).
  // This must NOT be overwritten by SyncState's presentationId (which is the
  // PresentationStore key in no-dash format ??wrong for /api/sessions endpoints).
  const sessionApiIdRef = useRef(null);

  // UI State
  const [isLeftSidebarOpen, setIsLeftSidebarOpen] = useState(true)
  const [isRightSidebarOpen, setIsRightSidebarOpen] = useState(true) // Default open on desktop viewer
  const [toastMessage, setToastMessage] = useState(null); // For feedback
  const viewerStageRef = useRef(null);
  const panDragRef = useRef(null);
  const previousScaleRef = useRef(null);
  const [isPanning, setIsPanning] = useState(false);

  // Annotation State
  const [annotations, setAnnotations] = useState({}) // { pageNum: [Annotation] }
  const [redoStack, setRedoStack] = useState({}) // { pageNum: [Annotation] }
  const [annotationTool, setAnnotationTool] = useState('cursor') // cursor, text, note
  const [toolColor, setToolColor] = useState('#ffeb3b') // default yellow
  const [toolOpacity, setToolOpacity] = useState(1)
  const [sessionEnded, setSessionEnded] = useState(false)
  const [downloadLinks, setDownloadLinks] = useState({
    originalPdf: null,
    originalPptx: null,
    inkArtifactPdf: null,
    inkedPdf: null,
    inkedWithSolutionsPdf: null,
    inkedPptx: null,
    annotatedPdf: null,
    annotatedPptx: null
  });
  const [isDownloadingAnnotatedPdf, setIsDownloadingAnnotatedPdf] = useState(false);
  const [summary, setSummary] = useState(null)
  // Transcript state: keep an array of recent entries (text + timestamp)
  const [transcripts, setTranscripts] = useState([]);
  const maxTranscriptEntries = 5;

  useDocumentTitle(
    sessionTitle && sessionTitle !== 'Waiting for presentation...'
      ? `${sessionTitle} | AutoSlide`
      : 'Live Session | AutoSlide'
  );
  
  // Replay State
  const [isReplayMode, setIsReplayMode] = useState(false);
  const [replayTranscript, setReplayTranscript] = useState([]);
  const [replayDeckUrl, setReplayDeckUrl] = useState(null);
  const replayDeckUrlCacheRef = useRef<{ url: string; expiresAt: number } | null>(null);
  const replayDeckInFlightRef = useRef<Promise<string | null> | null>(null);
  
  // Real-time Ink Overlay State - now stores ink per slide: { slideIndex: [strokes] }
  const [allInk, setAllInk] = useState({});
  const [solutionPages, setSolutionPages] = useState([])
  const [solutionImageSizes, setSolutionImageSizes] = useState({})
  const [leftSidebarTab, setLeftSidebarTab] = useState('slides')
  const [selectedSolutionId, setSelectedSolutionId] = useState(null)

  const fetchReplayDeckUrl = useCallback(async (apiSessionId, options: { forceRefresh?: boolean } = {}) => {
    const { forceRefresh = false } = options;
    const token = getToken();
    if (!apiSessionId || !token) return null;

    const now = Date.now();
    const cached = replayDeckUrlCacheRef.current;

    if (!forceRefresh && cached && cached.url && cached.expiresAt > now + 5_000) {
      return cached.url;
    }

    if (!forceRefresh && replayDeckInFlightRef.current) {
      return replayDeckInFlightRef.current;
    }

    const request = (async () => {
      const response = await fetch(apiUrl(`/api/sessions/${apiSessionId}/replay-deck`), {
        headers: { Authorization: `Bearer ${token}` }
      });

      if (!response.ok) {
        if (response.status === 403 || response.status === 404) return null;
        throw new Error(`Replay deck fetch failed (${response.status})`);
      }

      const payload = await response.json();
      const url = payload?.url;
      if (!url) return null;

      replayDeckUrlCacheRef.current = {
        url,
        expiresAt: Date.now() + 280_000
      };

      return url;
    })();

    replayDeckInFlightRef.current = request;
    try {
      return await request;
    } finally {
      replayDeckInFlightRef.current = null;
    }
  }, []);
  
  const fetchSignedSlideUrl = useCallback(async (apiSessionId, targetPage, options: { forceRefresh?: boolean } = {}) => {
    const { forceRefresh = false } = options;
    const token = getToken();
    if (!apiSessionId || !targetPage || !token) return null;

    const cacheKey = `${apiSessionId}:${targetPage}`;
    const now = Date.now();
    const cached = slideUrlCacheRef.current.get(cacheKey);

    if (!forceRefresh && cached && cached.expiresAt > now + 5_000) {
      return cached.url;
    }

    if (!forceRefresh && slideUrlInFlightRef.current.has(cacheKey)) {
      return slideUrlInFlightRef.current.get(cacheKey);
    }

    const request = (async () => {
      const response = await fetch(apiUrl(`/api/sessions/${apiSessionId}/slide/${targetPage}`), {
        headers: { Authorization: `Bearer ${token}` }
      });

      if (!response.ok) {
        if (response.status === 403) return null;
        throw new Error(`Slide URL fetch failed (${response.status})`);
      }

      const payload = await response.json();
      const url = payload?.url;
      if (!url) return null;

      // Slide URL TTL is 120s server-side. Keep local cache shorter to avoid stale links.
      slideUrlCacheRef.current.set(cacheKey, {
        url,
        expiresAt: Date.now() + 100_000
      });

      return url;
    })();

    slideUrlInFlightRef.current.set(cacheKey, request);
    try {
      return await request;
    } finally {
      slideUrlInFlightRef.current.delete(cacheKey);
    }
  }, []);

  const fetchSessionDownloads = useCallback(async (apiSessionId) => {
    const token = getToken();
    if (!apiSessionId || !token) return null;

    try {
      const response = await fetch(apiUrl(`/api/sessions/${apiSessionId}/downloads`), {
        headers: { Authorization: `Bearer ${token}` }
      });

      if (!response.ok) {
        if (response.status === 403) return null;
        throw new Error(`Downloads fetch failed (${response.status})`);
      }

      const payload = await response.json();
      return payload?.downloads || null;
    } catch (error) {
      console.warn('[Downloads] Failed to fetch session downloads:', error);
      return null;
    }
  }, []);

  const fitScale = useMemo(
    () => getPdfFitScale(pdfPageSize, viewerViewport),
    [pdfPageSize, viewerViewport]
  );

  const slideScale = useMemo(
    () => clampValue(
      fitScale * zoomMultiplier,
      PDF_VIEWER_MIN_SCALE,
      PDF_VIEWER_MAX_SCALE
    ),
    [fitScale, zoomMultiplier]
  );

  const slideAspectRatio = useMemo(() => getAspectRatioStyle(pdfPageSize), [pdfPageSize]);

  const selectedSolution = useMemo(
    () => solutionPages.find((page) => page.solutionPageId === selectedSolutionId) || null,
    [solutionPages, selectedSolutionId]
  );

  const selectedSolutionSize = useMemo(
    () => (selectedSolutionId ? solutionImageSizes[selectedSolutionId] || null : null),
    [selectedSolutionId, solutionImageSizes]
  );

  const solutionFitScale = useMemo(
    () => getPdfFitScale(selectedSolutionSize, viewerViewport),
    [selectedSolutionSize, viewerViewport]
  );

  const solutionScale = useMemo(
    () => clampValue(
      solutionFitScale * zoomMultiplier,
      PDF_VIEWER_MIN_SCALE,
      PDF_VIEWER_MAX_SCALE
    ),
    [solutionFitScale, zoomMultiplier]
  );

  const viewingSolution = !isReplayMode && leftSidebarTab === 'solutions' && !!selectedSolution;

  const renderedPageSize = useMemo(() => {
    if (!pdfPageSize) return null;

    return {
      width: pdfPageSize.width * slideScale,
      height: pdfPageSize.height * slideScale,
    };
  }, [pdfPageSize, slideScale]);

  const renderedSolutionSize = useMemo(() => {
    if (!selectedSolutionSize) return null;

    return {
      width: selectedSolutionSize.width * solutionScale,
      height: selectedSolutionSize.height * solutionScale,
    };
  }, [selectedSolutionSize, solutionScale]);

  const activeSurfaceSize = viewingSolution ? renderedSolutionSize : renderedPageSize;
  const activeScale = viewingSolution ? solutionScale : slideScale;

  const overflowMode = useMemo(() => {
    if (!activeSurfaceSize || !viewerViewport.width || !viewerViewport.height) {
      return 'center';
    }

    const wider = activeSurfaceSize.width > viewerViewport.width + 2;
    const taller = activeSurfaceSize.height > viewerViewport.height + 2;

    if (wider && taller) return 'both';
    if (wider) return 'x';
    if (taller) return 'y';
    return 'center';
  }, [activeSurfaceSize, viewerViewport]);

  const stageOverflowMode = overflowMode;
  const isScrollableSurface = overflowMode !== 'center';

  const handleSlidePageLoadSuccess = useCallback((pageSize) => {
    if (pageSize?.width && pageSize?.height) {
      setPdfPageSize(pageSize);
    }
  }, []);

  const handleSolutionImageLoad = useCallback((solutionPageId, imageSize) => {
    if (!solutionPageId || !imageSize?.width || !imageSize?.height) {
      return;
    }

    setSolutionImageSizes((previous) => {
      const current = previous[solutionPageId];
      if (current?.width === imageSize.width && current?.height === imageSize.height) {
        return previous;
      }

      return {
        ...previous,
        [solutionPageId]: imageSize,
      };
    });
  }, []);

  const handleStagePointerDown = useCallback((event) => {
    if (!viewerStageRef.current || !isScrollableSurface || event.button !== 0) {
      return;
    }

    event.preventDefault();
    viewerStageRef.current.setPointerCapture(event.pointerId);
    panDragRef.current = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      startScrollLeft: viewerStageRef.current.scrollLeft,
      startScrollTop: viewerStageRef.current.scrollTop,
    };
    setIsPanning(true);
  }, [isScrollableSurface]);

  const handleStagePointerMove = useCallback((event) => {
    const drag = panDragRef.current;
    const stage = viewerStageRef.current;

    if (!drag || !stage || drag.pointerId !== event.pointerId) {
      return;
    }

    event.preventDefault();
    stage.scrollLeft = drag.startScrollLeft - (event.clientX - drag.startX);
    stage.scrollTop = drag.startScrollTop - (event.clientY - drag.startY);
  }, []);

  const endStagePan = useCallback((event) => {
    const drag = panDragRef.current;
    const stage = viewerStageRef.current;

    if (!drag || (event && drag.pointerId !== event.pointerId)) {
      return;
    }

    if (stage?.hasPointerCapture?.(drag.pointerId)) {
      stage.releasePointerCapture(drag.pointerId);
    }

    panDragRef.current = null;
    setIsPanning(false);
  }, []);

  useEffect(() => {
    if (!isScrollableSurface) {
      panDragRef.current = null;
      setIsPanning(false);
    }
  }, [isScrollableSurface]);

  useLayoutEffect(() => {
    const stage = viewerStageRef.current;
    const previousScale = previousScaleRef.current;
    previousScaleRef.current = activeScale;

    if (!stage || !isScrollableSurface || !activeSurfaceSize || !viewerViewport.width || !viewerViewport.height) {
      return;
    }

    if (previousScale === null || !Number.isFinite(previousScale) || previousScale <= 0 || previousScale === activeScale) {
      return;
    }

    const scaleRatio = activeScale / previousScale;
    if (!Number.isFinite(scaleRatio) || scaleRatio <= 0 || scaleRatio === 1) {
      return;
    }

    const frameId = requestAnimationFrame(() => {
      const currentStage = viewerStageRef.current;
      if (!currentStage) return;

      const nextCenterX = (currentStage.scrollLeft + currentStage.clientWidth / 2) * scaleRatio;
      const nextCenterY = (currentStage.scrollTop + currentStage.clientHeight / 2) * scaleRatio;
      const maxScrollLeft = Math.max(0, currentStage.scrollWidth - currentStage.clientWidth);
      const maxScrollTop = Math.max(0, currentStage.scrollHeight - currentStage.clientHeight);

      currentStage.scrollLeft = clampValue(
        nextCenterX - currentStage.clientWidth / 2,
        0,
        maxScrollLeft
      );
      currentStage.scrollTop = clampValue(
        nextCenterY - currentStage.clientHeight / 2,
        0,
        maxScrollTop
      );
    });

    return () => cancelAnimationFrame(frameId);
  }, [activeScale, activeSurfaceSize, isScrollableSurface, viewerViewport.width, viewerViewport.height]);

  useEffect(() => {
    previousScaleRef.current = null;
  }, [viewingSolution, selectedSolutionId, pageNumber]);

  const currentPageInk = allInk[pageNumber];

  useEffect(() => {
    const conn = connectionRef.current;
    const sid = sessionApiIdRef.current;

    // Only request ink state when connected and the current page's ink is missing locally.
    // Do not retrigger on unrelated ink updates.
    if (
      !conn ||
      !sid ||
      !pageNumber ||
      isReplayMode ||
      connectionState !== 'connected' ||
      currentPageInk !== undefined
    ) {
      return;
    }

    const now = Date.now();
    const lastRequest = lastInkRequestRef.current;
    if (
      lastRequest.sid === sid &&
      lastRequest.page === pageNumber &&
      now - lastRequest.at < INK_REQUEST_COOLDOWN_MS
    ) {
      return;
    }

    lastInkRequestRef.current = { sid, page: pageNumber, at: now };

    conn.invoke("RequestInkState", sid)
      .catch(err => console.warn("RequestInkState on page navigation failed:", err));
  }, [pageNumber, connectionState, currentPageInk, isReplayMode]);

  useEffect(() => {
    if (!viewerStageRef.current) return;

    let frameId = 0;

    const measureViewport = () => {
      if (!viewerStageRef.current) return;

      const rect = viewerStageRef.current.getBoundingClientRect();
      if (rect.width > 0 && rect.height > 0) {
        setViewerViewport((prev) => (
          prev.width === rect.width && prev.height === rect.height
            ? prev
            : { width: rect.width, height: rect.height }
        ));
      }
    };

    measureViewport();

    const observer = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (!entry) return;

      const { width, height } = entry.contentRect;
      if (width <= 0 || height <= 0) return;

      if (frameId) {
        cancelAnimationFrame(frameId);
      }

      frameId = requestAnimationFrame(() => {
        setViewerViewport((prev) => (
          prev.width === width && prev.height === height
            ? prev
            : { width, height }
        ));
      });
    });

    observer.observe(viewerStageRef.current);

    return () => {
      observer.disconnect();
      if (frameId) {
        cancelAnimationFrame(frameId);
      }
    };
  }, []);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    // The route segment contains the presentationId, not the real SignalR key.
    // The proper group ID is supplied via ?sessionId=<GUID>.
    const querySid = params.get('sessionId');
    const normalizedQuerySid = normalizeSessionId(querySid);
    const normalizedRouteSid = normalizeSessionId(routePresentationId || routeSessionId);
    const sid = normalizedQuerySid || normalizedRouteSid;

    if (normalizedQuerySid) {
      if (normalizedRouteSid && normalizedQuerySid !== normalizedRouteSid) {
        console.log('[SignalR] sessionId query param overrides route param', { querySid: normalizedQuerySid, routeParam: normalizedRouteSid });
        }
      console.log(`[SignalR] using sessionId from query param: ${normalizedQuerySid}`);
    } else if (normalizedRouteSid) {
      console.log(`[SignalR] using sessionId from route param: ${normalizedRouteSid}`);
    } else {
        console.warn('[SignalR] sessionId query parameter missing; falling back to route segment', sid);
    }

    const pdfParam = params.get('pdf');
    if (sid) sessionApiIdRef.current = sid;
    if (DEBUG) console.log('[Init] sessionApiIdRef set to:', sessionApiIdRef.current);
    if (pdfParam) setPdfFile(pdfParam);

    if (!sid) return;

    // ── Create SignalR connection ──
    const connection = new HubConnectionBuilder()
      .withUrl(apiUrl('/hubs/presentation'), {
            transport: HttpTransportType.WebSockets,
            skipNegotiation: true,
        })
        .configureLogging(LogLevel.Information)
        .withAutomaticReconnect()
        .build();

    connectionRef.current = connection;

    // ── Register all event handlers BEFORE starting ──
    connection.on("SlideUnlocked", (data) => {
      console.log('[SignalR] SlideUnlocked received:', data);
        if (Array.isArray(data.unlockedSlides) && data.unlockedSlides.length > 0) {
            setUnlockedSlides(new Set(data.unlockedSlides));
        } else if (Array.isArray(data.newlyUnlocked)) {
            setUnlockedSlides(prev => {
                const next = new Set(prev);
                data.newlyUnlocked.forEach(s => next.add(s));
                return next;
            });
        }
        if (data.totalSlides > 0) setNumPages(prev => Math.max(prev, data.totalSlides));
        setLastUpdated(new Date().toLocaleTimeString());
    });

    connection.on("SlideAdvanced", (data) => {
        const idx = data.slideIndex || data.newIndex;
        if (DEBUG) console.log('[SignalR] SlideAdvanced received:', { slideIndex: idx, unlockedSlides: data.unlockedSlides });
        if (idx > 0) {
            setPageNumber(idx);
            if (Array.isArray(data.unlockedSlides) && data.unlockedSlides.length > 0) {
                setUnlockedSlides(new Set(data.unlockedSlides));
            } else {
          // Keep progressive unlock strict: do not infer 1..idx high-water unlock.
                setUnlockedSlides(prev => {
                    const next = new Set(prev);
            next.add(idx);
                    return next;
                });
            }
            if (data.totalSlides > 0) setNumPages(prev => Math.max(prev, data.totalSlides));
            setLastUpdated(new Date().toLocaleTimeString());
        }
    });

    connection.on("SyncState", (state) => {
        try {
            if (state?.presentationTitle) setSessionTitle(state.presentationTitle);
            if (state?.presentationId && !presentationId) {
            setPresentationId(normalizeSessionId(state.presentationId));
            }
            if (Array.isArray(state?.unlockedSlides) && state.unlockedSlides.length > 0) {
                setUnlockedSlides(new Set(state.unlockedSlides));
            }
            if (typeof state?.currentSlide === 'number' && state.currentSlide > 0) {
                setPageNumber(state.currentSlide);
            }
            setLastUpdated(new Date().toLocaleTimeString());
        } catch (e) {
            console.warn('Failed to apply SyncState', e);
        }
    });

        const applyReplayTranscript = async (sessionData) => {
          if (Array.isArray(sessionData?.transcript)) {
            setReplayTranscript(sessionData.transcript);
            return;
          }

          if (!sessionData?.transcriptUrl) {
            return;
          }

          try {
            const transcriptResponse = await fetch(sessionData.transcriptUrl);
            if (!transcriptResponse.ok) {
              return;
            }

            const archiveJson = await transcriptResponse.json();
            if (Array.isArray(archiveJson?.transcript)) {
              setReplayTranscript(archiveJson.transcript);
            }

            if (!sessionData?.summary && archiveJson?.summary) {
              setSummary(archiveJson.summary);
            }
          } catch (err) {
            console.warn('Failed to load transcript archive for replay mode:', err);
          }
        };

        const hydrateReplayDeck = async (apiSessionId, options: { forceRefresh?: boolean } = {}) => {
          const replayUrl = await fetchReplayDeckUrl(apiSessionId, options);
          if (replayUrl) {
            setReplayDeckUrl(replayUrl);
            return true;
          }

          setReplayDeckUrl(null);
          return false;
        };

    connection.on("SessionEnded", () => {
        if (DEBUG) console.log('[SignalR] SessionEnded received');
        setIsLive(false);
        setSessionEnded(true);
        setCurrentSlideUrl(null);
        setLeftSidebarTab('slides');
        setSelectedSolutionId(null);
        setSolutionPages([]);
        setSolutionImageSizes({});
        setUnlockedSlides(new Set(Array.from({ length: 9999 }, (_, i) => i + 1)));

        fetch(apiUrl(`/api/sessions/${sid}`), {
            headers: { Authorization: `Bearer ${localStorage.getItem('token')}` }
        })
          .then(async (res) => {
            const data = await res.json();
            if (data.summary) setSummary(data.summary);
            await applyReplayTranscript(data);
            if (data.downloads) setDownloadLinks(data.downloads);
            setIsReplayMode(true);
            setIsRightSidebarOpen(true);

            const replayReady = await hydrateReplayDeck(sid, { forceRefresh: true });
            if (!replayReady) {
              showToast('Replay deck is being prepared. Please retry in a few seconds.');
            }
          })
            .catch(err => console.warn("Failed to fetch replay data after end:", err));
    });

    // bake completion may happen a few seconds after session end
    connection.on("BakeCompleted", (data) => {
        if (data?.sessionId === sid) {
            if (DEBUG) console.log('[SignalR] BakeCompleted for', data.sessionId);
            // refresh the session metadata so downloadLinks are populated
            fetch(apiUrl(`/api/sessions/${sid}`), {
                headers: { Authorization: `Bearer ${localStorage.getItem('token')}` }
            })
                .then(res => res.json())
                .then(d => {
                    if (d.downloads) {
                        setDownloadLinks(d.downloads);
                        if (d.downloads.inkArtifactPdf) {
                          showToast("Ink artifact PDF is ready.");
                        } else {
                          showToast("Session files are updating — check downloads again shortly.");
                        }
                    }
                })
                .catch(e => console.warn('Failed to refresh download links after bake:', e));
        }
    });

    const handleTranscriptEvent = (data) => {
      const text = data.text || data.Text || (data.payload && (data.payload.text || data.payload.Text));
      const timestamp = data.timestamp || data.Timestamp || (data.payload && (data.payload.timestamp || data.payload.Timestamp)) || new Date().toISOString();
        if (text) {
            setTranscripts(prev => [...prev.slice(-(maxTranscriptEntries - 1)), { text, timestamp }]);
            setLastUpdated(new Date().toLocaleTimeString());
        }
    };

    connection.on("transcriptreceived", handleTranscriptEvent);

    connection.on("summary_update", (data) => {
        if (data?.summary) {
            setSummary(data.summary);
            showToast("New lecture summary available!");
        }
    });

    connection.on("InkStroke", (slideIndex, strokeData) => {
        console.log(`[SignalR] InkStroke received: slide=${slideIndex}, points=${strokeData?.points?.length}`);
        setAllInk(prev => ({
            ...prev,
            [slideIndex]: [...(prev[slideIndex] || []), strokeData]
        }));
        setLastUpdated(new Date().toLocaleTimeString());
    });

    connection.on("ClearInk", (slideIndex) => {
        console.log(`[SignalR] ClearInk received: slide=${slideIndex}`);
        setAllInk(prev => ({ ...prev, [slideIndex]: [] }));
        setLastUpdated(new Date().toLocaleTimeString());
    });

    connection.on("InkStateSync", (allSlidesInk) => {
        console.log("[SignalR] InkStateSync received:", allSlidesInk);
        if (allSlidesInk && typeof allSlidesInk === 'object') {
            
            // Normalize the incoming data (ensure keys are integers)
            const normalized = {};
            for (const [key, strokes] of Object.entries(allSlidesInk)) {
                normalized[parseInt(key, 10)] = strokes || [];
            }
            
            // Use functional state update to safely overwrite only the frames provided
            setAllInk(prev => {
                const nextState = { ...prev };
                for (const [frameKey, strokes] of Object.entries(normalized)) {
                    // The server's array is the single source of truth for this frame.
                    // Completely replace whatever we had for this frame with the server's data.
                    nextState[frameKey] = strokes;
                }
                return nextState;
            });
        }
    });

    connection.onreconnecting(() => setConnectionState('reconnecting'));
    connection.onreconnected(() => {
        setConnectionState('connected');
        connection.invoke("JoinSession", sid).catch(e => console.warn('Rejoin failed', e));
    });
    connection.onclose(() => setConnectionState('disconnected'));

    (async () => {
        const token = getToken();
        try {
            const res = await fetch(apiUrl(`/api/sessions/${sid}`), {
                headers: token ? { Authorization: `Bearer ${token}` } : {}
            });
            if (res.status === 403) { setSessionTitle('You are not enrolled in this course.'); return; }
            if (!res.ok) { setSessionTitle('Session not found or unavailable.'); return; }
            const sessionData = await res.json();
            const fetchedSessionId = normalizeSessionId(sessionData.id);
            if (fetchedSessionId && fetchedSessionId !== sid) {
              console.warn('[SignalR] fetched sessionId differs', { initial: sid, fetched: fetchedSessionId });
            }
            if (fetchedSessionId) {
              sessionApiIdRef.current = fetchedSessionId;
            }
            setPresentationId(fetchedSessionId || sid);
            setSessionTitle(sessionData.presentationTitle || 'Live Presentation');
            const totalSl = (sessionData.totalSlides > 0 ? sessionData.totalSlides : sessionData.slideCount) || 0;
            if (totalSl > 0) setNumPages(totalSl);
            if (sessionData.status === 2) {
              setIsLive(false);
                setSessionEnded(true);
                setIsReplayMode(true);
                setCurrentSlideUrl(null);
                setLeftSidebarTab('slides');
                setSelectedSolutionId(null);
                setSolutionPages([]);
                setSolutionImageSizes({});
                setUnlockedSlides(new Set(Array.from({ length: totalSl || 9999 }, (_, i) => i + 1)));
                setPageNumber(Math.max(sessionData.currentSlideIndex || 1, 1));
                if (sessionData.summary) setSummary(sessionData.summary);
                await applyReplayTranscript(sessionData);
                setIsRightSidebarOpen(true);
              if (sessionData.downloads) setDownloadLinks(sessionData.downloads);

                const replayReady = await hydrateReplayDeck(fetchedSessionId || sid);
                if (!replayReady) {
                  showToast('Replay deck is being prepared. Please retry in a few seconds.');
                }
                return;
            }
            if (sessionData.status === 1) {
                setIsLive(true);
                setIsReplayMode(false);
                setReplayDeckUrl(null);
                if (Array.isArray(sessionData.unlockedSlides) && sessionData.unlockedSlides.length > 0) {
                    setUnlockedSlides(new Set(sessionData.unlockedSlides));
                } else {
                    const unlocked = new Set();
                    for (let i = 1; i <= Math.max(sessionData.currentSlideIndex || 1, 1); i++) unlocked.add(i);
                    setUnlockedSlides(unlocked);
                }
                if (sessionData.currentSlideIndex > 0) setPageNumber(sessionData.currentSlideIndex);
            }
        } catch (err) {
            console.error('Failed to fetch session info', err);
            setSessionTitle('Session not found or ended');
            return;
        }
        try {
            await connection.start();
            setConnectionState('connected');
            console.log('Connected to SignalR');
            console.log(`[SignalR] JoinSession with sid=${sid}`);
            await connection.invoke("JoinSession", sid);
        } catch (err) {
            console.error('[SignalR] Connection failed:', err);
            setConnectionState('disconnected');
        }
    })();

    // ── Cleanup ──
    return () => { connection.stop(); };
  }, [routePresentationId, routeSessionId, fetchReplayDeckUrl]);

  // Auto-scroll thumbnail bar to active slide when entering replay mode
  useEffect(() => {
    if (!isReplayMode) return;
    const el = document.getElementById(`thumb-${pageNumber}`);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }, [isReplayMode, pageNumber]);

  // Poll session metadata a few times after session end in case BakeCompleted
  // was missed or took a while; this avoids forcing users to refresh the page.
  useEffect(() => {
    if (!sessionEnded) return;
    if (downloadLinks?.inkArtifactPdf) return; // already have link

    const interval = setInterval(async () => {
      try {
        const token = getToken();
        if (!token) return;
        const res = await fetch(apiUrl(`/api/sessions/${sessionApiIdRef.current}`), {
          headers: { Authorization: `Bearer ${token}` }
        });
        if (res.ok) {
          const data = await res.json();
          if (data.downloads) {
            setDownloadLinks(data.downloads);
            if (data.downloads.inkArtifactPdf) {
              showToast("Ink artifact PDF is ready.");
              clearInterval(interval);
            }
          }
        }
      } catch {
      }
    }, 3000);
    return () => clearInterval(interval);
  }, [sessionEnded, downloadLinks]);

  // helper to wrap setCurrentSlideUrl with logging
  const updateSlideUrl = (idx, url) => {
    if (DEBUG) {
      if (!url) console.warn(`[Canvas] ?��? No URL for slide ${idx} ??skipping canvas update`);
      else console.log(`[Canvas] Loading slide ${idx} onto canvas`);
    }
    setCurrentSlideUrl(url);
  };

  // === SECURE SLIDE DELIVERY: auto-fetch signed URL on page change ===
  // 150ms debounce allows backend DB write to commit before we request the slide URL.
  // This is a defence-in-depth safety net ??the primary fix is DB-before-broadcast on the backend.
  // === COMMENTED OUT FOR TESTING ===
  useEffect(() => {
    if (isReplayMode) return

    const apiId = sessionApiIdRef.current;
    if (!apiId || !pageNumber) return

    let cancelled = false
    const doFetch = async () => {
      if (cancelled) return
      if (DEBUG) console.log(`[Canvas] Effect triggered: loading slide ${pageNumber}, sessionApiId=${apiId}`);
      try {
        if (cancelled) return
        const nextUrl = await fetchSignedSlideUrl(apiId, pageNumber)
        if (!nextUrl) {
          showToast('Wait for instructor to unlock this slide')
          return
        }
        if (!cancelled) updateSlideUrl(pageNumber, nextUrl)
      } catch (e) {
        console.warn('[SecureSlide] Failed to fetch signed URL:', e)
      }
    };

    // TEMP: immediate fetch instead of 150ms timer
    doFetch();

    return () => { cancelled = true; }
  }, [pageNumber, fetchSignedSlideUrl, DEBUG, isReplayMode])

  // Callback passed to SlideThumbnailBar so it can lazily fetch each page's signed URL.
  // Uses sessionApiIdRef (stable URL-derived ID) ??NOT presentationId state which can
  // be overwritten by SyncState to the PresentationStore key (wrong format for this API).
  const getSlideUrl = useCallback(async (pageNum) => {
    const apiId = sessionApiIdRef.current;
    if (isReplayMode) return null
    if (DEBUG) console.log(`[Slide] Fetching URL for page ${pageNum}... sessionApiId=${apiId}`);
    if (!apiId) return null
    try {
      const url = await fetchSignedSlideUrl(apiId, pageNum)
      if (url && DEBUG) console.log(`[Slide] ??page ${pageNum} ??signed URL received`);
      // NOTE: do NOT call setNumPages or setTotalSlides here ??the
      // response includes the slide-level page count (always 1) which
      // would overwrite the correct session totalSlides that was set
      // during the initial session fetch. the only place total pages
      // should be populated is once after loading session metadata.
      return url
    } catch {
      return null
    }
  }, [fetchSignedSlideUrl, DEBUG, isReplayMode])

  const fetchSolutionPages = useCallback(async () => {
    if (isReplayMode) return

    const apiId = sessionApiIdRef.current
    const token = getToken()
    if (!apiId || !token) return

    try {
      const res = await fetch(apiUrl(`/api/sessions/${apiId}/solutions`), {
        headers: { Authorization: `Bearer ${token}` }
      })
      if (!res.ok) return

      const data = await res.json()
      const items = Array.isArray(data?.items) ? data.items : []
      setSolutionPages(items)
      setSelectedSolutionId(prev => (
        prev && items.some(item => item.solutionPageId === prev)
          ? prev
          : null
      ))
    } catch (err) {
      if (DEBUG) console.warn('Failed to fetch solution pages', err)
    }
  }, [isReplayMode, DEBUG])

  useEffect(() => {
    if (isReplayMode) {
      setSolutionPages([])
      setSelectedSolutionId(null)
      return
    }

    fetchSolutionPages()
    const interval = setInterval(fetchSolutionPages, 5000)
    return () => clearInterval(interval)
  }, [fetchSolutionPages, isReplayMode])

  // --- Handlers ---

  function handleSidebarTabChange(tab) {
    setLeftSidebarTab(tab)
    if (tab === 'slides') {
      setSelectedSolutionId(null)
    }
  }

  function openSolutionPage(solutionPageId) {
    setLeftSidebarTab('solutions')
    setSelectedSolutionId(solutionPageId)
  }

  // Helper: check if a specific slide is unlocked (true progressive unlock)
  function isSlideUnlocked(slideNum) {
    if (isReplayMode) return true;
    return unlockedSlides.has(slideNum);
  }

  function onDocumentLoadSuccess({ numPages }) {
    // Guard: never overwrite a known session total with a smaller value.
    // Signed single-page slide PDFs return numPages=1, which would clobber
    // the correct session total (e.g. 59) that was set from the session fetch.
    setNumPages(prev => {
      if (prev && numPages < prev) {
        if (DEBUG) console.log(`[Canvas] onLoadSuccess numPages=${numPages} ignored ??keeping session total ${prev}`);
        return prev;
      }
      return numPages;
    });
    // True progressive: ensure current page is unlocked, otherwise find first unlocked
    if (!isSlideUnlocked(pageNumber)) {
        // Find the first unlocked slide
        for (let i = 1; i <= numPages; i++) {
            if (isSlideUnlocked(i)) {
                setPageNumber(i);
                break;
            }
        }
    }
  }

  function changePage(offset) {
    const newPage = pageNumber + offset
    // True progressive: check if the specific page is unlocked
    if (newPage >= 1 && newPage <= (numPages || 1) && isSlideUnlocked(newPage)) {
      setSelectedSolutionId(null)
      setPageNumber(newPage)
    } else if (newPage >= 1 && newPage <= (numPages || 1)) {
      showToast("This slide is locked");
    }
  }

  function jumpToPage(pageNum) {
    // True progressive: check if the specific page is unlocked
    if (pageNum >= 1 && pageNum <= (numPages || 1) && isSlideUnlocked(pageNum)) {
      setSelectedSolutionId(null)
      setPageNumber(pageNum)
    } else {
        showToast("Wait for instructor to unlock this slide");
    }
  }

  function showToast(msg) {
      setToastMessage(msg);
      setTimeout(() => setToastMessage(null), 3000);
  }

  function retryConnection() {
      if (connectionRef.current && connectionState === 'disconnected') {
          setConnectionState('reconnecting');
          connectionRef.current.start()
            .then(() => {
                setConnectionState('connected');
                const params = new URLSearchParams(window.location.search);
                const querySid = params.get('sessionId');
        const sid = normalizeSessionId(querySid || routePresentationId || routeSessionId);
        if (normalizeSessionId(querySid)) {
                    console.log('[SignalR] retry JoinSession with sid=', sid, '(from query param)');
                } else {
                    console.warn('[SignalR] retry: sessionId query parameter missing; using route value', sid);
                }
                if (sid) {
                    connectionRef.current.invoke("JoinSession", sid);
                }
            })
            .catch(err => {
                console.error("Retry failed", err);
                setConnectionState('disconnected');
            });
      }
  }


  function zoomIn() {
    setZoomMultiplier((current) => clampValue(current * PDF_VIEWER_ZOOM_STEP, PDF_VIEWER_MIN_ZOOM_MULTIPLIER, PDF_VIEWER_MAX_ZOOM_MULTIPLIER));
  }

  function zoomOut() {
    setZoomMultiplier((current) => clampValue(current / PDF_VIEWER_ZOOM_STEP, PDF_VIEWER_MIN_ZOOM_MULTIPLIER, PDF_VIEWER_MAX_ZOOM_MULTIPLIER));
  }

  function fitPage() {
    setZoomMultiplier(1);
  }

  function toggleFullScreen() {
    if (!document.fullscreenElement) {
      document.documentElement.requestFullscreen().catch(err => {
        console.error(`Error attempting to enable full-screen mode: ${err.message} (${err.name})`);
      });
    } else {
      if (document.exitFullscreen) {
        document.exitFullscreen();
      }
    }
  }

  // Returns the best available session ID for fallback download URLs.
  // Prefers the stable API ref, falls back to the route param.
  function getEffectiveId() {
    return normalizeSessionId(sessionApiIdRef.current || presentationId || routePresentationId || routeSessionId);
  }

  // 1. Original PDF
  function handleDownloadPdf() {
    if (downloadLinks?.originalPdf) {
        window.open(downloadLinks.originalPdf, '_blank');
        return;
    }
    const sid = getEffectiveId();
    if (sid) {
        window.open(apiUrl(`/download/${sid}/pdf`), '_blank');
    } else {
        showToast("Original PDF is not available.");
    }
  }

  // 2. Original PPTX
  function handleDownloadPptx() {
    if (downloadLinks?.originalPptx) {
        window.open(downloadLinks.originalPptx, '_blank');
        return;
    }
    const sid = getEffectiveId();
    if (sid) {
        window.open(apiUrl(`/download/${sid}/pptx`), '_blank');
    } else {
        showToast("Original PPTX is not available.");
    }
  }

  // 3. Inked PDF (Baked with annotations)
  function handleDownloadAnnotated() {
    if (downloadLinks?.annotatedPdf) {
        window.open(downloadLinks.annotatedPdf, '_blank', 'noopener,noreferrer');
        return;
    }
    if (isDownloadingAnnotatedPdf) {
        return;
    }

    const sid = getEffectiveId();
    if (!sid) {
        showToast('Session identifier is unavailable.');
        return;
    }

    setIsDownloadingAnnotatedPdf(true);
    getAnnotatedPdfDownload(sid)
      .then((data) => {
        if (!data?.url) {
          throw new Error('Unable to prepare annotated PDF.');
        }

        setDownloadLinks((prev) => ({
          ...prev,
          annotatedPdf: data.url
        }));

        window.open(data.url, '_blank', 'noopener,noreferrer');
      })
      .catch((error) => {
        if (error?.status === 404) {
          showToast('Annotated PDF is not available yet.');
          return;
        }

        showToast(error?.message || 'Unable to prepare annotated PDF.');
      })
      .finally(() => {
        setIsDownloadingAnnotatedPdf(false);
      });
  }

  async function handleDownloadInkArtifactPdf() {
    const sid = getEffectiveId();
    if (!sid) {
      showToast("Session identifier is unavailable.");
      return;
    }

    if (downloadLinks?.inkArtifactPdf) {
      window.open(downloadLinks.inkArtifactPdf, '_blank');
      return;
    }

    const token = getToken();
    if (!token) {
      showToast("Please sign in to download ink artifact PDF.");
      return;
    }

    try {
      const res = await fetch(apiUrl(`/api/sessions/${sid}/exports/ink-artifact`), {
        headers: { Authorization: `Bearer ${token}` }
      });

      if (res.ok) {
        const data = await res.json();
        if (data?.url) {
          setDownloadLinks(prev => ({ ...prev, inkArtifactPdf: data.url }));
          window.open(data.url, '_blank');
          return;
        }
      }

      if (res.status === 404) {
        showToast("Ink artifact PDF is still generating. Please try again in a few seconds.");
      } else {
        showToast("Unable to fetch ink artifact PDF download link.");
      }
    } catch (err) {
      console.warn('Failed to download ink artifact PDF:', err);
      showToast("Unable to fetch ink artifact PDF download link.");
    }
  }

  // 4. Inked PPTX (Baked with annotations)
  async function handleDownloadAnnotatedPptx() {
    const sid = getEffectiveId();
    if (!sid) {
      showToast("Session identifier is unavailable.");
      return;
    }

    const directUrl = downloadLinks?.annotatedPptx || downloadLinks?.inkedPptx;

    const token = getToken();
    if (!token) {
      if (directUrl) {
        console.info("[Frontend] Annotated PPTX download response", { sessionId: sid, source: 'cached-no-token', ok: true });
        window.open(directUrl, '_blank');
        return;
      }

      showToast("Please sign in to download annotated PPTX.");
      return;
    }

    const endpoint = apiUrl(`/api/sessions/${sid}/exports/annotated-pptx`);
    console.info("[Frontend] Annotated PPTX download request", { sessionId: sid, endpoint });

    try {
      const res = await fetch(endpoint, {
        headers: { Authorization: `Bearer ${token}` }
      });

      console.info("[Frontend] Annotated PPTX download response", {
        sessionId: sid,
        status: res.status,
        ok: res.ok
      });

      if (!res.ok) {
        const text = await res.text();
        console.warn("[Frontend] Annotated PPTX download failed", { sessionId: sid, status: res.status, body: text });
        if (directUrl) {
          window.open(directUrl, '_blank');
          return;
        }
        showToast("Annotated PPTX is not available yet.");
        return;
      }

      const data = await res.json();
      if (!data?.url) {
        showToast("Annotated PPTX URL is not available yet.");
        return;
      }

      setDownloadLinks(prev => ({
        ...prev,
        annotatedPptx: data.url
      }));

      window.open(data.url, '_blank');
    } catch (err) {
      console.error("[Frontend] Annotated PPTX download request error", { sessionId: sid, error: err });
      showToast("Unable to fetch annotated PPTX download link.");
    }
  }

  async function requestSummary() {
    const sid = getEffectiveId();
    if (!sid) {
      showToast("Session identifier is unavailable.");
      return;
    }

    try {
      const token = getToken();
      const headers = token ? { Authorization: `Bearer ${token}` } : {};
      const response = await fetch(apiUrl(`/api/sessions/${sid}`), {
        headers
      });

      if (!response.ok) {
        showToast("Unable to load summary right now.");
        return;
      }

      const sessionData = await response.json();
      if (sessionData?.summary) {
        setSummary(sessionData.summary);
        return;
      }

      if (!sessionEnded) {
        showToast("Summary will be available after the session ends.");
        return;
      }

      // Queue summary generation and poll until it becomes available.
      const triggerResponse = await fetch(apiUrl(`/api/debug/sessions/${sid}/summary`), {
        method: 'POST',
        headers
      });

      if (!(triggerResponse.ok || triggerResponse.status === 202)) {
        showToast("Unable to start summary generation right now.");
        return;
      }

      // OpenRouter responses can take longer; poll for up to ~2 minutes.
      const maxAttempts = 24;
      const pollIntervalMs = 5000;
      const maxWaitSeconds = Math.round((maxAttempts * pollIntervalMs) / 1000);
      showToast(`Summary generation started. Checking for updates (up to ${maxWaitSeconds}s)...`);
      for (let attempt = 0; attempt < maxAttempts; attempt++) {
        // eslint-disable-next-line no-await-in-loop
        await new Promise((resolve) => setTimeout(resolve, pollIntervalMs));

        // eslint-disable-next-line no-await-in-loop
        const pollResponse = await fetch(apiUrl(`/api/sessions/${sid}`), { headers });
        if (!pollResponse.ok) {
          continue;
        }

        // eslint-disable-next-line no-await-in-loop
        const pollData = await pollResponse.json();
        if (pollData?.summary) {
          setSummary(pollData.summary);
          showToast("Summary is ready.");
          return;
        }
      }

      showToast("Summary is still being prepared after 2 minutes. Please try again shortly.");
    } catch (err) {
      console.warn('Failed to load summary:', err);
      showToast("Unable to load summary right now.");
    }
  }

  // Annotated PDF download removed (feature disabled)
  // function handleDownloadAnnotatedPdf() { /* removed */ }


  // Annotation Handlers
  function addAnnotation(pageNum, annotation) {
    setAnnotations(prev => ({
      ...prev,
      [pageNum]: [...(prev[pageNum] || []), annotation]
    }));
    setRedoStack(prev => ({
      ...prev,
      [pageNum]: []
    }));
  }

  function updateAnnotation(pageNum, id, updates) {
    setAnnotations(prev => ({
      ...prev,
      [pageNum]: (prev[pageNum] || []).map(ann => ann.id === id ? { ...ann, ...updates } : ann)
    }))
  }

  function deleteAnnotation(pageNum, id) {
    setAnnotations(prev => ({
      ...prev,
      [pageNum]: (prev[pageNum] || []).filter(ann => ann.id !== id)
    }))
  }

  function undoAnnotation(pageNum) {
    const pageAnns = annotations[pageNum] || [];
    if (pageAnns.length === 0) return;
    
    const lastAnn = pageAnns[pageAnns.length - 1];
    
    setAnnotations(prev => ({
      ...prev,
      [pageNum]: prev[pageNum].slice(0, -1)
    }));
    
    setRedoStack(prev => ({
      ...prev,
      [pageNum]: [...(prev[pageNum] || []), lastAnn]
    }));
  }

  function redoAnnotation(pageNum) {
    const pageStack = redoStack[pageNum] || [];
    if (pageStack.length === 0) return;
    
    const annToRestore = pageStack[pageStack.length - 1];
    
    setAnnotations(prev => ({
      ...prev,
      [pageNum]: [...(prev[pageNum] || []), annToRestore]
    }));
    
    setRedoStack(prev => ({
      ...prev,
      [pageNum]: prev[pageNum].slice(0, -1)
    }));
  }

  function clearAnnotations(pageNum) {
    setAnnotations(prev => ({
      ...prev,
      [pageNum]: []
    }))
  }

  function exportAnnotations() {
    const dataStr = "data:text/json;charset=utf-8," + encodeURIComponent(JSON.stringify(annotations));
    const downloadAnchorNode = document.createElement('a');
    downloadAnchorNode.setAttribute("href", dataStr);
    downloadAnchorNode.setAttribute("download", "annotations.json");
    document.body.appendChild(downloadAnchorNode); // required for firefox
    downloadAnchorNode.click();
    downloadAnchorNode.remove();
  }

  // Keyboard Shortcuts
  useEffect(() => {
    function handleKeyDown(e) {
      // Ignore if typing in an input
      if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;

      switch (e.key) {
        case 'ArrowLeft':
        case 'PageUp':
          changePage(-1);
          break;
        case 'ArrowRight':
        case 'PageDown':
          changePage(1);
          break;
        case 'Home':
          jumpToPage(1);
          break;
        case 'End':
          // Jump to highest unlocked slide
          if (unlockedSlides.size > 0) {
            const maxUnlocked = Math.max(...unlockedSlides);
            jumpToPage(maxUnlocked);
          }
          break;
        case '+':
        case '=': // Handle + without shift
          zoomIn();
          break;
        case '-':
          zoomOut();
          break;
        default:
          break;
      }
    }

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [pageNumber, unlockedSlides, numPages]); // Re-bind when these change to ensure fresh state

  return (
    <div className="app-container">
      <ViewerToolbar 
        sessionTitle={sessionTitle}
        onHome={() => navigate('/')}
        isLive={isLive}
        isReplayMode={isReplayMode}
        currentPage={pageNumber}
        totalPages={numPages || 0}
        unlockedSlides={unlockedSlides}
        onPrev={() => changePage(-1)}
        onNext={() => changePage(1)}
        onJumpTo={jumpToPage}
        scale={zoomMultiplier}
        onZoomIn={zoomIn}
        onZoomOut={zoomOut}
        onFitPage={fitPage}
        onToggleFullScreen={toggleFullScreen}
        onToggleSidebar={() => setIsLeftSidebarOpen(!isLeftSidebarOpen)}
        onToggleRightSidebar={() => setIsRightSidebarOpen((open) => !open)}
        isRightSidebarOpen={isRightSidebarOpen}
        summary={summary}
      />

      <MainLayout>
        <SlideThumbnailBar 
          isOpen={isLeftSidebarOpen}
          numPages={numPages || 0}
          currentPage={pageNumber}
          unlockedSlides={unlockedSlides}
          onPageClick={jumpToPage}
          pdfFile={isReplayMode ? replayDeckUrl : pdfFile}
          getSlideUrl={isReplayMode ? undefined : getSlideUrl}
          isReplayMode={isReplayMode}
          solutionPages={solutionPages}
          activeTab={leftSidebarTab}
          onTabChange={handleSidebarTabChange}
          onSolutionClick={openSolutionPage}
          currentSolutionId={selectedSolutionId}
          slideAspectRatio={slideAspectRatio}
          showSolutionsTab={!isReplayMode}
        />
        
        <div className="main-content-area">
            <div
              className={`slide-viewer-stage slide-viewer-stage--${stageOverflowMode}${isScrollableSurface ? ' is-scrollable' : ''}${isPanning ? ' is-panning' : ''}`}
              ref={viewerStageRef}
              onPointerDown={handleStagePointerDown}
              onPointerMove={handleStagePointerMove}
              onPointerUp={endStagePan}
              onPointerCancel={endStagePan}
            >
              <div className="slide-viewer-stage__viewport">
              {viewingSolution ? (
                <SolutionPageCanvas
                  imageUrl={selectedSolution?.imageUrl}
                  alt={selectedSolution?.solutionPageId}
                  renderedSize={renderedSolutionSize}
                  onImageLoad={(imageSize) => {
                    if (!selectedSolution?.solutionPageId) {
                      return;
                    }

                    handleSolutionImageLoad(selectedSolution.solutionPageId, imageSize);
                  }}
                />
              ) : isReplayMode && !replayDeckUrl ? (
                <div className="loading-spinner">Preparing replay deck...</div>
              ) : (
                <SlideCanvas 
                    // Replay mode uses a single backend replay deck artifact URL.
                    // Live mode keeps secure per-slide signed URL delivery.
                    pdfFile={isReplayMode ? replayDeckUrl : (currentSlideUrl || pdfFile)}
                    pageNumber={isReplayMode ? pageNumber : (currentSlideUrl ? 1 : pageNumber)}
                    scale={slideScale}
                    onDocumentLoadSuccess={onDocumentLoadSuccess}
                    onPageLoadSuccess={handleSlidePageLoadSuccess}
                    onLoadError={(err) => console.error("PDF Load Error", err)}
                    onUrlExpired={async () => {
                      if (isReplayMode) {
                        // Replay deck URLs are short-lived signed URLs as well.
                        const replayUrl = await fetchReplayDeckUrl(sessionApiIdRef.current, { forceRefresh: true })
                        if (replayUrl) {
                          setReplayDeckUrl(replayUrl)
                        } else {
                          showToast('Replay deck is being prepared. Please retry in a few seconds.')
                        }
                        return
                      }

                      const url = await fetchSignedSlideUrl(sessionApiIdRef.current, pageNumber, { forceRefresh: true })
                      if (url) setCurrentSlideUrl(url)
                    }}
                    // Annotation props
                    annotations={annotations[pageNumber] || []}
                    tool={annotationTool}
                    color={toolColor}
                    opacity={toolOpacity}
                    onAddAnnotation={(ann) => addAnnotation(pageNumber, ann)}
                    onUpdateAnnotation={(id, updates) => updateAnnotation(pageNumber, id, updates)}
                    onDeleteAnnotation={(id) => deleteAnnotation(pageNumber, id)}
                    // Real-time ink overlay from presenter - pass only current slide's ink
                    inkStrokes={isReplayMode ? [] : (allInk[pageNumber] || [])}
                />
              )}
                </div>
            </div>
            
        </div>

        <ContentPanel 
          isOpen={isRightSidebarOpen} 
            transcript={isReplayMode ? replayTranscript : transcripts}
          summary={summary}
            sessionEnded={sessionEnded}
            onRequestSummary={requestSummary}
            onDownloadPdf={handleDownloadPdf}
            onDownloadPptx={handleDownloadPptx}
            onDownloadInkOnlyPdf={handleDownloadInkArtifactPdf}
            onDownloadAnnotated={handleDownloadAnnotated}
            onDownloadAnnotatedPptx={handleDownloadAnnotatedPptx}
            isDownloadingAnnotatedPdf={isDownloadingAnnotatedPdf}
        />
      </MainLayout>
      {toastMessage && (
          <div className="toast-notification">
              {toastMessage}
          </div>
      )}

    </div>
  )
}
