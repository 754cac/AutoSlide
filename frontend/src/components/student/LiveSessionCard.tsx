import React from 'react';
import { formatHKT } from '../../utils/dateUtils';
import Card from '../ui/Card';
import StatusBadge from '../ui/StatusBadge';

export default function LiveSessionCard({ session, onJoin }) {
  return (
    <Card className="student-live-session-card" interactive>
      <div className="student-live-session-card__header">
        <StatusBadge status="live" />
        <span className="student-meta-text">{session.courseName}</span>
      </div>
      <h3 className="student-live-session-card__title">{session.title || 'Live session in progress'}</h3>
      <p className="student-muted-text">
        {session.startedAt ? `Started ${formatHKT(session.startedAt)}` : 'Join now to follow transcript and slide context.'}
      </p>
      <button type="button" className="dashboard-btn dashboard-btn--primary" onClick={() => onJoin(session)}>
        Enter Live Session
      </button>
    </Card>
  );
}
