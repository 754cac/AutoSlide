import React, { useMemo, useState, useEffect, useCallback } from "react";
import ReactDOM from "react-dom";
import { useParams, useNavigate, useSearchParams } from "react-router-dom";
import Papa from "papaparse";
import TeacherDashboardShell from "../components/instructor-workspace/TeacherDashboardShell";
import useSecureDownload from "../hooks/useSecureDownload";
import Card from "../components/ui/Card";
import StatusBadge from "../components/ui/StatusBadge";
import CourseWorkspaceHeader from "../components/course-workspace/CourseWorkspaceHeader";
import OverviewStatCard from "../components/course-workspace/OverviewStatCard";
import RosterPreviewCard from "../components/course-workspace/RosterPreviewCard";
import QuickUploadCard from "../components/course-workspace/QuickUploadCard";
import MaterialsUploadCard from "../components/course-workspace/MaterialsUploadCard";
import MaterialsLibraryCard from "../components/course-workspace/MaterialsLibraryCard";
import { formatHKT } from "../utils/dateUtils";
import useDocumentTitle from "../hooks/useDocumentTitle";
import PaginationControls from "../components/ui/PaginationControls";
import { apiUrl } from "../utils/api";
import {
  buildPagedPath,
  clampPage,
  getPaginationState,
  readPaginationHeaders,
} from "../utils/pagination";

const FA_USERS_ICON = <i className="fa-solid fa-users" aria-hidden="true" />;
const FA_HISTORY_ICON = <i className="fa-solid fa-clock-rotate-left" aria-hidden="true" />;
const FA_CALENDAR_ICON = <i className="fa-solid fa-calendar-days" aria-hidden="true" />;
const FA_UPLOAD_ICON = <i className="fa-solid fa-upload" aria-hidden="true" />;
const FA_TRASH_ICON = <i className="fa-solid fa-trash-can" aria-hidden="true" />;
const FA_DOWNLOAD_CHEVRON = <i className="fa-solid fa-chevron-down" aria-hidden="true" />;
const FA_PLAY_ICON = <i className="fa-solid fa-circle-play" aria-hidden="true" />;

function formatDateOnlyHKT(utcValue) {
  if (!utcValue) return "";

  return new Date(utcValue).toLocaleDateString("en-HK", {
    timeZone: "Asia/Hong_Kong",
    month: "short",
    day: "numeric",
    year: "numeric",
  });
}

function normalizeSessionStatus(statusValue) {
  const normalized = String(statusValue ?? "").toLowerCase();

  if (
    normalized === "1" ||
    normalized.includes("live") ||
    normalized.includes("active")
  ) {
    return "live";
  }

  if (normalized === "2" || normalized.includes("end")) {
    return "ended";
  }

  return "processing";
}

function getInitialMaterialReleaseAt() {
  const date = new Date();
  date.setMinutes(date.getMinutes() - date.getTimezoneOffset());
  return date.toISOString().slice(0, 16);
}

const COURSE_WORKSPACE_TABS = ["overview", "roster", "history", "materials"];
const MATERIAL_UPLOAD_MAX_BYTES = 5 * 1024 * 1024;
const MATERIAL_UPLOAD_MAX_LABEL = '5 MB';

function normalizeCourseWorkspaceTab(tabValue) {
  const normalized = String(tabValue || "").toLowerCase();
  return COURSE_WORKSPACE_TABS.includes(normalized) ? normalized : "overview";
}

function formatUploadErrorMessage(response, responseText, fallbackMessage) {
  if (!response) return fallbackMessage;
  if (response.status === 413) {
    return `File is too large. Please upload a file under ${MATERIAL_UPLOAD_MAX_LABEL}.`;
  }

  const trimmed = String(responseText || '').trim();
  if (trimmed && !trimmed.startsWith('<')) {
    try {
      const parsed = JSON.parse(trimmed);
      return parsed.error || parsed.message || parsed.title || parsed.detail || fallbackMessage;
    } catch {
      return trimmed;
    }
  }

  return fallbackMessage;
}

function isMaterialUploadTooLarge(file) {
  return Boolean(file && file.size > MATERIAL_UPLOAD_MAX_BYTES);
}

function getMaterialUploadSizeMessage() {
  return `File is too large. Please upload a file under ${MATERIAL_UPLOAD_MAX_LABEL}.`;
}

const SESSION_DOWNLOAD_DEFINITIONS = [
  {
    key: "inkArtifactPdf",
    label: "Download Ink Artifact PDF",
    extension: "pdf",
  },
  { key: "annotatedPdf", label: "Download Annotated PDF", extension: "pdf" },
  { key: "annotatedPptx", label: "Download Annotated PPTX", extension: "pptx" },
  { key: "originalPdf", label: "Download Original PDF", extension: "pdf" },
  { key: "originalPptx", label: "Download Original PPTX", extension: "pptx" },
];

function normalizeSessionDownloads(downloads) {
  if (!downloads) return null;

  const normalized = {
    inkArtifactPdf: downloads?.inkArtifactPdf || null,
    annotatedPdf: downloads?.annotatedPdf || downloads?.inkedPdf || null,
    annotatedPptx: downloads?.annotatedPptx || downloads?.inkedPptx || null,
    originalPdf: downloads?.originalPdf || null,
    originalPptx: downloads?.originalPptx || null,
  };

  return Object.values(normalized).some(Boolean) ? normalized : null;
}

function getSessionDownloadOptions(downloads) {
  if (!downloads) return [];

  return SESSION_DOWNLOAD_DEFINITIONS.map((definition) => ({
    ...definition,
    url: downloads[definition.key] || null,
  })).filter((option) => Boolean(option.url));
}

