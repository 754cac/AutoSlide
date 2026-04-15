import React, { useState, useEffect, useRef } from 'react';
import { Document, Page } from 'react-pdf';

const DEBUG = import.meta.env.DEV;

/**
 * ThumbnailItem - Renders a single slide thumbnail.
 *
 * Secure mode: when `getSlideUrl` is supplied and the slide is unlocked,
 * fetches the signed URL immediately as soon as the slide becomes unlocked.
 * This eager strategy eliminates the previous IntersectionObserver delay
 * and ensures thumbnails are ready in step with the canvas URL fetch.
 *
 * Legacy mode: falls back to rendering via the shared `pdfFile` Document.
 */
function ThumbnailItem({ pageNum, isCurrent, isLocked, onPageClick, getSlideUrl, pdfFile, slideAspectRatio }) {
  const [slideUrl, setSlideUrl] = useState(null)
  const [fetched, setFetched] = useState(false)
  const [isVisible, setIsVisible] = useState(false)
  const itemRef = useRef(null)
  const hasLoggedLock = useRef(false);
  const useSecureSignedUrls = typeof getSlideUrl === 'function';

  const previewStyle = slideAspectRatio ? { aspectRatio: slideAspectRatio } : undefined;

  useEffect(() => {
    if (!itemRef.current) return;

    const observer = new IntersectionObserver(
      (entries) => {
        const first = entries[0];
        if (first?.isIntersecting) {
          setIsVisible(true);
          observer.disconnect();
        }
      },
      {
        root: null,
        rootMargin: '150px',
        threshold: 0.01,
      }
    );

    observer.observe(itemRef.current);
    return () => observer.disconnect();
  }, []);

  // Log lock state transitions once per thumbnail
  useEffect(() => {
    const isUnlocked = !isLocked;
    if (!isUnlocked) {
      if (DEBUG && !hasLoggedLock.current) {
        hasLoggedLock.current = true;
        console.log(`[Thumb] Slide ${pageNum} locked`, { isUnlocked, unlockedUpTo: '(check parent prop)' });
      }
    } else {
      if (DEBUG && hasLoggedLock.current) {
        console.log(`[Thumb] Slide ${pageNum} unlocked`);
        hasLoggedLock.current = false;
      }
    }
  }, [isLocked, pageNum]);

  // Fetch once when the thumbnail is visible and unlocked.
  useEffect(() => {
    if (isLocked || !getSlideUrl || fetched || !isVisible) return;

    setFetched(true);
    if (DEBUG) console.log(`[Thumb] Slide ${pageNum} visible and unlocked - fetching URL`);
    getSlideUrl(pageNum).then(url => {
      if (url) {
        if (DEBUG) console.log(`[Thumb] Slide ${pageNum} URL fetched`);
        setSlideUrl(url);
      }
    });
  }, [isLocked, pageNum, getSlideUrl, fetched, isVisible])

  const label = (
    <div className="thumbnail-label">
      Slide {pageNum}
      {isLocked && <span className="lock-icon"> Locked</span>}
    </div>
  )

  const preview = isLocked ? (
    <div className="locked-thumbnail-placeholder">Locked</div>
  ) : slideUrl ? (
    // Secure mode: render single-page PDF from signed URL
    <Document
      file={slideUrl}
      loading={<div className="thumb-loading">...</div>}
      error={<div className="thumb-loading">!</div>}
    >
      <Page
        pageNumber={1}
        width={120}
        renderTextLayer={false}
        renderAnnotationLayer={false}
        loading={<div className="thumb-loading">...</div>}
      />
    </Document>
  ) : pdfFile && !useSecureSignedUrls ? (
    // Legacy mode: render page N from the shared full PDF document
    <Page
      pageNumber={pageNum}
      width={120}
      renderTextLayer={false}
      renderAnnotationLayer={false}
      loading={<div className="thumb-loading">...</div>}
    />
  ) : (
    <div className="thumb-loading" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: 68, color: '#9ca3af', fontSize: '0.8rem' }}>
      {pageNum}
    </div>
  )

  return (
    <div
      ref={itemRef}
      id={`thumb-${pageNum}`}
      className={`thumbnail-item viewer-control viewer-control--card ${isCurrent ? 'active' : ''} ${isLocked ? 'locked' : ''}`}
      onClick={() => onPageClick(pageNum)}
      title={isLocked ? 'Locked – wait for instructor' : `Go to slide ${pageNum}`}
    >
      <div className="thumbnail-preview" style={previewStyle}>{preview}</div>
      {label}
    </div>
  )
}

/**
 * SlideThumbnailBar - Sidebar list of slide thumbnails.
 *
 * Props:
 *   pdfFile        – full PDF URL (legacy / replay fallback)
 *   getSlideUrl    – async (pageNum) => signedUrl  (secure per-page mode)
 *   unlockedSlides – Set<number> of unlocked page numbers
 *   slideAspectRatio – optional "width / height" ratio string for thumbnail framing
 */
