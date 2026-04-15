import React from 'react';
import Card from '../ui/Card';
import PaginationControls from '../ui/PaginationControls';
import MaterialRow from './MaterialRow';
import StudentSectionCard from './StudentSectionCard';

export default function MaterialsTab({
  loading,
  error,
  groups,
  totalCount,
  page,
  pageSize,
  onPageChange,
  onPageSizeChange,
  onDownload,
  isDownloading
}) {
  const itemCount = groups.reduce((sum, group) => sum + group.rows.length, 0);
  const showLoadingHint = loading && itemCount === 0;

  return (
    <StudentSectionCard
      title="Materials"
      subtitle="Weekly materials grouped by release order. Locked items remain muted and non-clickable."
      className="course-materials-panel course-detail-panel--materials"
      footer={(
        <PaginationControls
          page={page}
          pageSize={pageSize}
          totalCount={totalCount}
          itemCount={itemCount}
          onPageChange={onPageChange}
          onPageSizeChange={onPageSizeChange}
        />
      )}
    >
      {error ? <p className="student-error-text">{error}</p> : null}

      {showLoadingHint ? <p className="student-muted-text">Loading materials...</p> : null}

      {!loading && groups.length === 0 ? (
        <div className="course-empty-state">
          <p className="course-empty-state__title">No materials available yet</p>
          <p className="course-empty-state__text">Materials will appear here once they are uploaded and become visible to students.</p>
        </div>
      ) : null}

      {groups.length > 0 ? (
        <div className="course-material-scroll" role="region" aria-label="Course materials">
          <div className="course-material-groups">
            {groups.map((group) => (
              <Card key={group.week} className="course-material-group-card">
                <h3 className="student-subsection-title">Week {group.week || 'Unscheduled'}</h3>
                <div className="course-material-list">
                  {group.rows.map((material) => (
                    <MaterialRow
                      key={material.id}
                      material={material}
                      onDownload={onDownload}
                      isDownloading={isDownloading}
                    />
                  ))}
                </div>
              </Card>
            ))}
          </div>
        </div>
      ) : null}
    </StudentSectionCard>
  );
}
