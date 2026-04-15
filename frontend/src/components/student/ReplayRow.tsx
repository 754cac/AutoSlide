import React, { useEffect, useMemo, useRef, useState } from 'react';
import ReactDOM from 'react-dom';
import { formatHKT } from '../../utils/dateUtils';
import StatusBadge from '../ui/StatusBadge';

export default function ReplayRow({ replay, onWatch, downloadFile, isDownloading }) {
  const startedAt = replay.startedAt ? new Date(replay.startedAt) : null;
  const day = startedAt ? startedAt.getDate() : '';
  const month = startedAt
    ? startedAt.toLocaleString('en-HK', { month: 'short', timeZone: 'Asia/Hong_Kong' })
    : '';
  const downloadOptions = Array.isArray(replay.downloadOptions) ? replay.downloadOptions : [];
  const canDownload = downloadOptions.length > 0;
  const buttonRef = useRef(null);
  const [isMenuOpen, setIsMenuOpen] = useState(false);
  const [anchorRect, setAnchorRect] = useState(null);

  useEffect(() => {
    if (!isMenuOpen) return undefined;

    const updateAnchorRect = () => {
      const trigger = buttonRef.current;
      if (!trigger) return;
      setAnchorRect(trigger.getBoundingClientRect());
    };

    const handleKeyDown = (event) => {
      if (event.key === 'Escape') {
        setIsMenuOpen(false);
      }
    };

    updateAnchorRect();
    window.addEventListener('resize', updateAnchorRect);
    window.addEventListener('scroll', updateAnchorRect, true);
    window.addEventListener('keydown', handleKeyDown);

    return () => {
      window.removeEventListener('resize', updateAnchorRect);
      window.removeEventListener('scroll', updateAnchorRect, true);
      window.removeEventListener('keydown', handleKeyDown);
    };
  }, [isMenuOpen]);

  const position = useMemo(() => {
    if (!anchorRect) return null;

    const menuWidth = Math.max(240, Math.min(340, window.innerWidth - 32));
    const estimatedHeight = Math.max(152, downloadOptions.length * 44 + 12);
    const spaceBelow = window.innerHeight - anchorRect.bottom;
    const spaceAbove = anchorRect.top;
    const openUpward = spaceBelow < estimatedHeight && spaceAbove > spaceBelow;
    const left = Math.max(16, Math.min(anchorRect.left, window.innerWidth - menuWidth - 16));
    const top = openUpward
      ? Math.max(16, anchorRect.top - estimatedHeight - 8)
      : Math.min(window.innerHeight - estimatedHeight - 16, anchorRect.bottom + 8);
    const maxHeight = openUpward
      ? Math.max(160, spaceAbove - 16)
      : Math.max(160, spaceBelow - 16);

    return {
      left,
      top,
      width: menuWidth,
      maxHeight,
      placement: openUpward ? 'up' : 'down'
    };
  }, [anchorRect, downloadOptions.length]);

  const handleDownload = (option) => {
    if (!replay.sessionId || !option?.url) return;

    downloadFile(replay.sessionId, `${replay.title || 'session'}.${option.extension}`, option.url);
    setIsMenuOpen(false);
  };

  const handleToggleDownloadMenu = () => {
    if (!canDownload || isDownloading) return;
    setIsMenuOpen((value) => !value);
  };

  return (
    <article className="course-history-row">
      <div className="course-history-row__date">
        <span className="course-history-row__month">{month || 'Replay'}</span>
        <span className="course-history-row__day">{day || '—'}</span>
      </div>

      <div className="course-history-row__body">
        <p className="course-history-row__title">{replay.title || 'Session replay'}</p>
        <p className="course-history-row__meta">
          {replay.courseName || 'Course'}
          {replay.startedAt ? ` · ${formatHKT(replay.startedAt)}` : ''}
          {replay.durationSeconds ? ` · ${Math.round(replay.durationSeconds / 60)} min` : ''}
        </p>

        <div className="course-history-row__badges" aria-label="Replay details">
          <StatusBadge status="replay" />
          {replay.hasTranscript ? <span className="course-pill course-pill--soft">Transcript</span> : null}
          {replay.hasSummary ? <span className="course-pill course-pill--soft">Summary</span> : null}
        </div>
      </div>

      <div className="course-history-row__actions">
        <div className="download-dropdown-container course-history-download-dropdown">
          <button
            ref={buttonRef}
            type="button"
            className={`btn btn-outline course-history-download-toggle${canDownload ? '' : ' course-history-download-toggle--disabled'}`}
            onClick={handleToggleDownloadMenu}
            disabled={!canDownload || isDownloading}
            aria-disabled={!canDownload || isDownloading}
            aria-expanded={isMenuOpen}
            aria-haspopup="menu"
          >
            Download
            <i className="fa-solid fa-chevron-down" aria-hidden="true" />
          </button>
        </div>
        <button type="button" className="btn btn-primary" onClick={() => onWatch(replay)}>
          Watch Replay
        </button>
      </div>

      {isMenuOpen && position
        ? ReactDOM.createPortal(
            <>
              <div className="course-history-download-backdrop" onClick={() => setIsMenuOpen(false)} />
              <div
                className={`download-menu course-history-download-menu course-history-download-popover${position.placement === 'up' ? ' course-history-download-popover--up' : ''}`}
                style={{
                  left: `${position.left}px`,
                  top: `${position.top}px`,
                  width: `${position.width}px`,
                  maxHeight: `${position.maxHeight}px`
                }}
              >
                {downloadOptions.map((option) => (
                  <button
                    key={option.key}
                    type="button"
                    onClick={() => handleDownload(option)}
                  >
                    {option.label}
                  </button>
                ))}
              </div>
            </>,
            document.body
          )
        : null}
    </article>
  );
}