function mapHistorySession(row) {
  const id = row?.id || row?.Id || row?.sessionId || row?.SessionId || "";
  const startedAt = row?.startedAt || row?.StartedAt || null;
  const endedAt = row?.endedAt || row?.EndedAt || null;
  const durationSeconds = Number(
    row?.durationSeconds || row?.DurationSeconds || 0,
  );
  const slideCount = Number(row?.slideCount || row?.SlideCount || 0);
  const maxUnlockedSlide = Number(
    row?.maxUnlockedSlide ?? row?.MaxUnlockedSlide ?? 0,
  );
  const hasTranscript = Boolean(
    row?.hasTranscript || row?.HasTranscript || row?.transcriptUrl || row?.TranscriptUrl,
  );
  const hasSummary = Boolean(
    row?.hasSummary || row?.HasSummary || row?.summaryText || row?.SummaryText,
  );
  const transcriptUrl = row?.transcriptUrl || row?.TranscriptUrl || null;

  return {
    id,
    title: row?.presentationTitle || row?.PresentationTitle || "Session replay",
    startedAt,
    endedAt,
    durationMinutes:
      durationSeconds > 0
        ? Math.max(1, Math.round(durationSeconds / 60))
        : startedAt && endedAt
          ? Math.max(
              1,
              Math.round(
                (new Date(endedAt).getTime() - new Date(startedAt).getTime()) /
                  60000,
              ),
            )
          : 0,
    slidesCovered: maxUnlockedSlide,
    totalSlides: slideCount,
    status: normalizeSessionStatus(row?.status || row?.Status),
    hasTranscript,
    hasSummary,
    transcriptUrl,
    recordingUrl: null,
    downloads: null,
    downloadOptions: [],
  };
}

function mergeSessionAssets(session, assets: any = {}) {
  const downloads = normalizeSessionDownloads(assets.downloads);
  const downloadOptions = getSessionDownloadOptions(downloads);
  const recordingUrl =
    assets.recordingUrl ||
    (session.status === "ended" && session.id ? `/viewer/${session.id}` : null);

  return {
    ...session,
    status: assets.status || session.status,
    recordingUrl,
    downloads,
    downloadOptions,
    canReplay: Boolean(recordingUrl),
    canDownload: downloadOptions.length > 0,
  };
}

