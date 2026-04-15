import React, { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import TeacherDashboardShell from "../components/instructor-workspace/TeacherDashboardShell";
import Card from "../components/ui/Card";
import PaginationControls from "../components/ui/PaginationControls";
import CreateCourseModal from "../components/modals/CreateCourseModal";
import { formatHKT } from "../utils/dateUtils";
import { getTeacherDashboardData } from "../services/teacherApi";
import { clampPage, getPaginationState } from "../utils/pagination";
import useDocumentTitle from "../hooks/useDocumentTitle";

function getTeacherDashboardDataFallback() {
  return {
    user: null,
    courses: [],
    recentSessions: [],
    stats: {
      courses: 0,
      students: 0,
      materials: 0,
      liveSessions: 0,
      drafts: 0,
    },
    totalCount: 0,
  };
}

type TeacherCourse = {
  Id?: string | number;
  CourseId?: string | number;
  id?: string | number;
  courseId?: string | number;
  status?: string;
  code?: string;
  name?: string;
  studentCount?: number;
  materialCount?: number;
  latestSessionAt?: string | null;
};

function getCourseId(course?: TeacherCourse | null) {
  return course?.id || course?.Id || course?.courseId || course?.CourseId || null;
}

function TeacherStatCard({ label, value, hint }: { label: string; value: React.ReactNode; hint?: React.ReactNode }) {
  return (
    <Card className="teacher-stat-card">
      <p className="teacher-stat-card__label">{label}</p>
      <h3 className="teacher-stat-card__value">{value}</h3>
      {hint ? <p className="teacher-stat-card__hint">{hint}</p> : null}
    </Card>
  );
}

export default function TeacherDashboard() {
  const navigate = useNavigate();
  const [coursesData, setCoursesData] = useState({
    user: null,
    courses: [],
    recentSessions: [],
    stats: {
      courses: 0,
      students: 0,
      materials: 0,
      liveSessions: 0,
      drafts: 0,
    },
    totalCount: 0,
  });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [reloadToken, setReloadToken] = useState(0);
  const [showCreateCourse, setShowCreateCourse] = useState(false);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);

  useDocumentTitle("Dashboard | AutoSlide");

  useEffect(() => {
    let mounted = true;

    const loadDashboard = async () => {
      setLoading(true);
      setError("");

      try {
        const nextData = await getTeacherDashboardData();
        if (!mounted) return;
        setCoursesData(nextData);
      } catch (loadError) {
        if (!mounted) return;
        setCoursesData(getTeacherDashboardDataFallback());
        setError(loadError.message || "Unable to load instructor workspace.");
      } finally {
        if (mounted) {
          setLoading(false);
        }
      }
    };

    loadDashboard();

    return () => {
      mounted = false;
    };
  }, [reloadToken]);

  const userName =
    coursesData.user?.name || coursesData.user?.fullName || "Instructor";
  const courses: TeacherCourse[] = Array.isArray(coursesData.courses) ? coursesData.courses : [];
  const stats = coursesData.stats || {
    courses: 0,
    students: 0,
    materials: 0,
    liveSessions: 0,
    drafts: 0,
  };
  const totalCount = courses.length;
  const pagination = useMemo(
    () => getPaginationState(totalCount, page, pageSize),
    [totalCount, page, pageSize],
  );
  const visibleCourses = courses.slice(
    (pagination.currentPage - 1) * pagination.pageSize,
    pagination.currentPage * pagination.pageSize,
  );
  const liveSessionCount = stats.liveSessions || 0;
  const draftCount = stats.drafts || 0;

  const summaryLine = useMemo(() => {
    if (error)
      return "We could not load your workspace. Try refreshing the page.";
    if (loading) return "Loading your workspace...";
    if (liveSessionCount > 0)
      return `${liveSessionCount} live session${liveSessionCount === 1 ? "" : "s"} running now.`;
    if (draftCount > 0)
      return `${draftCount} course${draftCount === 1 ? "" : "s"} still need setup.`;
    return "Everything is ready for your next class.";
  }, [draftCount, error, liveSessionCount, loading]);

  const openCourse = (course: TeacherCourse) => {
    const courseId = getCourseId(course);
    if (!courseId) return;
    navigate(`/teacher/courses/${courseId}`);
  };

  const refreshDashboard = () => {
    setReloadToken((value) => value + 1);
  };

  const handleCourseCreated = async (createdCourse: TeacherCourse | null) => {
    const createdCourseId = getCourseId(createdCourse);

    if (createdCourseId) {
      navigate(`/teacher/courses/${createdCourseId}`);
      return;
    }

    refreshDashboard();
  };

  useEffect(() => {
    setPage((currentPage) => {
      return clampPage(currentPage, pagination.totalPages);
    });
  }, [pagination.totalPages]);

  return (
    <TeacherDashboardShell
      primaryAction={
        <button
          type="button"
          className="btn btn-primary"
          onClick={() => setShowCreateCourse(true)}
        >
          + Create Course
        </button>
      }
      user={coursesData.user}
    >
      <div className="teacher-dashboard">
        <section className="teacher-hero">
          <div>
            <p className="teacher-hero__eyebrow">Instructor Workspace</p>
            <h2 className="teacher-hero__title">Welcome back, {userName}.</h2>
            <p className="teacher-hero__subtitle">{summaryLine}</p>
          </div>
          <div className="teacher-hero__meta">
            <span className="teacher-course-chip teacher-course-chip--soft">
              {stats.courses} Courses
            </span>
            <span className="teacher-course-chip teacher-course-chip--soft">
              {stats.liveSessions} Live
            </span>
          </div>
        </section>

        <section className="teacher-course-section">
          <div className="dashboard-section__header teacher-section-header">
            <div>
              <h2 className="dashboard-section__title">My Courses</h2>
            </div>
          </div>

          {error ? (
            <div role="alert">
              <Card
              className="teacher-state-card teacher-state-card--error"
              >
                <div>
                  <p className="teacher-state-card__title">
                    Unable to load courses
                  </p>
                  <p className="teacher-state-card__text">{error}</p>
                </div>
                <button
                  type="button"
                  className="btn btn-outline"
                  onClick={refreshDashboard}
                >
                  Retry
                </button>
              </Card>
            </div>
          ) : null}

          {loading ? (
            <div className="teacher-skeleton-grid" aria-label="Loading courses">
              {Array.from({ length: 4 }).map((_, index) => (
                <Card key={index} className="teacher-skeleton-card">
                  <div className="teacher-skeleton-line teacher-skeleton-line--title" />
                  <div className="teacher-skeleton-line" />
                  <div className="teacher-skeleton-line teacher-skeleton-line--button" />
                </Card>
              ))}
            </div>
          ) : null}

          {!loading && !error && visibleCourses.length === 0 ? (
            <Card className="teacher-state-card">
              <div>
                <p className="teacher-state-card__title">No courses yet</p>
                <p className="teacher-state-card__text">
                  Create your first course to start managing rosters, materials,
                  and sessions.
                </p>
              </div>
              <button
                type="button"
                className="btn btn-primary"
                onClick={() => setShowCreateCourse(true)}
              >
                Create Course
              </button>
            </Card>
          ) : null}

          {!loading && visibleCourses.length > 0 ? (
            <div
              className="teacher-course-grid"
              role="region"
              aria-label="Instructor courses"
            >
              {visibleCourses.map((course, index) => (
                <Card
                  key={String(getCourseId(course) || course.code || course.name || index)}
                  className={`teacher-course-card${course.status === "Live" ? " teacher-course-card--live" : ""}`}
                >
                  <div className="teacher-course-card__header">
                    <div>
                      <p className="teacher-course-card__code">{course.code || "COURSE"}</p>
                      <h3 className="teacher-course-card__title">{course.name}</h3>
                    </div>
                    {course.status === "Live" ? (
                      <span className="teacher-course-chip teacher-course-chip--live">
                        Live
                      </span>
                    ) : null}
                  </div>

                  <div className="teacher-course-card__meta-grid">
                    <div>
                      <span className="teacher-course-card__meta-label">Students</span>
                      <span className="teacher-course-card__meta-value">
                        {course.studentCount}
                      </span>
                    </div>
                    <div>
                      <span className="teacher-course-card__meta-label">Materials</span>
                      <span className="teacher-course-card__meta-value">
                        {course.materialCount}
                      </span>
                    </div>
                    <div>
                      <span className="teacher-course-card__meta-label">
                        Latest session
                      </span>
                      <span className="teacher-course-card__meta-value">
                        {course.latestSessionAt
                          ? formatHKT(course.latestSessionAt)
                          : "No sessions yet"}
                      </span>
                    </div>
                  </div>

                  <div className="teacher-course-card__actions">
                    <button
                      type="button"
                      className="btn btn-primary"
                      onClick={() => openCourse(course)}
                    >
                      Open Course
                    </button>
                  </div>
                </Card>
              ))}
            </div>
          ) : null}

          {totalCount > 0 ? (
            <PaginationControls
              page={pagination.currentPage}
              pageSize={pagination.pageSize}
              totalCount={pagination.totalItems}
              itemCount={visibleCourses.length}
              onPageChange={(nextPage) =>
                setPage(clampPage(nextPage, pagination.totalPages))
              }
              onPageSizeChange={(nextPageSize) => {
                setPage(1);
                setPageSize(nextPageSize);
              }}
            />
          ) : null}
        </section>
      </div>

      {showCreateCourse ? (
        <CreateCourseModal
          onClose={() => setShowCreateCourse(false)}
          onCreated={handleCourseCreated}
        />
      ) : null}
    </TeacherDashboardShell>
  );
}
