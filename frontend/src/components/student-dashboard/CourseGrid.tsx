import React from 'react';
import CourseCard from './CourseCard';
import BentoCard from './BentoCard';
import { formatLocalDateTime } from '../../utils/dateUtils';

export default function CourseGrid({
  title,
  subtitle,
  courses,
  bento,
  onOpenCourse,
  onOpenReplay
}) {
  return (
    <section className="dashboard-section">
      <div className="dashboard-section__header">
        <h2 className="dashboard-section__title">{title}</h2>
        <p className="dashboard-section__subtitle">{subtitle}</p>
      </div>

      <div className="dashboard-grid">
        {courses.map((course) => (
          <CourseCard
            key={`${course.courseCode}-${course.courseName}`}
            courseCode={course.courseCode}
            courseName={course.courseName}
            instructor={course.instructor}
            coverUrl={course.coverUrl}
            isLive={false}
            statusText={`Last session: ${formatLocalDateTime(course.lastSession)} · Materials released: ${course.materialsCount}`}
            actionLabel="Open Course"
            onAction={() => onOpenCourse(course)}
          />
        ))}

        <div className="dashboard-bento-slot">
          <BentoCard
            title="Latest Replay"
            sessionTitle={bento.sessionTitle}
            courseName={bento.courseName}
            date={bento.date}
            hasTranscript={bento.hasTranscript}
            hasSummary={bento.hasSummary}
            actionLabel="Watch Now"
            onAction={onOpenReplay}
          />
        </div>
      </div>
    </section>
  );
}
