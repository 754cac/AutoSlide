import React from 'react';

// Data needed: highlighted replay or material summary card content with CTA.
export default function BentoCard({
  title,
  sessionTitle,
  courseName,
  date,
  hasTranscript,
  hasSummary,
  actionLabel,
  onAction
}) {
  return (
    <article className="dashboard-bento-card">
      <p className="dashboard-bento-card__kicker">{title}</p>
      <h3 className="dashboard-bento-card__title">{sessionTitle}</h3>
      <p className="dashboard-bento-card__meta">{courseName} · {date}</p>

      <div className="dashboard-bento-card__chips">
        {hasTranscript ? <span className="dashboard-bento-chip">Transcript</span> : null}
        {hasSummary ? <span className="dashboard-bento-chip">Summary</span> : null}
      </div>

      <button type="button" className="dashboard-btn dashboard-btn--surface" onClick={onAction}>
        {actionLabel}
      </button>
    </article>
  );
}
