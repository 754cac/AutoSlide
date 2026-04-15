import React from 'react';
import { useNavigate } from 'react-router-dom';

const RichCourseCard = ({ course }) => {
  const navigate = useNavigate();
  const isLive = !!(course?.activeSessionId || course?.ActiveSessionId || course?.sessionId || course?.SessionId);
  const liveId = course?.activeSessionId || course?.ActiveSessionId || course?.sessionId || course?.SessionId;

  return (
    <article className={`dashboard-course-card${isLive ? ' dashboard-course-card--live' : ''}`}>
      <div className="dashboard-course-card__cover">
        <div className="dashboard-course-card__overlay" />
        <div className="dashboard-course-card__badge-slot">
          <span className={isLive ? 'dashboard-live-badge' : 'dashboard-course-pill'}>
            {isLive ? 'Live now' : (course?.code || 'Course')}
          </span>
        </div>
      </div>

      <div className="dashboard-course-card__body">
        <div className="dashboard-course-card__copy">
          <h3 className="dashboard-course-card__title">{course?.name || course?.title || course?.Name || 'Untitled Course'}</h3>
          <p className="dashboard-course-card__instructor">{course?.description ? (course.description.length > 120 ? `${course.description.slice(0, 120)}…` : course.description) : 'Course workspace'}</p>
        </div>

        <button
          type="button"
          className={`dashboard-btn ${isLive ? 'dashboard-btn--primary' : 'dashboard-btn--surface'}`}
          onClick={() => (isLive ? navigate(`/viewer/${liveId}`) : navigate(`/courses/${course?.id || course?.Id}`))}
        >
          {isLive ? 'Join Live Session' : 'View Course Details'}
        </button>
      </div>
    </article>
  );
};

export default RichCourseCard;
