import React from 'react';
import { formatHKT } from '../../utils/dateUtils';
import CourseCard from './CourseCard';

// Data needed: section heading/subtitle and an array of currently live courses.
export default function LiveNowSection({ title, subtitle, sessions, onOpenCourse, onEnterLive }) {
  if (!sessions.length) return null;

  return (
    <section className="dashboard-section">
      <div className="dashboard-section__header">
        <h2 className="dashboard-section__title">{title}</h2>
        <p className="dashboard-section__subtitle">{subtitle}</p>
      </div>

      <div className="dashboard-grid">
        {sessions.map((session, index) => (
          <CourseCard
            key={session.id || session.courseId || session.sessionId || `${session.courseCode || session.code || 'live'}-${session.courseName || session.name || index}`}
            course={session}
            isLive
            statusText="Live session is active now"
            actionLabel="Join Live Session"
            metaItems={[
              { label: 'Latest session', value: session.startedAt ? `Started ${formatHKT(session.startedAt)}` : 'Live now' },
              { label: 'Materials released', value: session.materialsCount || 0 }
            ]}
            onCardClick={() => onOpenCourse?.(session)}
            onAction={() => onEnterLive(session)}
            badgeLabel="LIVE NOW"
          />
        ))}
      </div>
    </section>
  );
}
