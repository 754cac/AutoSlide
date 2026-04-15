import React from 'react';
import { formatHKT } from '../../utils/dateUtils';
import Card from '../ui/Card';
import StatusBadge from '../ui/StatusBadge';

export default function ReleaseCard({ item, onOpenCourse }) {
  return (
    <Card className="student-release-card" interactive>
      <div className="student-release-card__header">
        <StatusBadge status={item.status} />
        <span className="student-meta-text">{item.courseName}</span>
      </div>
      <h3 className="student-release-card__title">{item.title || 'Course material'}</h3>
      <p className="student-muted-text">{item.releaseAt ? formatHKT(item.releaseAt) : 'Release schedule pending'}</p>
      <button type="button" className="btn btn-text" onClick={() => onOpenCourse(item.courseId)}>
        Open Course
      </button>
    </Card>
  );
}
