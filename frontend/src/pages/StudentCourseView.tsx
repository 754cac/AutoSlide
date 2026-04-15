import React, { useEffect, useMemo, useState, useCallback } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import useSecureDownload from '../hooks/useSecureDownload';
import AppShell from '../components/ui/AppShell';
import Card from '../components/ui/Card';
import { formatHKT } from '../utils/dateUtils';
import useDocumentTitle from '../hooks/useDocumentTitle';
import { apiUrl } from '../utils/api';
import CourseHero from '../components/student/CourseHero';
import TabNav from '../components/student/TabNav';
import MaterialsTab from '../components/student/MaterialsTab';
import SessionsTab from '../components/student/SessionsTab';
import { getCourseById, getCourseHistory, getCourseMaterials, getCourseSessions } from '../services/studentApi';
import { mapCourse } from '../utils/studentViewModels';

const SESSION_DOWNLOAD_DEFINITIONS = [
  { key: 'originalPdf', label: 'Original PDF', extension: 'pdf' },
  { key: 'originalPptx', label: 'Original PPTX', extension: 'pptx' },
  { key: 'annotatedPdf', label: 'Annotated PDF', extension: 'pdf' },
  { key: 'annotatedPptx', label: 'Annotated PPTX', extension: 'pptx' },
  { key: 'inkArtifactPdf', label: 'Ink Artifact PDF', extension: 'pdf' }
];

function normalizeSessionDownloads(downloads) {
  if (!downloads) return null;

  const normalized = {
    originalPdf: downloads?.originalPdf || null,
    originalPptx: downloads?.originalPptx || null,
    annotatedPdf: downloads?.annotatedPdf || downloads?.inkedPdf || null,
    inkArtifactPdf: downloads?.inkArtifactPdf || null,
    annotatedPptx: downloads?.annotatedPptx || downloads?.inkedPptx || null
  };

  return Object.values(normalized).some(Boolean) ? normalized : null;
}

function getSessionDownloadOptions(downloads) {
  if (!downloads) return [];

  return SESSION_DOWNLOAD_DEFINITIONS.map((definition) => ({
    ...definition,
    url: downloads[definition.key] || null
  })).filter((option) => Boolean(option.url));
}

function mergeReplayAssets(replay, assets = {}) {
  const downloads = normalizeSessionDownloads(assets.downloads);
  const downloadOptions = getSessionDownloadOptions(downloads);

  return {
    ...replay,
    downloads,
    downloadOptions,
    canDownload: downloadOptions.length > 0
  };
}

const STUDENT_COURSE_TABS = ['history', 'materials'];

function normalizeStudentCourseTab(tabValue) {
  const normalized = String(tabValue || '').toLowerCase();
  return STUDENT_COURSE_TABS.includes(normalized) ? normalized : 'history';
}

