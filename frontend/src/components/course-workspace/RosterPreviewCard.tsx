import React from 'react';
import Card from '../ui/Card';
import StatusBadge from '../ui/StatusBadge';

function normalizeRosterStatus(student) {
  if (student?.status === 1) return { status: 'released', label: 'Active' };
  if (student?.status === 0) return { status: 'upcoming', label: 'Pending Invite' };
  return { status: 'locked', label: student?.isRegistered ? 'Registered' : 'Unknown' };
}

export default function RosterPreviewCard({
  rosterItems = [],
  totalCount = 0,
  loading = false,
  error = '',
  onImportCsv,
  onInvite,
  onOpenRoster
}) {
  if (loading) {
    return (
      <Card className="course-roster-preview-card course-roster-preview-card--loading">
        <div className="course-roster-preview-card__header">
          <div>
            <div className="course-roster-preview-card__skeleton course-roster-preview-card__skeleton--title" />
            <div className="course-roster-preview-card__skeleton course-roster-preview-card__skeleton--subtitle" />
          </div>
          <div className="course-roster-preview-card__skeleton course-roster-preview-card__skeleton--button" />
        </div>
        <div className="course-roster-preview-card__skeleton course-roster-preview-card__skeleton--table" />
      </Card>
    );
  }

  return (
    <Card className="course-roster-preview-card">
      <div className="course-roster-preview-card__header">
        <div>
          <p className="course-roster-preview-card__eyebrow">Student Roster</p>
          <p className="course-roster-preview-card__subtitle">Manage enrollments, invitations, and preview the latest roster changes.</p>
        </div>
        <div className="course-roster-preview-card__actions">
          <button type="button" className="btn btn-outline" onClick={onImportCsv}>
            Import CSV
          </button>
          <button type="button" className="btn btn-primary" onClick={onInvite}>
            Email Invite
          </button>
        </div>
      </div>

      {error ? <p className="course-roster-preview-card__error">{error}</p> : null}

      {!error && rosterItems.length === 0 ? (
        <div className="course-roster-preview-card__empty">
          <p className="course-roster-preview-card__empty-title">No roster entries yet</p>
          <p className="course-roster-preview-card__empty-text">Enroll students to populate this workspace.</p>
        </div>
      ) : null}

      {rosterItems.length > 0 ? (
        <div className="course-roster-preview-card__table-wrap">
          <table className="course-roster-preview-card__table">
            <thead>
              <tr>
                <th>Student</th>
                <th>Email</th>
                <th>Status</th>
                <th>Registration</th>
              </tr>
            </thead>
            <tbody>
              {rosterItems.map((student) => {
                const key = student.id || student.email;
                const status = normalizeRosterStatus(student);

                return (
                  <tr key={key}>
                    <td className="course-roster-preview-card__name">{student.name || '-'}</td>
                    <td className="course-roster-preview-card__email">{student.email || '-'}</td>
                    <td>
                      <StatusBadge status={status.status} label={status.label} />
                    </td>
                    <td className="course-roster-preview-card__registration">{student.isRegistered ? 'Registered' : 'Pending'}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      ) : null}

      <div className="course-roster-preview-card__footer">
        <span>{totalCount} students enrolled</span>
        <button type="button" className="course-roster-preview-card__link" onClick={onOpenRoster}>
          View full roster
        </button>
      </div>
    </Card>
  );
}