function SessionDownloadMenu({
  sessionId,
  sessionTitle,
  downloadFile,
  downloadOptions = [],
  canDownload,
  isOpen,
  anchorRect,
  onOpenMenu,
  onCloseMenu,
}) {
  const buttonRef = React.useRef(null);

  useEffect(() => {
    if (!isOpen) return undefined;

    function handleKeyDown(event) {
      if (event.key === "Escape") {
        onCloseMenu();
      }
    }

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [isOpen, onCloseMenu]);

  const handleTriggerClick = () => {
    if (!canDownload) return;

    const trigger = buttonRef.current;
    if (!trigger) return;
    onOpenMenu(sessionId, trigger);
  };

  const position = React.useMemo(() => {
    if (!anchorRect) return null;

    const menuWidth = Math.max(240, Math.min(340, window.innerWidth - 32));
    const estimatedHeight = Math.max(152, downloadOptions.length * 44 + 12);
    const spaceBelow = window.innerHeight - anchorRect.bottom;
    const spaceAbove = anchorRect.top;
    const openUpward = spaceBelow < estimatedHeight && spaceAbove > spaceBelow;
    const left = Math.max(
      16,
      Math.min(anchorRect.left, window.innerWidth - menuWidth - 16),
    );
    const top = openUpward
      ? Math.max(16, anchorRect.top - estimatedHeight - 8)
      : Math.min(
          window.innerHeight - estimatedHeight - 16,
          anchorRect.bottom + 8,
        );
    const maxHeight = openUpward
      ? Math.max(160, spaceAbove - 16)
      : Math.max(160, spaceBelow - 16);

    return {
      left,
      top,
      width: menuWidth,
      maxHeight,
      placement: openUpward ? "up" : "down",
    };
  }, [anchorRect, downloadOptions.length]);

  const handleDownload = (url, extension) => {
    if (!sessionId || !url) return;

    downloadFile(sessionId, `${sessionTitle || "session"}.${extension}`, url);
    onCloseMenu();
  };

  return (
    <>
      <div className="download-dropdown-container course-history-download-dropdown">
        <button
          ref={buttonRef}
          type="button"
          className={`btn btn-outline course-history-download-toggle${canDownload ? "" : " course-history-download-toggle--disabled"}`}
          onClick={handleTriggerClick}
          disabled={!canDownload}
          aria-disabled={!canDownload}
          aria-expanded={isOpen}
          aria-haspopup="menu"
        >
          Download
          {FA_DOWNLOAD_CHEVRON}
        </button>
      </div>

      {isOpen && position
        ? ReactDOM.createPortal(
            <>
              <div
                className="course-history-download-backdrop"
                aria-hidden="true"
                onClick={onCloseMenu}
              />
              <div
                className={`download-menu course-history-download-menu course-history-download-popover${position.placement === "up" ? " course-history-download-popover--up" : ""}`}
                role="menu"
                aria-label="Download options"
                style={{
                  left: `${position.left}px`,
                  top: `${position.top}px`,
                  width: `${position.width}px`,
                  maxHeight: `${position.maxHeight}px`,
                }}
              >
                {downloadOptions.map((option) => (
                  <button
                    key={option.key}
                    type="button"
                    role="menuitem"
                    onClick={() => handleDownload(option.url, option.extension)}
                  >
                    {option.label}
                  </button>
                ))}
              </div>
            </>,
            document.body,
          )
        : null}
    </>
  );
}

const CourseDetailsPage = () => {
  const { courseId } = useParams();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const activeTab = normalizeCourseWorkspaceTab(searchParams.get("tab"));
  const [course, setCourse] = useState(null);
  const [courseLoading, setCourseLoading] = useState(true);
  const [courseError, setCourseError] = useState("");
  const [allRosterItems, setAllRosterItems] = useState([]);
  const [rosterPage, setRosterPage] = useState(1);
  const [rosterPageSize, setRosterPageSize] = useState(25);
  const [rosterLoading, setRosterLoading] = useState(true);
  const [newStudentEmail, setNewStudentEmail] = useState("");

  const [historySessions, setHistorySessions] = useState([]);
  const [historyPage, setHistoryPage] = useState(1);
  const [historyPageSize, setHistoryPageSize] = useState(25);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyAssetsById, setHistoryAssetsById] = useState({});
  const [openDownloadMenuSessionId, setOpenDownloadMenuSessionId] =
    useState(null);
  const [downloadMenuAnchor, setDownloadMenuAnchor] = useState(null);
  const [downloadMenuAnchorRect, setDownloadMenuAnchorRect] = useState(null);

  const [materials, setMaterials] = useState([]);
  const [materialsTotal, setMaterialsTotal] = useState(0);
  const [materialsPage, setMaterialsPage] = useState(1);
  const [materialsPageSize, setMaterialsPageSize] = useState(25);
  const [materialsLoading, setMaterialsLoading] = useState(false);
  const [materialsError, setMaterialsError] = useState("");
  const [materialsSearch, setMaterialsSearch] = useState("");
  const [materialsStatusFilter, setMaterialsStatusFilter] = useState("all");
  const [materialTitle, setMaterialTitle] = useState("");
  const [materialReleaseMode, setMaterialReleaseMode] = useState("now");
  const [materialReleaseAt, setMaterialReleaseAt] = useState(getInitialMaterialReleaseAt);
  const [materialFile, setMaterialFile] = useState(null);
  const [materialUploading, setMaterialUploading] = useState(false);
  const [materialUploadError, setMaterialUploadError] = useState('');
  const [workspaceReloadToken, setWorkspaceReloadToken] = useState(0);

  const { downloadFile, isDownloading } = useSecureDownload();

  useDocumentTitle(course?.name ? `${course.name} | AutoSlide` : 'Course Details | AutoSlide');

  const refreshWorkspace = () => {
    setWorkspaceReloadToken((value) => value + 1);
  };

  const handleTabChange = useCallback((nextTab) => {
    const normalizedTab = normalizeCourseWorkspaceTab(nextTab);
    const nextParams = new URLSearchParams(searchParams);

    if (normalizedTab === "overview") {
      nextParams.delete("tab");
    } else {
      nextParams.set("tab", normalizedTab);
    }

    setSearchParams(nextParams, { replace: true });
  }, [searchParams, setSearchParams]);

  useEffect(() => {
    setCourse(null);
    setCourseLoading(true);
    setCourseError("");

    setAllRosterItems([]);
    setRosterPage(1);
    setRosterPageSize(25);
    setRosterLoading(true);
    setNewStudentEmail("");

    setHistorySessions([]);
    setHistoryPage(1);
    setHistoryPageSize(25);
    setHistoryLoading(false);
    setHistoryAssetsById({});
    setOpenDownloadMenuSessionId(null);
    setDownloadMenuAnchor(null);
    setDownloadMenuAnchorRect(null);

    setMaterials([]);
    setMaterialsTotal(0);
    setMaterialsPage(1);
    setMaterialsPageSize(25);
    setMaterialsLoading(false);
    setMaterialsError("");
    setMaterialsSearch("");
    setMaterialsStatusFilter("all");
    setMaterialTitle("");
    setMaterialReleaseMode("now");
    setMaterialReleaseAt(getInitialMaterialReleaseAt());
    setMaterialFile(null);
    setMaterialUploading(false);
    setMaterialUploadError('');
  }, [courseId]);

  const fetchDetails = async () => {
    const token = localStorage.getItem("token");
    setCourseLoading(true);
    setRosterLoading(true);
    setCourseError("");

    const fetchRosterItems = async () => {
      const pageSize = 100;
      let page = 1;
      let allItems = [];
      let totalCount = 0;

      while (true) {
        const response = await fetch(
          apiUrl(
            buildPagedPath(`/api/courses/${courseId}/roster`, page, pageSize),
          ),
          {
            headers: { Authorization: `Bearer ${token}` },
          },
        );

        if (!response.ok) {
          return { items: [], total: 0 };
        }

        const pagination = readPaginationHeaders(response, pageSize);
        const pageItems = await response.json();
        const items = Array.isArray(pageItems) ? pageItems : [];

        totalCount = pagination.total || totalCount || items.length;
        allItems = allItems.concat(items);

        if (allItems.length >= totalCount || items.length < pageSize) {
          break;
        }

        page += 1;
      }

      return { items: allItems, total: totalCount };
    };

    try {
      const [courseRes, rosterResult] = await Promise.all([
        fetch(apiUrl(`/api/courses/${courseId}`), {
          headers: { Authorization: `Bearer ${token}` },
        }),
        fetchRosterItems(),
      ]);

      if (courseRes.ok) {
        setCourse(await courseRes.json());
      } else {
        setCourse(null);
        setCourseError("Unable to load course details.");
      }

      setAllRosterItems(rosterResult.items);
    } catch (error) {
      setCourse(null);
      setAllRosterItems([]);
      setCourseError(error.message || "Unable to load course details.");
    } finally {
      setCourseLoading(false);
      setRosterLoading(false);
    }
  };

  useEffect(() => {
    fetchDetails();
  }, [courseId, workspaceReloadToken]);

  useEffect(() => {
    if (!downloadMenuAnchor) return undefined;

    const updateAnchorRect = () => {
      if (!downloadMenuAnchor) return;
      setDownloadMenuAnchorRect(downloadMenuAnchor.getBoundingClientRect());
    };

    updateAnchorRect();
    window.addEventListener("resize", updateAnchorRect);
    window.addEventListener("scroll", updateAnchorRect, true);

    return () => {
      window.removeEventListener("resize", updateAnchorRect);
      window.removeEventListener("scroll", updateAnchorRect, true);
    };
  }, [downloadMenuAnchor]);

  useEffect(() => {
    let mounted = true;

    const loadHistory = async () => {
      const token = localStorage.getItem("token");
      if (!token) {
        if (mounted) {
          setHistorySessions([]);
          setHistoryAssetsById({});
          setHistoryLoading(false);
        }
        return;
      }

      try {
        setHistoryLoading(true);
        setHistoryAssetsById({});
        setHistoryPage(1);

        const pageSize = 100;
        let page = 1;
        let totalCount = 0;
        let allHistory = [];

        while (true) {
          const res = await fetch(
            apiUrl(
              buildPagedPath(
                `/api/courses/${courseId}/history`,
                page,
                pageSize,
              ),
            ),
            {
              headers: { Authorization: `Bearer ${token}` },
            },
          );

          if (!res.ok) {
            console.error("Failed to fetch history", await res.text());
            allHistory = [];
            totalCount = 0;
            break;
          }

          const pagination = readPaginationHeaders(res, pageSize);
          const data = await res.json();
          const nextRows = Array.isArray(data)
            ? data.map(mapHistorySession)
            : [];

          totalCount = pagination.total || nextRows.length;
          allHistory = allHistory.concat(nextRows);

          if (allHistory.length >= totalCount || nextRows.length < pageSize) {
            break;
          }

          page += 1;
        }

        if (!mounted) return;

        allHistory.sort((left, right) => {
          const leftTime = left.startedAt
            ? new Date(left.startedAt).getTime()
            : 0;
          const rightTime = right.startedAt
            ? new Date(right.startedAt).getTime()
            : 0;
          return rightTime - leftTime;
        });

        setHistorySessions(allHistory);
      } catch (e) {
        console.error("Failed to fetch history", e);
        if (mounted) {
          setHistorySessions([]);
        }
      } finally {
        if (mounted) {
          setHistoryLoading(false);
        }
      }
    };

    loadHistory();

    return () => {
      mounted = false;
    };
  }, [courseId, workspaceReloadToken]);

  useEffect(() => {
    let mounted = true;

    const loadMaterials = async () => {
      const token = localStorage.getItem("token");
      if (!token) {
        if (mounted) {
          setMaterials([]);
          setMaterialsTotal(0);
          setMaterialsError("");
          setMaterialsLoading(false);
        }
        return;
      }

      try {
        setMaterialsLoading(true);
        setMaterialsError("");
        setMaterialsPage(1);

        const pageSize = 100;
        let page = 1;
        let totalCount = 0;
        let allMaterials = [];

        while (true) {
          const res = await fetch(
            apiUrl(
              buildPagedPath(
                `/api/courses/${courseId}/materials`,
                page,
                pageSize,
              ),
            ),
            {
              headers: { Authorization: `Bearer ${token}` },
            },
          );

          if (!res.ok) {
            console.error("Failed to fetch materials", await res.text());
            allMaterials = [];
            totalCount = 0;
            break;
          }

          const pagination = readPaginationHeaders(res, pageSize);
          const data = await res.json();
          const nextRows = Array.isArray(data) ? data : [];

          totalCount = pagination.total || nextRows.length;
          allMaterials = allMaterials.concat(nextRows);

          if (allMaterials.length >= totalCount || nextRows.length < pageSize) {
            break;
          }

          page += 1;
        }

        if (!mounted) return;

        setMaterials(allMaterials);
        setMaterialsTotal(totalCount || allMaterials.length);
      } catch (e) {
        console.error("Failed to fetch materials", e);
        if (mounted) {
          setMaterials([]);
          setMaterialsTotal(0);
          setMaterialsError(e.message || "Failed to load materials.");
        }
      } finally {
        if (mounted) {
          setMaterialsLoading(false);
        }
      }
    };

    loadMaterials();

    return () => {
      mounted = false;
    };
  }, [courseId, workspaceReloadToken]);

  const sortedHistory = useMemo(() => {
    const rows = Array.isArray(historySessions) ? [...historySessions] : [];
    const getTime = (r) => (r.startedAt ? new Date(r.startedAt).getTime() : 0);
    rows.sort((a, b) => getTime(b) - getTime(a));
    return rows;
  }, [historySessions]);

  const totalSessions = sortedHistory.length;

  const filteredRosterItems = useMemo(() => {
    return Array.isArray(allRosterItems) ? allRosterItems : [];
  }, [allRosterItems]);

  const totalRosterItems = filteredRosterItems.length;
  const rosterPagination = useMemo(
    () => getPaginationState(totalRosterItems, rosterPage, rosterPageSize),
    [totalRosterItems, rosterPage, rosterPageSize],
  );
  const historyPagination = useMemo(
    () => getPaginationState(totalSessions, historyPage, historyPageSize),
    [totalSessions, historyPage, historyPageSize],
  );

  const filteredMaterials = useMemo(() => {
    const source = Array.isArray(materials) ? materials : [];
    const query = materialsSearch.trim().toLowerCase();

    return source.filter((material) => {
      const status = String(material.status || "").toLowerCase();
      const weekLabel = Number(material.week || 0) > 0 ? `week ${material.week}` : "unscheduled";
      const releaseLabel = material.releaseAt ? formatHKT(material.releaseAt).toLowerCase() : "";
      const searchableText = [material.title, material.fileName, weekLabel, status, releaseLabel]
        .filter(Boolean)
        .join(" ")
        .toLowerCase();

      const matchesQuery = !query || searchableText.includes(query);
      const matchesStatus = materialsStatusFilter === "all" || status === materialsStatusFilter;

      return matchesQuery && matchesStatus;
    });
  }, [materials, materialsSearch, materialsStatusFilter]);

  const materialsWeekCount = useMemo(
    () => new Set(filteredMaterials.map((material) => Number(material.week || 0))).size,
    [filteredMaterials],
  );

  const hasMaterialFilters = Boolean(materialsSearch.trim()) || materialsStatusFilter !== "all";

  const materialsSummary = useMemo(() => {
    if (materialsLoading && filteredMaterials.length === 0) {
      return "Loading materials library...";
    }

    if (filteredMaterials.length === 0) {
      return hasMaterialFilters ? "Filters are hiding all materials." : "No materials uploaded yet.";
    }

    return `${filteredMaterials.length} file${filteredMaterials.length === 1 ? "" : "s"} across ${materialsWeekCount} week${materialsWeekCount === 1 ? "" : "s"}`;
  }, [filteredMaterials.length, hasMaterialFilters, materialsLoading, materialsWeekCount]);

  const materialsPagination = useMemo(
    () => getPaginationState(filteredMaterials.length, materialsPage, materialsPageSize),
    [filteredMaterials.length, materialsPage, materialsPageSize],
  );

  const paginatedMaterialsItems = useMemo(() => {
    const start = (materialsPagination.currentPage - 1) * materialsPagination.pageSize;
    return filteredMaterials.slice(start, start + materialsPagination.pageSize);
  }, [filteredMaterials, materialsPagination.currentPage, materialsPagination.pageSize]);

  const paginatedRosterItems = useMemo(() => {
    const start =
      (rosterPagination.currentPage - 1) * rosterPagination.pageSize;
    return filteredRosterItems.slice(start, start + rosterPagination.pageSize);
  }, [
    filteredRosterItems,
    rosterPagination.currentPage,
    rosterPagination.pageSize,
  ]);

  const latestHistory = sortedHistory[0] || null;

  const rosterPreviewItems = useMemo(
    () => filteredRosterItems.slice(0, 5),
    [filteredRosterItems],
  );
  const recentHistoryPreview = useMemo(
    () => sortedHistory.slice(0, 3),
    [sortedHistory],
  );
  const nextReleaseMaterial = useMemo(() => {
    const now = Date.now();
    const upcoming = (Array.isArray(materials) ? materials : [])
      .filter((material) => {
        const releaseAt = material.releaseAt || material.ReleaseAt;
        return releaseAt && new Date(releaseAt).getTime() > now;
      })
      .sort((left, right) => {
        const leftTime = new Date(left.releaseAt || left.ReleaseAt).getTime();
        const rightTime = new Date(
          right.releaseAt || right.ReleaseAt,
        ).getTime();
        return leftTime - rightTime;
      });

    return upcoming[0] || null;
  }, [materials]);

  const overviewLoading =
    courseLoading || rosterLoading || historyLoading || materialsLoading;

  const pagedHistory = useMemo(() => {
    const start =
      (historyPagination.currentPage - 1) * historyPagination.pageSize;
    return sortedHistory.slice(start, start + historyPagination.pageSize);
  }, [
    historyPagination.currentPage,
    historyPagination.pageSize,
    sortedHistory,
  ]);

  useEffect(() => {
    setHistoryPage((currentPage) =>
      clampPage(currentPage, historyPagination.totalPages),
    );
  }, [historyPagination.totalPages]);

  useEffect(() => {
    setRosterPage((currentPage) =>
      clampPage(currentPage, rosterPagination.totalPages),
    );
  }, [rosterPagination.totalPages]);

  useEffect(() => {
    setMaterialsPage((currentPage) =>
      clampPage(currentPage, materialsPagination.totalPages),
    );
  }, [materialsPagination.totalPages]);

  const visibleHistory = useMemo(() => {
    return pagedHistory.map((session) =>
      mergeSessionAssets(session, historyAssetsById[session.id] || {}),
    );
  }, [historyAssetsById, pagedHistory]);

  useEffect(() => {
    if (activeTab !== "history" || pagedHistory.length === 0) return;

    let mounted = true;

    const loadSessionAssets = async () => {
      const token = localStorage.getItem("token");
      if (!token) return;

      const missingSessions = pagedHistory.filter(
        (session) => session.id && !historyAssetsById[session.id],
      );
      if (missingSessions.length === 0) return;

      const resolvedAssets = await Promise.all(
        missingSessions.map(async (session) => {
          try {
            const response = await fetch(
              apiUrl(`/api/sessions/${session.id}/downloads`),
              {
                headers: { Authorization: `Bearer ${token}` },
              },
            );

            if (!response.ok) {
              return [session.id, null];
            }

            const meta = await response.json();
            return [
              session.id,
              {
                status: normalizeSessionStatus(session.status ?? session.Status),
                downloads: normalizeSessionDownloads(meta.downloads),
              },
            ];
          } catch (error) {
            return [session.id, null];
          }
        }),
      );

      if (!mounted) return;

      setHistoryAssetsById((current) => {
        const next = { ...current };
        for (const [sessionId, assets] of resolvedAssets) {
          if (assets) next[sessionId] = assets;
        }
        return next;
      });
    };

    loadSessionAssets();

    return () => {
      mounted = false;
    };
  }, [activeTab, historyAssetsById, pagedHistory]);

  const materialsByWeek = useMemo(() => {
    const map = {};
    for (const m of paginatedMaterialsItems) {
      const wk = m.week ?? m.Week ?? 1;
      if (!map[wk]) map[wk] = [];
      map[wk].push(m);
    }

    for (const wk of Object.keys(map)) {
      map[wk].sort((a, b) => {
        const at = a.uploadedAt || a.UploadedAt;
        const bt = b.uploadedAt || b.UploadedAt;
        return (
          (bt ? new Date(bt).getTime() : 0) - (at ? new Date(at).getTime() : 0)
        );
      });
    }

    return map;
  }, [paginatedMaterialsItems]);

  const handleAddStudent = async () => {
    if (!newStudentEmail) return;
    await enrollStudents([newStudentEmail]);
    setNewStudentEmail("");
  };

  const handleMaterialTitleChange = (value) => {
    setMaterialTitle(value);
    if (materialUploadError) {
      setMaterialUploadError('');
    }
  };

  const handleMaterialReleaseModeChange = (value) => {
    setMaterialReleaseMode(value);
    if (materialUploadError) {
      setMaterialUploadError('');
    }
  };

  const handleMaterialReleaseAtChange = (value) => {
    setMaterialReleaseAt(value);
    if (materialUploadError) {
      setMaterialUploadError('');
    }
  };

  const handleMaterialFileChange = (file) => {
    if (file && isMaterialUploadTooLarge(file)) {
      setMaterialFile(null);
      setMaterialUploadError(getMaterialUploadSizeMessage());
      return;
    }

    setMaterialUploadError('');
    setMaterialFile(file);
  };

  const handleQuickUploadMaterial = async (event) => {
    event?.preventDefault?.();

    const token = localStorage.getItem("token");
    if (!token) return;
    if (materialUploadError) return;
    if (!materialTitle.trim()) {
      setMaterialUploadError('Please enter a material title.');
      return;
    }
    if (!materialFile) {
      setMaterialUploadError('Please choose a file.');
      return;
    }
    if (isMaterialUploadTooLarge(materialFile)) {
      setMaterialUploadError(getMaterialUploadSizeMessage());
      return;
    }

    setMaterialUploading(true);
    setMaterialUploadError('');

    try {
      const fd = new FormData();
      fd.append("file", materialFile);
      fd.append("title", materialTitle.trim());
      fd.append("week", "0");

      const uploadResponse = await fetch(apiUrl(`/api/courses/${courseId}/materials`), {
        method: "POST",
        headers: { Authorization: `Bearer ${token}` },
        body: fd,
      });

      if (!uploadResponse.ok) {
        const responseText = await uploadResponse.text().catch(() => '');
        console.error("Upload material failed", uploadResponse.status, responseText);
        setMaterialUploadError(formatUploadErrorMessage(uploadResponse, responseText, 'Unable to upload material. Please try again.'));
        return;
      }

      const createdMaterial = await uploadResponse.json();
      const materialId = createdMaterial?.id || createdMaterial?.Id;

      if (materialId) {
        const releaseAtValue = materialReleaseMode === "schedule" && materialReleaseAt
          ? new Date(materialReleaseAt).toISOString()
          : null;

        const visibilityResponse = await fetch(apiUrl(`/api/materials/${materialId}/visibility`), {
          method: "PATCH",
          headers: {
            "Content-Type": "application/json",
            Authorization: `Bearer ${token}`,
          },
          body: JSON.stringify({
            isVisible: materialReleaseMode === "now",
            releaseAt: releaseAtValue,
          }),
        });

        if (!visibilityResponse.ok) {
          console.error("Material visibility update failed", await visibilityResponse.text());
          setMaterialUploadError("Material uploaded, but the release settings could not be saved.");
        }
      }

      setMaterialTitle("");
      setMaterialFile(null);
      setMaterialReleaseMode("now");
      refreshWorkspace();
    } finally {
      setMaterialUploading(false);
    }
  };

  const handleMaterialsUpload = async (event) => {
    event?.preventDefault?.();

    const token = localStorage.getItem("token");
    if (!token) return;
    if (materialUploadError) return;
    if (!materialTitle.trim()) {
      setMaterialUploadError('Please enter a material title.');
      return;
    }
    if (!materialFile) {
      setMaterialUploadError('Please choose a file.');
      return;
    }
    if (isMaterialUploadTooLarge(materialFile)) {
      setMaterialUploadError(getMaterialUploadSizeMessage());
      return;
    }

    setMaterialUploading(true);
    setMaterialUploadError('');

    try {
      const fd = new FormData();
      fd.append("file", materialFile);
      fd.append("title", materialTitle.trim());
      fd.append("week", "0");

      const uploadResponse = await fetch(apiUrl(`/api/courses/${courseId}/materials`), {
        method: "POST",
        headers: { Authorization: `Bearer ${token}` },
        body: fd,
      });

      if (!uploadResponse.ok) {
        const responseText = await uploadResponse.text().catch(() => '');
        console.error("Upload material failed", uploadResponse.status, responseText);
        setMaterialUploadError(formatUploadErrorMessage(uploadResponse, responseText, 'Unable to upload material. Please try again.'));
        return;
      }

      const createdMaterial = await uploadResponse.json();
      const materialId = createdMaterial?.id || createdMaterial?.Id;
      const releaseAtValue = materialReleaseAt ? new Date(materialReleaseAt).toISOString() : null;

      if (materialId) {
        const visibilityResponse = await fetch(apiUrl(`/api/materials/${materialId}/visibility`), {
          method: "PATCH",
          headers: {
            "Content-Type": "application/json",
            Authorization: `Bearer ${token}`,
          },
          body: JSON.stringify({
            isVisible: !releaseAtValue || new Date(releaseAtValue).getTime() <= Date.now(),
            releaseAt: releaseAtValue,
          }),
        });

        if (!visibilityResponse.ok) {
          console.error("Material visibility update failed", await visibilityResponse.text());
          setMaterialUploadError("Material uploaded, but the release settings could not be saved.");
        }
      }

      setMaterialTitle("");
      setMaterialFile(null);
      const now = new Date();
      now.setMinutes(now.getMinutes() - now.getTimezoneOffset());
      setMaterialReleaseAt(now.toISOString().slice(0, 16));
      refreshWorkspace();
    } finally {
      setMaterialUploading(false);
    }
  };

  const handleDownloadMaterial = async (material) => {
    const materialId = material?.id || material?.Id;
    if (!materialId) return;

    await downloadFile(
      materialId,
      material?.fileName || material?.OriginalFileName || material?.title || "material",
      "materials",
    );
  };

  const handleDeleteMaterial = async (material) => {
    const materialId = material?.id || material?.Id || material;
    if (!materialId) return;
    if (!confirm("Delete this material?")) return;
    const token = localStorage.getItem("token");
    if (!token) return;

    const res = await fetch(apiUrl(`/api/materials/${materialId}`), {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });

    if (!res.ok) {
      console.error("Delete material failed", await res.text());
      alert("Delete failed");
      return;
    }

    refreshWorkspace();
  };

  const handleFileUpload = (e) => {
    const file = e.target.files[0];
    if (!file) return;

    Papa.parse(file, {
      header: true,
      complete: (results) => {
        const emails = results.data
          .map((row) => row.Email || row.email)
          .filter((email) => email);

        if (emails.length > 0) {
          enrollStudents(emails);
        } else {
          alert(
            "No emails found in CSV. Please ensure there is an 'Email' column.",
          );
        }
      },
    });
  };

  const enrollStudents = async (emails) => {
    const token = localStorage.getItem("token");
    const response = await fetch(apiUrl(`/api/courses/${courseId}/enroll`), {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify({ emails }),
    });

    if (response.ok) {
      refreshWorkspace();
    } else {
      alert("Failed to enroll students");
    }
  };

  const removeStudent = async (email) => {
    if (!confirm(`Are you sure you want to remove ${email}?`)) return;

    const token = localStorage.getItem("token");
    const response = await fetch(
      apiUrl(`/api/courses/${courseId}/enroll/${email}`),
      {
        method: "DELETE",
        headers: {
          Authorization: `Bearer ${token}`,
        },
      },
    );

    if (response.ok) {
      refreshWorkspace();
    } else {
      alert("Failed to remove student");
    }
  };

  if (!course) return <div style={{ padding: "2rem" }}>Loading...</div>;

  return (
    <TeacherDashboardShell>
      <div className="course-details-page course-workspace-page">
        <CourseWorkspaceHeader
          activeTab={activeTab}
          onTabChange={handleTabChange}
          courseLabel={course ? course.code : ""}
        />

        {courseError && !courseLoading ? (
          <Card className="course-workspace-error" role="alert">
            <div>
              <p className="course-workspace-error__title">
                Unable to load course workspace
              </p>
              <p className="course-workspace-error__text">{courseError}</p>
            </div>
            <button
              type="button"
              className="btn btn-outline"
              onClick={refreshWorkspace}
            >
              Retry
            </button>
          </Card>
        ) : null}

        {!courseError ? (
          <>
            {activeTab === "overview" ? (
              <section className="course-overview-layout">
                <div className="course-overview-layout__main">
                  <div className="course-overview-stats">
                    <OverviewStatCard
                      tone="blue"
                      loading={rosterLoading}
                      icon={FA_USERS_ICON}
                      value={totalRosterItems}
                      label="Total Students Enrolled"
                      detail="Derived from the full roster list"
                      badge={overviewLoading ? "Loading" : null}
                    />

                    <OverviewStatCard
                      tone="indigo"
                      loading={historyLoading}
                      icon={FA_HISTORY_ICON}
                      value={totalSessions}
                      label="Past Recorded Sessions"
                      detail={
                        latestHistory
                          ? `Latest session: ${formatDateOnlyHKT(latestHistory.startedAt)}`
                          : "No sessions recorded yet"
                      }
                    />

                    <OverviewStatCard
                      tone="violet"
                      loading={materialsLoading}
                      icon={FA_CALENDAR_ICON}
                      value={
                        nextReleaseMaterial
                          ? formatDateOnlyHKT(
                              nextReleaseMaterial.releaseAt ||
                                nextReleaseMaterial.ReleaseAt,
                            )
                          : "Not scheduled"
                      }
                      label="Next Material Release"
                      detail={
                        materialsTotal > 0
                          ? `${materialsTotal} materials in the library`
                          : "No materials uploaded yet"
                      }
                    />
                  </div>

                  <RosterPreviewCard
                    rosterItems={rosterPreviewItems}
                    totalCount={totalRosterItems}
                    loading={rosterLoading}
                    onImportCsv={() => handleTabChange("roster")}
                    onInvite={() => handleTabChange("roster")}
                    onOpenRoster={() => handleTabChange("roster")}
                  />
                </div>

                <aside className="course-overview-layout__rail">
                  <QuickUploadCard
                    materialTitle={materialTitle}
                    materialReleaseMode={materialReleaseMode}
                    materialReleaseAt={materialReleaseAt}
                    materialFile={materialFile}
                    pending={
                      materialUploading || materialsLoading || courseLoading
                    }
                    nextReleaseLabel={
                      nextReleaseMaterial
                        ? formatDateOnlyHKT(
                            nextReleaseMaterial.releaseAt ||
                              nextReleaseMaterial.ReleaseAt,
                          )
                        : ""
                    }
                    uploadError={materialUploadError}
                    maxUploadSizeLabel={MATERIAL_UPLOAD_MAX_LABEL}
                    onMaterialTitleChange={handleMaterialTitleChange}
                    onMaterialReleaseModeChange={handleMaterialReleaseModeChange}
                    onMaterialReleaseAtChange={handleMaterialReleaseAtChange}
                    onMaterialFileChange={handleMaterialFileChange}
                    onSubmit={handleQuickUploadMaterial}
                  />
                </aside>
              </section>
            ) : null}

            {activeTab === "roster" && (
              <div>
                <div className="actions-bar">
                  <div style={{ flex: 1 }} className="input-group">
                    <div style={{ flex: 1 }}>
                      <label
                        style={{
                          display: "block",
                          marginBottom: "6px",
                          fontSize: "0.9rem",
                          fontWeight: "600",
                        }}
                      >
                        Add Student Email
                      </label>
                      <div style={{ display: "flex", gap: "0.5rem" }}>
                        <input
                          type="email"
                          value={newStudentEmail}
                          onChange={(e) => setNewStudentEmail(e.target.value)}
                          style={{
                            padding: "0.5rem",
                            border: "1px solid #dce7f2",
                            borderRadius: "8px",
                            width: "100%",
                          }}
                          placeholder="student@polyu.edu.hk"
                        />
                        <button
                          onClick={handleAddStudent}
                          className="btn btn-secondary"
                          style={{ minWidth: "84px" }}
                        >
                          Add
                        </button>
                      </div>
                    </div>
                  </div>

                  <div
                    style={{
                      width: "1px",
                      height: "40px",
                      background: "#eef2f5",
                    }}
                  ></div>

                  <div>
                    <label
                      style={{
                        display: "block",
                        marginBottom: "6px",
                        fontSize: "0.9rem",
                        fontWeight: "600",
                      }}
                    >
                      Bulk Import
                    </label>
                    <label className="file-upload-label">
                      {FA_UPLOAD_ICON}
                      <span>Upload CSV</span>
                      <input
                        type="file"
                        accept=".csv"
                        onChange={handleFileUpload}
                      />
                    </label>
                  </div>
                </div>

                <table className="roster-table">
                  <thead>
                    <tr>
                      <th>Name</th>
                      <th>Email</th>
                      <th>Status</th>
                      <th>Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    {paginatedRosterItems.map((student, idx) => (
                      <tr key={idx}>
                        <td>{student.name || "-"}</td>
                        <td>{student.email}</td>
                        <td>
                          {student.status === 1 ? (
                            <span
                              style={{
                                padding: "6px 8px",
                                borderRadius: "9999px",
                                fontSize: "0.82rem",
                                backgroundColor: "#dcfce7",
                                color: "#065f46",
                              }}
                            >
                              Active
                            </span>
                          ) : student.status === 0 ? (
                            <span
                              style={{
                                padding: "6px 8px",
                                borderRadius: "9999px",
                                fontSize: "0.82rem",
                                backgroundColor: "#ffedd5",
                                color: "#9a3412",
                              }}
                            >
                              Pending Invite
                            </span>
                          ) : (
                            <span
                              style={{
                                padding: "6px 8px",
                                borderRadius: "9999px",
                                fontSize: "0.82rem",
                                backgroundColor: "#f3f4f6",
                                color: "#6b7280",
                              }}
                            >
                              Unknown
                            </span>
                          )}
                        </td>
                        <td>
                          <button
                            onClick={() => removeStudent(student.email)}
                            className="trash-btn"
                            title="Remove student"
                            aria-label="Remove student"
                          >
                            {FA_TRASH_ICON}
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>

                <PaginationControls
                  page={rosterPagination.currentPage}
                  pageSize={rosterPagination.pageSize}
                  totalCount={rosterPagination.totalItems}
                  itemCount={paginatedRosterItems.length}
                  onPageChange={(nextPage) =>
                    setRosterPage(
                      clampPage(nextPage, rosterPagination.totalPages),
                    )
                  }
                  onPageSizeChange={(nextSize) => {
                    setRosterPage(1);
                    setRosterPageSize(nextSize);
                  }}
                />
              </div>
            )}

            {activeTab === "history" && (
              <section className="course-history-panel">
                <div className="course-history-summary-grid">
                  <Card className="course-history-summary-card">
                    <div
                      className="course-history-summary-card__icon course-history-summary-card__icon--primary"
                      aria-hidden="true"
                    >
                      {FA_CALENDAR_ICON}
                    </div>
                    <div className="course-history-summary-card__body">
                      <p className="course-history-summary-card__label">
                        Total Sessions
                      </p>
                      <p className="course-history-summary-card__value">
                        {totalSessions}
                      </p>
                    </div>
                  </Card>

                  <Card className="course-history-summary-card">
                    <div
                      className="course-history-summary-card__icon course-history-summary-card__icon--secondary"
                      aria-hidden="true"
                    >
                      {FA_HISTORY_ICON}
                    </div>
                    <div className="course-history-summary-card__body">
                      <p className="course-history-summary-card__label">
                        Latest Session
                      </p>
                      <p className="course-history-summary-card__value">
                        {latestHistory
                          ? formatDateOnlyHKT(latestHistory.startedAt)
                          : "—"}
                      </p>
                    </div>
                  </Card>
                </div>

                <Card className="course-history-table-card">
                  <div className="course-history-table-card__header">
                    <h3 className="course-history-table-card__title">
                      Past Sessions
                    </h3>
                  </div>

                  {historyLoading ? (
                    <div className="course-history-empty course-history-empty--loading">
                      <p className="course-history-empty__title">
                        Loading session history...
                      </p>
                      <p className="course-history-empty__text">
                        Fetching completed sessions for this course.
                      </p>
                    </div>
                  ) : totalSessions === 0 ? (
                    <div className="course-history-empty">
                      <p className="course-history-empty__title">
                        No past sessions are available yet
                      </p>
                      <p className="course-history-empty__text">
                        Completed sessions will appear here once they are
                        available.
                      </p>
                    </div>
                  ) : (
                    <div
                      className="course-history-table-scroll"
                      role="region"
                      aria-label="Session history table"
                    >
                      <table className="roster-table course-history-table">
                        <thead>
                          <tr>
                            <th>Date</th>
                            <th>Title</th>
                            <th>Duration</th>
                            <th>Status</th>
                            <th>Actions</th>
                          </tr>
                        </thead>
                        <tbody>
                          {visibleHistory.map((session) => {
                            const startedAt = session.startedAt;
                            const status = session.status;
                            const isEnded = status === "ended";

                            return (
                              <tr
                                key={session.id}
                                className="course-history-table__row"
                              >
                                <td className="course-history-table__cell course-history-table__cell--date">
                                  {startedAt
                                    ? formatDateOnlyHKT(startedAt)
                                    : "-"}
                                </td>
                                <td className="course-history-table__cell course-history-table__cell--title">
                                  <div className="course-history-table__title-wrap">
                                    <span
                                      className="course-history-table__dot"
                                      aria-hidden="true"
                                    />
                                    <span className="course-history-table__title-text">
                                      {session.title}
                                    </span>
                                  </div>
                                </td>
                                <td className="course-history-table__cell course-history-table__cell--duration">
                                  {session.durationMinutes > 0
                                    ? `${session.durationMinutes} min`
                                    : "-"}
                                </td>
                                <td className="course-history-table__cell course-history-table__cell--status">
                                  <StatusBadge
                                    status={
                                      isEnded
                                        ? "released"
                                        : status === "live"
                                          ? "live"
                                          : "upcoming"
                                    }
                                    label={
                                      isEnded
                                        ? "Ended"
                                        : status === "live"
                                          ? "Live"
                                          : "Processing"
                                    }
                                  />
                                </td>
                                <td className="course-history-table__cell course-history-table__cell--actions">
                                  <div className="course-history-table__actions-group">
                                    <SessionDownloadMenu
                                      sessionId={session.id}
                                      sessionTitle={session.title}
                                      downloadFile={downloadFile}
                                      downloadOptions={session.downloadOptions}
                                      canDownload={session.canDownload}
                                      isOpen={
                                        openDownloadMenuSessionId === session.id
                                      }
                                      anchorRect={
                                        openDownloadMenuSessionId === session.id
                                          ? downloadMenuAnchorRect
                                          : null
                                      }
                                      onOpenMenu={(id, trigger) => {
                                        const rect =
                                          trigger?.getBoundingClientRect();
                                        setDownloadMenuAnchor(trigger || null);
                                        setDownloadMenuAnchorRect(rect || null);
                                        setOpenDownloadMenuSessionId(
                                          (current) =>
                                            current === id ? null : id,
                                        );
                                      }}
                                      onCloseMenu={() => {
                                        setOpenDownloadMenuSessionId(null);
                                        setDownloadMenuAnchor(null);
                                        setDownloadMenuAnchorRect(null);
                                      }}
                                    />

                                    <button
                                      type="button"
                                      className={`course-history-table__replay-btn${session.canReplay ? "" : " course-history-table__replay-btn--disabled"}`}
                                      onClick={() =>
                                        session.canReplay &&
                                        navigate(session.recordingUrl)
                                      }
                                      disabled={!session.canReplay}
                                      aria-disabled={!session.canReplay}
                                    >
                                      {FA_PLAY_ICON}
                                      Watch Replay
                                    </button>
                                  </div>
                                </td>
                              </tr>
                            );
                          })}
                        </tbody>
                      </table>
                    </div>
                  )}

                  <div className="course-history-pagination">
                    <PaginationControls
                      page={historyPagination.currentPage}
                      pageSize={historyPagination.pageSize}
                      totalCount={historyPagination.totalItems}
                      itemCount={pagedHistory.length}
                      onPageChange={(nextPage) =>
                        setHistoryPage(
                          clampPage(nextPage, historyPagination.totalPages),
                        )
                      }
                      onPageSizeChange={(nextSize) => {
                        setHistoryPage(1);
                        setHistoryPageSize(nextSize);
                      }}
                    />
                  </div>
                </Card>
              </section>
            )}

            {activeTab === "materials" && (
              <section className="course-materials-layout">
                <MaterialsUploadCard
                  materialTitle={materialTitle}
                  materialReleaseMode={materialReleaseMode}
                  materialReleaseAt={materialReleaseAt}
                  materialFile={materialFile}
                  pending={materialUploading || materialsLoading || courseLoading}
                  uploadError={materialUploadError}
                  maxUploadSizeLabel={MATERIAL_UPLOAD_MAX_LABEL}
                  onMaterialTitleChange={handleMaterialTitleChange}
                  onMaterialReleaseModeChange={handleMaterialReleaseModeChange}
                  onMaterialReleaseAtChange={handleMaterialReleaseAtChange}
                  onMaterialFileChange={handleMaterialFileChange}
                  onSubmit={handleMaterialsUpload}
                />

                <MaterialsLibraryCard
                  materialsByWeek={materialsByWeek}
                  summary={materialsSummary}
                  loading={materialsLoading}
                  error={materialsError}
                  searchValue={materialsSearch}
                  statusValue={materialsStatusFilter}
                  onSearchChange={setMaterialsSearch}
                  onStatusChange={setMaterialsStatusFilter}
                  onDownload={handleDownloadMaterial}
                  onDelete={handleDeleteMaterial}
                  isDownloading={isDownloading}
                  totalCount={materialsPagination.totalItems}
                  page={materialsPagination.currentPage}
                  pageSize={materialsPagination.pageSize}
                  onPageChange={(nextPage) =>
                    setMaterialsPage(
                      clampPage(nextPage, materialsPagination.totalPages),
                    )
                  }
                  onPageSizeChange={(nextSize) => {
                    setMaterialsPage(1);
                    setMaterialsPageSize(nextSize);
                  }}
                  hasMaterials={materials.length > 0}
                />
              </section>
            )}
          </>
        ) : null}
      </div>
    </TeacherDashboardShell>
  );
};

export default CourseDetailsPage;
