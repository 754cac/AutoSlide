import React from 'react';
import { formatHKT } from '../../utils/dateUtils';
import Card from '../ui/Card';
import StatusBadge from '../ui/StatusBadge';

export default function RecentReplayCard({ replay, onWatch }) {
  return (
    <Card className="student-replay-card" interactive>
      <div className="student-replay-card__header">
        <StatusBadge status="replay" />
        <span className="student-meta-text">{replay.courseName}</span>
      </div>
      <h3 className="student-replay-card__title">{replay.title || 'Session replay'}</h3>
      <p className="student-muted-text">{replay.startedAt ? formatHKT(replay.startedAt) : 'Ready to replay'}</p>
      <button type="button" className="btn btn-outline" onClick={() => onWatch(replay)}>
        Continue Replay
      </button>
    </Card>
  );
}