export default function SlideThumbnailBar({
  isOpen,
  numPages,
  currentPage,
  unlockedSlides,
  onPageClick,
  pdfFile,
  getSlideUrl,
  isReplayMode,
  solutionPages = [],
  activeTab = 'slides',
  onTabChange,
  onSolutionClick,
  currentSolutionId,
  slideAspectRatio,
  showSolutionsTab = true,
}) {
  // diagnostics
  if (DEBUG) console.log('[ThumbnailBar] Render:', {
    totalSlides: numPages,
    unlockedUpTo: unlockedSlides && unlockedSlides.size > 0 ? Math.max(...unlockedSlides) : 0,
    currentIndex: currentPage,
    isReplayMode,
    willRender: numPages > 0 ? `${numPages} items` : 'EMPTY — check totalSlides'
  });
  if (!isOpen) return null

  const isSlideUnlocked = (n) => isReplayMode || (unlockedSlides && unlockedSlides.has(n))

  const handleTabChange = (nextTab) => {
    if (onTabChange) onTabChange(nextTab)
  }

  const items = Array.from({ length: numPages }, (_, i) => i + 1).map(pageNum => (
    <React.Fragment key={`thumb_${pageNum}`}>
      <ThumbnailItem
        pageNum={pageNum}
        isCurrent={pageNum === currentPage}
        isLocked={!isSlideUnlocked(pageNum)}
        onPageClick={onPageClick}
        getSlideUrl={getSlideUrl}
        pdfFile={pdfFile}
        slideAspectRatio={slideAspectRatio}
      />
    </React.Fragment>
  ))

  const solutionItems = (solutionPages || []).map(solution => {
    const isActive = currentSolutionId === solution.solutionPageId
    const kindLabel = solution.kind === 'currentSlide' ? 'Current Slide' : 'Blank'

    return (
      <div
        key={solution.solutionPageId}
        className={`thumbnail-item solution-thumbnail-item viewer-control viewer-control--card ${isActive ? 'active' : ''}`}
        onClick={() => onSolutionClick && onSolutionClick(solution.solutionPageId)}
        title={`Open ${solution.solutionPageId}`}
      >
        <div className="thumbnail-preview">
          {solution.imageUrl ? (
            <img
              src={solution.imageUrl}
              alt={solution.solutionPageId}
              className="solution-thumbnail-image"
            />
          ) : (
            <div className="thumb-loading">...</div>
          )}
        </div>
        <div className="thumbnail-label">
          {solution.solutionPageId}
          <span className="solution-kind-badge">{kindLabel}</span>
        </div>
      </div>
    )
  })

  // container for thumbnails
  const scrollContainer = (
    <div className="thumbnail-list" style={{ overflowY: 'auto', height: '100%' }}>
      {activeTab === 'slides' || !showSolutionsTab ? items : solutionItems}
      {showSolutionsTab && activeTab === 'solutions' && solutionItems.length === 0 && (
        <div className="solution-empty-state">No solution pages yet.</div>
      )}
    </div>
  )

  // When using secure mode (getSlideUrl), we don't need the outer shared Document
  if (getSlideUrl) {
    return (
      <div className="sidebar left-sidebar viewer-panel viewer-panel--thumbnails">
        <div className="thumbnail-tabs">
          <button
            type="button"
            className={`thumbnail-tab-btn viewer-control viewer-control--tab ${activeTab === 'slides' ? 'active' : ''}`}
            onClick={() => handleTabChange('slides')}
          >
            <i className="fa-solid fa-images thumbnail-tab-btn__icon" aria-hidden="true" />
            <span>Slides</span>
          </button>
          {showSolutionsTab && (
            <button
              type="button"
              className={`thumbnail-tab-btn viewer-control viewer-control--tab ${activeTab === 'solutions' ? 'active' : ''}`}
              onClick={() => handleTabChange('solutions')}
            >
              <i className="fa-solid fa-square-check thumbnail-tab-btn__icon" aria-hidden="true" />
              <span>Solutions</span>
            </button>
          )}
        </div>
        {scrollContainer}
      </div>
    )
  }

  // Legacy mode: wrap all items in a single shared Document
  return (
    <div className="sidebar left-sidebar viewer-panel viewer-panel--thumbnails">
      <div className="thumbnail-tabs">
        <button
          type="button"
          className={`thumbnail-tab-btn viewer-control viewer-control--tab ${activeTab === 'slides' ? 'active' : ''}`}
          onClick={() => handleTabChange('slides')}
        >
          <i className="fa-solid fa-images thumbnail-tab-btn__icon" aria-hidden="true" />
          <span>Slides</span>
        </button>
        {showSolutionsTab && (
          <button
            type="button"
            className={`thumbnail-tab-btn viewer-control viewer-control--tab ${activeTab === 'solutions' ? 'active' : ''}`}
            onClick={() => handleTabChange('solutions')}
          >
            <i className="fa-solid fa-square-check thumbnail-tab-btn__icon" aria-hidden="true" />
            <span>Solutions</span>
          </button>
        )}
      </div>
      <div className="thumbnail-list" style={{ overflowY: 'auto', height: '100%' }}>
        {activeTab === 'slides' || !showSolutionsTab ? (
          <Document
            file={pdfFile}
            loading={<div className="loading-text">Loading thumbnails...</div>}
          >
            {items}
          </Document>
        ) : (
          <>
            {solutionItems}
            {solutionItems.length === 0 && (
              <div className="solution-empty-state">No solution pages yet.</div>
            )}
          </>
        )}
      </div>
    </div>
  )
}
