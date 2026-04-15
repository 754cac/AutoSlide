import React from 'react';
import LiveBadge from './LiveBadge';
import StatusLine from './StatusLine';

export default function CourseCard({
  course,
  courseCode,
  courseName,
  instructor,
  coverUrl,
  isLive = false,
  statusText,
  actionLabel,
  onCardClick,
  onAction,
  badgeLabel,
  metaItems = [],
  description,
  className = ''
}) {
  const resolvedCourse = course || {
    courseCode,
    courseName,
    instructor,
    coverUrl
  };

  const resolvedCourseCode = resolvedCourse.courseCode || resolvedCourse.code || '';
  const resolvedCourseName = resolvedCourse.courseName || resolvedCourse.name || 'Course';
  const resolvedInstructor = resolvedCourse.instructor || resolvedCourse.teacherName || 'Instructor';
  const resolvedCoverUrl = resolvedCourse.coverUrl || coverUrl || '';
  const resolvedDescription = description || (isLive ? 'Slides and transcript are updating in real time.' : '');
  const resolvedStatusText = statusText || (isLive ? 'Live session is active now' : resolvedCourse.statusText || '');
  const resolvedActionLabel = actionLabel || (isLive ? 'Join Live Session' : 'Open Course');
  const resolvedBadgeLabel = badgeLabel || 'LIVE NOW';
  const resolvedMetaItems = Array.isArray(metaItems)
    ? metaItems.filter((item) => item && item.label && item.value !== undefined && item.value !== null)
    : [];
  const hasMetaItems = resolvedMetaItems.length > 0;

  const handleCardKeyDown = (event) => {
    if (!onCardClick) return;

    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      onCardClick(resolvedCourse);
    }
  };

  const handleCardClick = () => {
    if (onCardClick) {
      onCardClick(resolvedCourse);
    }
  };

  const handleActionClick = (event) => {
    event.stopPropagation();

    if (onAction) {
      onAction(resolvedCourse);
    } else if (onCardClick) {
      onCardClick(resolvedCourse);
    }
  };

  return (
    <article
      className={`dashboard-course-card${onCardClick ? ' dashboard-course-card--clickable' : ''}${isLive ? ' dashboard-course-card--live' : ''} ${className}`.trim()}
      role={onCardClick ? 'link' : undefined}
      tabIndex={onCardClick ? 0 : undefined}
      aria-label={`${resolvedCourseName}${resolvedStatusText ? `, ${resolvedStatusText}` : ''}`}
      onClick={handleCardClick}
      onKeyDown={handleCardKeyDown}
    >
      <div className="dashboard-course-card__cover">
        <img src={resolvedCoverUrl} alt={`${resolvedCourseName} cover`} className="dashboard-course-card__cover-image" />
        <div className="dashboard-course-card__overlay" />
        <div className="dashboard-course-card__badge-slot">
          {isLive ? (
            <LiveBadge label={resolvedBadgeLabel} />
          ) : (
            <span className="dashboard-course-pill">{resolvedCourseCode}</span>
          )}
        </div>
      </div>

      <div className="dashboard-course-card__body">
        <div className="dashboard-course-card__copy">
          <h3 className="dashboard-course-card__title">{resolvedCourseName}</h3>
          <p className="dashboard-course-card__instructor">{resolvedInstructor}</p>

          {hasMetaItems ? (
            <dl className="dashboard-course-card__meta">
              {resolvedMetaItems.map((item) => (
                <div key={item.label} className="dashboard-course-card__meta-item">
                  <dt className="dashboard-course-card__meta-label">{item.label}</dt>
                  <dd className="dashboard-course-card__meta-value">{item.value}</dd>
                </div>
              ))}
            </dl>
          ) : null}

          {!hasMetaItems && resolvedDescription ? <p className="dashboard-course-card__description">{resolvedDescription}</p> : null}
          {!hasMetaItems && resolvedStatusText ? <StatusLine text={resolvedStatusText} /> : null}
        </div>

        <button
          type="button"
          className={`dashboard-btn ${isLive ? 'dashboard-btn--primary' : 'dashboard-btn--surface'}`}
          onClick={handleActionClick}
        >
          {resolvedActionLabel}
        </button>
      </div>
    </article>
  );
}
