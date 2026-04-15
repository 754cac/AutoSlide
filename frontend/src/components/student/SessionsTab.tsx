import React, { useMemo } from 'react';
import PaginationControls from '../ui/PaginationControls';
import ReplayRow from './ReplayRow';
import StudentSectionCard from './StudentSectionCard';

export default function SessionsTab({
  replays,
  replayLoading,
  replayError,
  replayPage,
  replayPageSize,
  replayTotal,
  onReplayPageChange,
  onReplayPageSizeChange,
  onWatchReplay,
  downloadFile,
  isDownloading
}) {
  const replayRows = useMemo(() => replays || [], [replays]);
  const showLoadingHint = replayLoading && replayRows.length === 0;

  return (
    <StudentSectionCard
      title="Session History"
      subtitle="Completed sessions, replay actions, and transcript access where available."
      className="course-history-panel course-detail-panel--history"
      footer={(
        <PaginationControls
          page={replayPage}
          pageSize={replayPageSize}
          totalCount={replayTotal}
          itemCount={replayRows.length}
          onPageChange={onReplayPageChange}
          onPageSizeChange={onReplayPageSizeChange}
        />
      )}
    >
      <div className="course-history-scroll" role="region" aria-label="Session history">
        {replayError ? <p className="student-error-text">{replayError}</p> : null}
        {showLoadingHint ? <p className="student-muted-text">Loading replay history...</p> : null}

        {!replayLoading && replayRows.length === 0 ? (
          <div className="course-empty-state">
            <p className="course-empty-state__title">No replay sessions yet</p>
            <p className="course-empty-state__text">Completed sessions will appear here once they are available.</p>
          </div>
        ) : null}

        {replayRows.length > 0 ? (
          <div className="course-history-list">
            {replayRows.map((replay) => (
              <ReplayRow
                key={`${replay.sessionId}-${replay.startedAt || 'history'}`}
                replay={replay}
                onWatch={onWatchReplay}
                downloadFile={downloadFile}
                isDownloading={isDownloading}
              />
            ))}
          </div>
        ) : null}
      </div>
    </StudentSectionCard>
  );
}
