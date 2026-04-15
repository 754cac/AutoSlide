import React from 'react';

// Data needed: latest replay details and watch/download callbacks.
export default function ContinueBanner({
  title,
  sessionTitle,
  courseName,
  replayEta,
  onWatch,
  onDownload
}) {
  return (
    <section className="dashboard-continue-banner">
      <div>
        <p className="dashboard-continue-banner__kicker">{title}</p>
        <h3 className="dashboard-continue-banner__title">{sessionTitle}</h3>
        <p className="dashboard-continue-banner__meta">
          {courseName}
          {replayEta ? ` · Estimated replay time: ${replayEta}` : ' · Replay available now'}
        </p>
      </div>

      <div className="dashboard-continue-banner__actions">
        <button type="button" className="dashboard-btn dashboard-btn--primary" onClick={onWatch}>
          Watch Replay
        </button>
        <button type="button" className="dashboard-btn dashboard-btn--surface" onClick={onDownload}>
          Download Slides
        </button>
      </div>
    </section>
  );
}
