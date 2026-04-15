import React, { useEffect, useMemo, useState } from 'react';
import CourseCard from './CourseCard';
import PaginationControls from '../ui/PaginationControls';
import { clampPage, getPaginationState } from '../../utils/pagination';
import { formatLocalDateTime } from '../../utils/dateUtils';

// Data needed: enrolled courses, optional live course ids, and course-detail/live callbacks.
export default function CourseListContainer({
  title,
  subtitle,
  courses = [],
  highlightCourseId = null,
  highlightCourseIds = [],
  emptyTitle = 'No enrolled courses yet',
  emptyText = 'Courses you join will appear here once they are available.',
  onOpenCourse,
  onEnterLive,
  initialPageSize = 25,
  loading = false,
  error = '',
  onRetry
}) {
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(initialPageSize);

  const highlightSet = useMemo(() => {
    const ids = Array.isArray(highlightCourseIds) ? [...highlightCourseIds] : [];
    if (highlightCourseId) {
      ids.push(highlightCourseId);
    }
    return new Set(ids.map((value) => String(value)));
  }, [highlightCourseId, highlightCourseIds]);

  const orderedCourses = useMemo(() => {
    const items = [...courses];

    if (highlightSet.size > 0) {
      items.sort((left, right) => {
        const leftId = left.courseId || left.id;
        const rightId = right.courseId || right.id;
        const leftLive = highlightSet.has(String(leftId));
        const rightLive = highlightSet.has(String(rightId));

        if (leftLive && !rightLive) return -1;
        if (!leftLive && rightLive) return 1;
        return 0;
      });
    }

    return items;
  }, [courses, highlightSet]);

  const totalCount = orderedCourses.length;
  const pagination = useMemo(() => getPaginationState(totalCount, page, pageSize), [totalCount, page, pageSize]);
  const visibleCourses = orderedCourses.slice((pagination.currentPage - 1) * pagination.pageSize, pagination.currentPage * pagination.pageSize);

  useEffect(() => {
    setPage((currentPage) => clampPage(currentPage, pagination.totalPages));
  }, [pagination.totalPages]);

  const handlePageChange = (nextPage) => {
    setPage(clampPage(nextPage, pagination.totalPages));
  };

  const handlePageSizeChange = (nextPageSize) => {
    setPage(1);
    setPageSize(nextPageSize);
  };

  const handleOpenCourse = (course) => {
    if (onOpenCourse) {
      onOpenCourse(course);
    }
  };

  const handleEnterLive = (course) => {
    if (onEnterLive && (course.activeSessionId || course.sessionId)) {
      onEnterLive(course);
      return;
    }

    handleOpenCourse(course);
  };

  return (
    <section className="dashboard-section dashboard-course-list">
      <div className="dashboard-section__header dashboard-course-list__header">
        <div>
          <h2 className="dashboard-section__title">{title}</h2>
          <p className="dashboard-section__subtitle">{subtitle}</p>
        </div>
      </div>

      {error ? (
        <div className="dashboard-state-card dashboard-state-card--error" role="alert">
          <p className="dashboard-state-card__title">Unable to load courses</p>
          <p className="dashboard-state-card__text">{error}</p>
          {onRetry ? (
            <button type="button" className="dashboard-btn dashboard-btn--surface" onClick={onRetry}>
              Retry
            </button>
          ) : null}
        </div>
      ) : null}

      {loading ? (
        <div className="dashboard-skeleton-grid" aria-label="Loading courses">
          {Array.from({ length: 3 }).map((_, index) => (
            <div key={index} className="dashboard-skeleton-card">
              <div className="dashboard-skeleton-card__cover" />
              <div className="dashboard-skeleton-card__body">
                <div className="dashboard-skeleton-line dashboard-skeleton-line--title" />
                <div className="dashboard-skeleton-line" />
                <div className="dashboard-skeleton-line dashboard-skeleton-line--button" />
              </div>
            </div>
          ))}
        </div>
      ) : null}

      {!loading && !error && orderedCourses.length === 0 ? (
        <div className="dashboard-state-card" role="status">
          <p className="dashboard-state-card__title">{emptyTitle}</p>
          <p className="dashboard-state-card__text">{emptyText}</p>
        </div>
      ) : null}

      {visibleCourses.length > 0 ? (
        <div className="dashboard-course-list__scroll" role="region" aria-label="Enrolled courses">
          <div className="dashboard-grid dashboard-course-list__grid">
            {visibleCourses.map((course) => {
              const courseId = course.courseId || course.id;
              const isHighlighted = highlightSet.has(String(courseId));
              const isLive = isHighlighted || Boolean(course.activeSessionId || course.sessionId);
              const latestSessionText = isHighlighted
                ? 'Live now'
                : course.lastSession
                  ? formatLocalDateTime(course.lastSession)
                  : 'No previous session';

              return (
                <CourseCard
                  key={courseId}
                  course={course}
                  courseCode={course.courseCode}
                  courseName={course.courseName}
                  instructor={course.instructor}
                  coverUrl={course.coverUrl}
                  isLive={isLive}
                  statusText={isHighlighted
                    ? `Live session is active now · Materials released: ${course.materialsCount || 0}`
                    : course.lastSession
                      ? `Last session: ${formatLocalDateTime(course.lastSession)} · Materials released: ${course.materialsCount || 0}`
                      : `Materials released: ${course.materialsCount || 0}`}
                  actionLabel={isLive ? 'Join Live Session' : 'Open Course'}
                  metaItems={[
                    { label: 'Latest session', value: latestSessionText },
                    { label: 'Materials released', value: course.materialsCount || 0 }
                  ]}
                  onCardClick={handleOpenCourse}
                  onAction={isLive ? handleEnterLive : handleOpenCourse}
                  badgeLabel="LIVE NOW"
                  className={isHighlighted ? 'dashboard-course-card--highlighted' : ''}
                />
              );
            })}
          </div>
        </div>
      ) : null}

      {totalCount > 0 ? (
        <PaginationControls
          page={pagination.currentPage}
          pageSize={pagination.pageSize}
          totalCount={pagination.totalItems}
          itemCount={visibleCourses.length}
          onPageChange={handlePageChange}
          onPageSizeChange={handlePageSizeChange}
        />
      ) : null}
    </section>
  );
}
