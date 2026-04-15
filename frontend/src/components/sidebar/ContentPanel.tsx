import React, { useEffect, useRef, useState } from 'react';

/**
 * ContentPanel - Compact right sidebar for transcript, summary, and downloads.
 * Keeps the viewer shell intact and only changes the panel surface.
 */
export default function ContentPanel({ 
  isOpen,
  transcript = [],
  summary,
  sessionEnded,
  onRequestSummary,
  onDownloadPdf,
  onDownloadPptx,
  onDownloadInkOnlyPdf,
  onDownloadAnnotated,
  onDownloadAnnotatedPptx,
  isDownloadingAnnotatedPdf = false,
}) {
  const [activeTab, setActiveTab] = useState('transcript');
  const [isRequestingSummary, setIsRequestingSummary] = useState(false);
  const [isSummaryCopied, setIsSummaryCopied] = useState(false);
  const transcriptListRef = useRef(null);

  const downloadsEnabled = Boolean(sessionEnded);
  const downloadItems = [
    {
      key: 'original-pdf',
      iconClass: 'fa-solid fa-file-lines',
      label: 'Original PDF',
      detail: 'Source slide deck',
      onClick: onDownloadPdf,
    },
    {
      key: 'original-pptx',
      iconClass: 'fa-solid fa-file-powerpoint',
      label: 'Original PPTX',
      detail: 'Editable source deck',
      onClick: onDownloadPptx,
    },
    {
      key: 'ink-artifact-pdf',
      iconClass: 'fa-solid fa-pen-to-square',
      label: 'Ink Artifact PDF',
      detail: 'Presenter ink only',
      onClick: onDownloadInkOnlyPdf,
    },
    {
      key: 'annotated-pdf',
      iconClass: 'fa-solid fa-file-pen',
      label: 'Annotated PDF',
      detail: 'Slides with annotations',
      loading: isDownloadingAnnotatedPdf,
      onClick: onDownloadAnnotated,
    },
    {
      key: 'annotated-pptx',
      iconClass: 'fa-solid fa-file-pen',
      label: 'Annotated PPTX',
      detail: 'Editable annotated deck',
      onClick: onDownloadAnnotatedPptx,
    },
  ];

  useEffect(() => {
    if (activeTab === 'material' && !downloadsEnabled) {
      setActiveTab('transcript');
    }
  }, [activeTab, downloadsEnabled]);

  useEffect(() => {
    if (activeTab !== 'transcript') return;
    const container = transcriptListRef.current;
    if (!container) return;

    container.scrollTop = container.scrollHeight;
  }, [activeTab, transcript]);

  const handleRequestSummary = async () => {
    if (!onRequestSummary || isRequestingSummary) return;

    setIsRequestingSummary(true);
    try {
      await onRequestSummary();
    } finally {
      setIsRequestingSummary(false);
    }
  };

  const handleCopySummary = async () => {
    if (!summary) return;

    await navigator.clipboard.writeText(summary);
    setIsSummaryCopied(true);
  };

  return (
    <div
      className={`sidebar right-sidebar viewer-panel viewer-panel--materials content-panel${isOpen ? '' : ' content-panel--closed'}`}
      aria-hidden={!isOpen}
    >
      <div className="sidebar-tabs content-panel__tabs">
        <button
          type="button"
          className={`content-panel__tab viewer-control viewer-control--tab${activeTab === 'transcript' ? ' active' : ''}`}
          onClick={() => setActiveTab('transcript')}
        >
          Transcript
        </button>
        <button
          type="button"
          className={`content-panel__tab viewer-control viewer-control--tab${activeTab === 'summary' ? ' active' : ''}`}
          onClick={() => setActiveTab('summary')}
        >
          Summary
        </button>
        <button
          type="button"
          className={`content-panel__tab viewer-control viewer-control--tab${activeTab === 'material' ? ' active' : ''}`}
          onClick={() => downloadsEnabled && setActiveTab('material')}
          disabled={!downloadsEnabled}
          title={downloadsEnabled ? 'Session materials and downloads' : 'Available after the session ends'}
        >
          Material
        </button>
      </div>

      <div className="sidebar-content content-panel__body" ref={transcriptListRef}>
        {activeTab === 'transcript' && (
          <div className="content-panel__section">
            {transcript.length > 0 ? (
              transcript.map((entry, idx) => (
                <div key={`${entry.timestamp || idx}-${idx}`} className="content-panel__transcript-item">
                  <div className="content-panel__timestamp">
                    {entry.timestamp ? new Date(entry.timestamp).toLocaleTimeString() : ''}
                  </div>
                  <div className="content-panel__transcript-text">
                    {entry.text}
                  </div>
                </div>
              ))
            ) : (
              <div className="content-panel__empty-state content-panel__empty-state--transcript">
                <i className="fa-solid fa-comment-dots content-panel__empty-icon" aria-hidden="true" />
                <div className="content-panel__empty-copy">
                  <p className="content-panel__empty-title">No transcript yet</p>
                  <p className="content-panel__empty-detail">
                    Live captions will appear here as the session starts.
                  </p>
                </div>
              </div>
            )}
          </div>
        )}

        {activeTab === 'summary' && (
          <div className="content-panel__section content-panel__summary">
            <button
              type="button"
              className="action-btn content-panel__summary-button viewer-control viewer-control--primary"
              onClick={handleRequestSummary}
              disabled={!onRequestSummary || isRequestingSummary}
            >
              {isRequestingSummary ? 'Generating Summary…' : 'Generate Summary'}
            </button>

            {summary ? (
              <>
                <div className="content-panel__summary-header">
                  <h3 className="content-panel__summary-title">Summary</h3>
                  <button
                    type="button"
                    className="content-panel__copy-btn viewer-control viewer-control--neutral"
                    onClick={handleCopySummary}
                    title="Copy to clipboard"
                  >
                    {isSummaryCopied ? 'Copied' : 'Copy'}
                  </button>
                </div>
                <div className="content-panel__summary-body">{summary}</div>
              </>
            ) : (
              <p className="content-panel__empty">
                {sessionEnded
                  ? 'Summary is still being prepared or unavailable.'
                  : 'Click Generate Summary to load the latest available lecture summary.'}
              </p>
            )}
          </div>
        )}

        {activeTab === 'material' && (
          <div className="content-panel__section">
            {!downloadsEnabled ? (
              <div className="content-panel__empty content-panel__empty--center">
                <p>Downloads become available after the session ends.</p>
              </div>
            ) : (
              <>
                <p className="content-panel__download-note">
                  Session materials use secure signed URLs and open in a new tab.
                </p>
                <div className="content-panel__download-list">
                  {downloadItems.map((item) => (
                    <button
                      key={item.key}
                      type="button"
                      className="content-panel__download-row viewer-control viewer-control--row"
                      onClick={item.onClick}
                      disabled={!item.onClick || item.loading}
                      title={item.loading ? 'Preparing Annotated PDF...' : item.label}
                      aria-busy={item.loading || undefined}
                    >
                      <span className="content-panel__download-icon" aria-hidden="true">
                        <i className={item.iconClass} />
                      </span>
                      <span className="content-panel__download-copy">
                        <span className="content-panel__download-title">
                          {item.loading ? 'Preparing...' : item.label}
                        </span>
                        <span className="content-panel__download-detail">
                          {item.loading ? 'Generating the annotated PDF artifact.' : item.detail}
                        </span>
                      </span>
                      <i className="fa-solid fa-chevron-right content-panel__download-chevron" aria-hidden="true" />
                    </button>
                  ))}
                </div>
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
