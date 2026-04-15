import React from 'react';

export default function LiveBadge({ label }) {
  return (
    <span className="dashboard-live-badge" aria-label={label}>
      <span className="dashboard-live-badge__dot" aria-hidden="true" />
      {label}
    </span>
  );
}