export default function StudentCourseView() {
  const { courseId } = useParams();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const activeTab = normalizeStudentCourseTab(searchParams.get('tab'));
  const { downloadFile, isDownloading } = useSecureDownload();

  const [course, setCourse] = useState(null);
  const [sessions, setSessions] = useState([]);
  const [replays, setReplays] = useState([]);
  const [materials, setMaterials] = useState([]);

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [reloadToken, setReloadToken] = useState(0);
  const [sessionsLoading, setSessionsLoading] = useState(false);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [materialsLoading, setMaterialsLoading] = useState(false);

  const [materialsPage, setMaterialsPage] = useState(1);
  const [materialsPageSize, setMaterialsPageSize] = useState(25);
  const [materialsTotal, setMaterialsTotal] = useState(0);

  const [sessionsPage, setSessionsPage] = useState(1);
  const [sessionsPageSize, setSessionsPageSize] = useState(25);
  const [sessionsTotal, setSessionsTotal] = useState(0);

  const [replayPage, setReplayPage] = useState(1);
  const [replayPageSize, setReplayPageSize] = useState(25);
  const [replayTotal, setReplayTotal] = useState(0);
  const [replayAssetsById, setReplayAssetsById] = useState({});

  useDocumentTitle(course?.name ? `${course.name} | AutoSlide` : 'Course Workspace | AutoSlide');

  const handleTabChange = useCallback((nextTab) => {
    const normalizedTab = normalizeStudentCourseTab(nextTab);
    const nextParams = new URLSearchParams(searchParams);

    if (normalizedTab === 'history') {
      nextParams.delete('tab');
    } else {
      nextParams.set('tab', normalizedTab);
    }

    setSearchParams(nextParams, { replace: true });
  }, [searchParams, setSearchParams]);

  useEffect(() => {
    setCourse(null);
    setSessions([]);
    setReplays([]);
    setMaterials([]);

    setLoading(true);
    setError('');

    setSessionsLoading(false);
    setHistoryLoading(false);
    setMaterialsLoading(false);

    setMaterialsPage(1);
    setMaterialsPageSize(25);
    setMaterialsTotal(0);

    setSessionsPage(1);
    setSessionsPageSize(25);
    setSessionsTotal(0);

    setReplayPage(1);
    setReplayPageSize(25);
    setReplayTotal(0);
    setReplayAssetsById({});
  }, [courseId]);

  useEffect(() => {
    let mounted = true;

    const loadCourse = async () => {
      setLoading(true);
      setError('');

      try {
        const nextCourse = await getCourseById(courseId);

        if (!mounted) return;
        const mappedCourse = nextCourse ? mapCourse(nextCourse) : { id: courseId, name: 'Course', code: '' };
        setCourse(mappedCourse);
      } catch (err) {
        if (!mounted) return;
        setError(err.message || 'Unable to load course details right now.');
      } finally {
        if (mounted) {
          setLoading(false);
        }
      }
    };

    loadCourse();

    return () => {
      mounted = false;
    };
  }, [courseId, reloadToken]);

  useEffect(() => {
    let mounted = true;

    const loadSessions = async () => {
      setSessionsLoading(true);

      try {
        const nextSessions = await getCourseSessions(courseId, {
          page: sessionsPage,
          pageSize: sessionsPageSize
        });

        if (!mounted) return;
        setSessions(nextSessions.sessions);
        setSessionsTotal(nextSessions.totalCount);
      } catch (err) {
        if (mounted) {
          setError(err.message || 'Unable to load sessions right now.');
        }
      } finally {
        if (mounted) {
          setSessionsLoading(false);
        }
      }
    };

    loadSessions();

    return () => {
      mounted = false;
    };
  }, [courseId, sessionsPage, sessionsPageSize]);

  useEffect(() => {
    let mounted = true;

    const loadHistory = async () => {
      setHistoryLoading(true);

      try {
        const nextHistory = await getCourseHistory(courseId, {
          page: replayPage,
          pageSize: replayPageSize
        });

        if (!mounted) return;
        setReplays(nextHistory.sessions);
        setReplayTotal(nextHistory.totalCount);
      } catch (err) {
        if (mounted) {
          setError(err.message || 'Unable to load replay history right now.');
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
  }, [courseId, replayPage, replayPageSize]);

  useEffect(() => {
    let mounted = true;

    const loadReplayAssets = async () => {
      if (!replays.length) {
        if (mounted) {
          setReplayAssetsById({});
        }
        return;
      }

      const token = localStorage.getItem('token');
      if (!token) return;

      const missingReplayIds = replays
        .map((replay) => replay.sessionId)
        .filter((sessionId) => sessionId && !replayAssetsById[sessionId]);

      if (missingReplayIds.length === 0) return;

      const nextAssets = await Promise.all(
        missingReplayIds.map(async (sessionId) => {
          try {
            const response = await fetch(apiUrl(`/api/sessions/${sessionId}/downloads`), {
              headers: { Authorization: `Bearer ${token}` }
            });

            if (!response.ok) {
              return [sessionId, {}];
            }

            const data = await response.json();
            return [sessionId, { downloads: data?.downloads || null }];
          } catch (error) {
            return [sessionId, {}];
          }
        })
      );

      if (mounted) {
        setReplayAssetsById((current) => {
          const next = { ...current };

          nextAssets.forEach(([sessionId, assets]) => {
            if (sessionId) {
              next[sessionId] = assets;
            }
          });

          return next;
        });
      }
    };

    loadReplayAssets();

    return () => {
      mounted = false;
    };
  }, [replays, replayAssetsById]);

  useEffect(() => {
    let mounted = true;

    const loadMaterials = async () => {
      setMaterialsLoading(true);

      try {
        const nextMaterials = await getCourseMaterials(courseId, {
          page: materialsPage,
          pageSize: materialsPageSize
        });

        if (!mounted) return;
        setMaterials(nextMaterials.materials);
        setMaterialsTotal(nextMaterials.totalCount);
      } catch (err) {
        if (mounted) {
          setError(err.message || 'Unable to load materials right now.');
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
  }, [courseId, materialsPage, materialsPageSize]);

  const materialGroups = useMemo(() => {
    const groups = {};
    materials.forEach((material) => {
      const week = Number.isFinite(material.week) ? material.week : 0;
      if (!groups[week]) groups[week] = [];
      groups[week].push(material);
    });

    return Object.keys(groups)
      .map((value) => Number(value))
      .sort((a, b) => a - b)
      .map((week) => ({ week, rows: groups[week] }));
  }, [materials]);

  const liveSession = useMemo(() => {
    return sessions.find((session) => session.status === 'live') || null;
  }, [sessions]);

  const replayEntries = useMemo(() => {
    return replays.map((replay) => mergeReplayAssets(replay, replayAssetsById[replay.sessionId] || {}));
  }, [replays, replayAssetsById]);

  const courseTabs = useMemo(
    () => [
      { id: 'history', label: 'Session History', count: replayTotal },
      { id: 'materials', label: 'Materials', count: materialsTotal }
    ],
    [materialsTotal, replayTotal]
  );

  const courseDetailsSubtitle = course
    ? [course.code ? `Course code ${course.code}` : null, course.instructor || null].filter(Boolean).join(' · ') || 'Course workspace'
    : 'Course workspace';

  const downloadMaterial = (material) => {
    if (!material.id || !material.canDownload) return;
    downloadFile(material.id, material.fileName);
  };

  const openSession = (session) => {
    const sessionId = session.id || session.sessionId;
    if (!sessionId) return;
    navigate(`/viewer/${sessionId}`);
  };

  const watchReplay = (replay) => {
    if (!replay.sessionId) return;
    navigate(`/viewer/${replay.sessionId}`);
  };

  return (
    <AppShell
      title={course?.name || 'Course'}
      subtitle={courseDetailsSubtitle}
    >
      {error ? (
        <Card className="course-alert-card">
          <p className="student-error-text">{error}</p>
          <button type="button" className="btn btn-outline" onClick={() => setReloadToken((value) => value + 1)}>
            Retry
          </button>
        </Card>
      ) : null}

      {loading ? (
        <Card>
          <p className="student-muted-text">Loading course workspace...</p>
        </Card>
      ) : null}

      {!loading && !error && !course ? (
        <Card>
          <h3 className="student-empty-title">Course not found</h3>
          <p className="student-muted-text">This course is not part of your current enrolment list.</p>
        </Card>
      ) : null}

      {!loading && !error && course ? (
        <div className="course-page-stack">
          <CourseHero
            course={course}
            instructor={course?.instructor}
            liveCount={liveSession ? 1 : 0}
            materialsCount={materialsTotal}
            sessionsCount={sessionsTotal}
            replaysCount={replayTotal}
          />

          {liveSession ? (
            <Card className="course-live-banner">
              <div className="course-live-banner__copy">
                <div className="course-live-banner__eyebrow">
                  <span className="course-live-banner__dot" aria-hidden="true" />
                  Live session in progress
                </div>
                <h3 className="course-live-banner__title">{liveSession.title || 'Live session'}</h3>
                <p className="course-live-banner__meta">
                  {formatHKT(liveSession.startedAt) || 'Session started'}
                </p>
              </div>

              <button type="button" className="btn btn-primary course-live-banner__action" onClick={() => openSession(liveSession)}>
                Join Live Session
              </button>
            </Card>
          ) : null}

          <TabNav tabs={courseTabs} activeTab={activeTab} onChange={handleTabChange} />

          {activeTab === 'history' ? (
            <SessionsTab
              replays={replayEntries}
              replayLoading={historyLoading}
              replayError={''}
              replayPage={replayPage}
              replayPageSize={replayPageSize}
              replayTotal={replayTotal}
              onReplayPageChange={(nextPage) => setReplayPage(Math.max(1, nextPage))}
              onReplayPageSizeChange={(nextSize) => {
                setReplayPage(1);
                setReplayPageSize(nextSize);
              }}
              onWatchReplay={watchReplay}
              downloadFile={downloadFile}
              isDownloading={isDownloading}
            />
          ) : null}

          {activeTab === 'materials' ? (
            <MaterialsTab
              loading={materialsLoading}
              error={''}
              groups={materialGroups}
              totalCount={materialsTotal}
              page={materialsPage}
              pageSize={materialsPageSize}
              onPageChange={(nextPage) => setMaterialsPage(Math.max(1, nextPage))}
              onPageSizeChange={(nextSize) => {
                setMaterialsPage(1);
                setMaterialsPageSize(nextSize);
              }}
              onDownload={downloadMaterial}
              isDownloading={isDownloading}
            />
          ) : null}
        </div>
      ) : null}
    </AppShell>
  );
}
