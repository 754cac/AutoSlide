import React from 'react';

const LABELS = {
  live: 'Live',
  upcoming: 'Upcoming',
  released: 'Released',
  locked: 'Locked',
  replay: 'Replay'
};

export default function StatusBadge({ status = 'released', label }) {
  const normalized = String(status).toLowerCase();
  const text = label || LABELS[normalized] || status;
  return <span className={`status-badge status-badge--${normalized}`}>{text}</span>;
}
