import React from 'react';
import Card from '../ui/Card';
import StatusBadge from '../ui/StatusBadge';

export default function CourseOverviewCard({ course, onOpenCourse, onJoinLive }) {
  return (
    <Card className="student-course-overview-card" interactive>
      <div className="student-course-overview-card__header">
        <p className="student-course-overview-card__code">{course.code || 'Course'}</p>
        {course.activeSessionId ? <StatusBadge status="live" /> : <StatusBadge status="released" label="Enrolled" />}
      </div>
      <h3 className="student-course-overview-card__title">{course.name}</h3>
      <p className="student-muted-text">{course.instructor || 'Instructor information will appear here.'}</p>
      <div className="student-course-overview-card__actions">
        <button type="button" className="btn btn-outline" onClick={() => onOpenCourse(course)}>
          View Course
        </button>
        {course.activeSessionId ? (
          <button type="button" className="btn btn-primary" onClick={() => onJoinLive(course)}>
            Join Live
          </button>
        ) : null}
      </div>
    </Card>
  );
}
