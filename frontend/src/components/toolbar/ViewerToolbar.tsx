import React, { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import QRCode from "react-qr-code";

function ToolbarIconButton({ iconClass, label, title, onClick, disabled = false, className = '', style = undefined }) {
  return (
    <button
      type="button"
      className={`icon-btn viewer-control viewer-control--icon${className ? ` ${className}` : ''}`}
      onClick={onClick}
      disabled={disabled}
      title={title || label}
      aria-label={label}
      style={style}
    >
      <i className={iconClass} aria-hidden="true" />
    </button>
  );
}

/**
 * ViewerToolbar - Main toolbar for the live session viewer.
 * Provides navigation, zoom, annotation tools, download options, and session sharing.
 */
export default function ViewerToolbar({ 
  sessionTitle, 
  onHome,
  isLive,
  isReplayMode,
  currentPage, 
  totalPages, 
  unlockedSlides, // Set of unlocked slide numbers (true progressive unlock)
  onPrev, 
  onNext, 
  onJumpTo, 
  scale, 
  onZoomIn, 
  onZoomOut,
  onFitPage,
  onToggleFullScreen,
  onToggleSidebar,
  onToggleRightSidebar,
  isRightSidebarOpen,
  summary
}) {
  const [isQrModalOpen, setIsQrModalOpen] = useState(false);
  const [isCopied, setIsCopied] = useState(false);
  const [isSummaryOpen, setIsSummaryOpen] = useState(false);
  const shareDialogRef = useRef(null);
  const shareUrlInputRef = useRef(null);
  const shareCopyButtonRef = useRef(null);
  const shareCloseButtonRef = useRef(null);
  const shareUrl = typeof window !== 'undefined' ? window.location.href : '';
  const modalRoot = typeof document !== 'undefined' ? document.body : null;
  const shareModalTitleId = 'share-session-modal-title';
  const shareModalDescriptionId = 'share-session-modal-description';

  useEffect(() => {
    if (!isSummaryOpen) return undefined;

    const previousOverflow = document.body.style.overflow;
    const handleKeyDown = (event) => {
      if (event.key === 'Escape') {
        setIsQrModalOpen(false);
        setIsSummaryOpen(false);
      }
    };

    document.body.style.overflow = 'hidden';
    window.addEventListener('keydown', handleKeyDown);

    return () => {
      document.body.style.overflow = previousOverflow;
      window.removeEventListener('keydown', handleKeyDown);
    };
  }, [isQrModalOpen, isSummaryOpen]);

  useEffect(() => {
    if (!isQrModalOpen) return undefined;

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    setIsCopied(false);

    const frameId = window.requestAnimationFrame(() => {
      const focusTarget = shareUrlInputRef.current || shareCopyButtonRef.current || shareCloseButtonRef.current || shareDialogRef.current;
      if (focusTarget?.focus) {
        focusTarget.focus({ preventScroll: true });
      }
    });

    return () => {
      document.body.style.overflow = previousOverflow;
      window.cancelAnimationFrame(frameId);
    };
  }, [isQrModalOpen]);

  const renderModal = ({ isOpen, onClose, title, size = 'md', children }) => {
    if (!isOpen || !modalRoot) return null;

    return createPortal(
      <div className="viewer-modal-root" onClick={onClose}>
        <div className="viewer-modal-backdrop" aria-hidden="true" />
        <section
          className={`viewer-modal-card viewer-modal-card--${size}`}
          role="dialog"
          aria-modal="true"
          aria-label={title}
          onClick={(event) => event.stopPropagation()}
        >
          <div className="viewer-modal-card__header">
            <div className="viewer-modal-card__heading">
              <h3 className="viewer-modal-card__title">{title}</h3>
            </div>
            <button type="button" className="viewer-modal-card__close viewer-control viewer-control--icon" onClick={onClose} aria-label={`Close ${title}`}>
              <i className="fa-solid fa-xmark" aria-hidden="true" />
            </button>
          </div>
          <div className="viewer-modal-card__body">
            {children}
          </div>
        </section>
      </div>,
      modalRoot,
    );
  };

  // Helper: check if a specific slide is unlocked (true progressive unlock)
  const isSlideUnlocked = (slideNum) => {
    return unlockedSlides && unlockedSlides.has(slideNum);
  };

  // Count of unlocked slides
  const unlockedCount = unlockedSlides ? unlockedSlides.size : 0;

  // Check if next slide is unlocked
  const canGoNext = isSlideUnlocked(currentPage + 1);
  const pageInputDigits = Math.max(String(Math.max(totalPages || 1, currentPage || 1)).length, 3);

  const copyLink = async () => {
    const url = shareUrl;
    if (!navigator.clipboard) {
      // fallback
      const textarea = document.createElement('textarea');
      textarea.value = url;
      document.body.appendChild(textarea);
      textarea.select();
      try { document.execCommand('copy'); setIsCopied(true); } catch (e) { console.error('copy fallback failed', e); }
      document.body.removeChild(textarea);
    } else {
      try {
        await navigator.clipboard.writeText(url);
        setIsCopied(true);
      } catch (err) {
        console.error('copy failed', err);
      }
    }
    setTimeout(() => setIsCopied(false), 2500);
  };

  const handleShareModalKeyDown = (event) => {
    if (!isQrModalOpen) return;

    if (event.key === 'Escape') {
      event.preventDefault();
      setIsQrModalOpen(false);
      return;
    }

    if (event.key !== 'Tab') return;

    const dialog = shareDialogRef.current;
    if (!dialog) return;

    const focusableElements = Array.from(
      dialog.querySelectorAll('button:not([disabled]), input:not([disabled]), [href], [tabindex]:not([tabindex="-1"])')
    ) as HTMLElement[];

    const visibleFocusableElements = focusableElements.filter((element) => !element.hasAttribute('aria-hidden'));

    if (visibleFocusableElements.length === 0) {
      event.preventDefault();
      dialog.focus();
      return;
    }

    const firstFocusable = visibleFocusableElements[0];
    const lastFocusable = visibleFocusableElements[visibleFocusableElements.length - 1];

    if (event.shiftKey) {
      if (document.activeElement === firstFocusable || document.activeElement === dialog) {
        event.preventDefault();
        lastFocusable.focus();
      }
      return;
    }

    if (document.activeElement === lastFocusable) {
      event.preventDefault();
      firstFocusable.focus();
    }
  };

  const renderShareModal = () => {
    if (!isQrModalOpen || !modalRoot) return null;

    return createPortal(
      <div className="share-session-modal" onClick={() => setIsQrModalOpen(false)}>
        <div className="share-session-modal__backdrop" aria-hidden="true" />
        <section
          ref={shareDialogRef}
          className="share-session-modal__dialog"
          role="dialog"
          aria-modal="true"
          aria-labelledby={shareModalTitleId}
          aria-describedby={shareModalDescriptionId}
          tabIndex={-1}
          onClick={(event) => event.stopPropagation()}
          onKeyDown={handleShareModalKeyDown}
        >
          <div className="share-session-modal__header">
            <div className="share-session-modal__heading">
              <h3 id={shareModalTitleId} className="share-session-modal__title">Share Session</h3>
              <div id={shareModalDescriptionId} className="share-session-modal__intro">
                Scan the QR code or copy the link below to invite others.
              </div>
            </div>
            <button
              ref={shareCloseButtonRef}
              type="button"
              className="share-session-modal__close viewer-control viewer-control--icon"
              onClick={() => setIsQrModalOpen(false)}
              aria-label="Close share session dialog"
            >
              <i className="fa-solid fa-xmark" aria-hidden="true" />
            </button>
          </div>

          <div className="share-session-modal__body">
            <div className="share-session-modal__qr-section">
              <div className="share-session-modal__qr-box">
                <QRCode
                  value={shareUrl}
                  size={192}
                  className="share-session-modal__qr-code"
                  style={{ width: '100%', height: '100%', display: 'block' }}
                />
              </div>
            </div>

            <div className="share-session-modal__link-section">
              <label className="share-session-modal__label" htmlFor="viewer-share-url">
                Shareable link
              </label>
              <div className="share-session-modal__link-row">
                <input
                  ref={shareUrlInputRef}
                  id="viewer-share-url"
                  className="share-session-modal__input"
                  type="text"
                  value={shareUrl}
                  readOnly
                  onFocus={(event) => event.currentTarget.select()}
                />
                <button
                  ref={shareCopyButtonRef}
                  className="share-session-modal__copy-btn viewer-control viewer-control--primary"
                  onClick={copyLink}
                  aria-label="Copy session link"
                  type="button"
                >
                  {isCopied ? 'Copied!' : 'Copy'}
                </button>
              </div>
            </div>
          </div>
        </section>
      </div>,
      modalRoot,
    );
  };

  return (
    <div className="toolbar">
      {/* Session Section */}
      <div className="toolbar-group session-info">
        {onHome && (
          <ToolbarIconButton
            iconClass="fa-solid fa-house"
            label="Home"
            title="Home"
            onClick={onHome}
            style={{ marginRight: 6 }}
          />
        )}
        <ToolbarIconButton
          iconClass="fa-solid fa-bars"
          label="Toggle sidebar"
          title="Toggle Sidebar"
          onClick={onToggleSidebar}
        />
        <div className="session-title">{sessionTitle}</div>
        <div className={`status-cluster ${isLive ? 'live' : 'ended'}`}>
          {isLive ? 'LIVE' : 'ENDED'}
        </div>
      </div>

      {/* Navigation Group */}
      <div className="toolbar-group navigation">
        <ToolbarIconButton
          iconClass="fa-solid fa-chevron-left"
          label="Previous slide"
          title="Previous Slide"
          onClick={onPrev}
          disabled={currentPage <= 1 || !isSlideUnlocked(currentPage - 1)}
        />
        
        <div className="page-indicator">
          Slide 
          <input 
            type="number" 
            value={currentPage} 
            onChange={(e) => onJumpTo(parseInt(e.target.value))}
            min={1}
            max={totalPages}
            style={{ width: `${pageInputDigits + 1.25}ch` }}
          />
          <span className="total-count">/ {totalPages}</span>
          <span className="unlocked-count">({unlockedCount} unlocked)</span>
        </div>

        <ToolbarIconButton
          iconClass="fa-solid fa-chevron-right"
          label="Next slide"
          title="Next Slide"
          onClick={onNext}
          disabled={!canGoNext}
        />
      </div>

      {/* Zoom & View Group */}
      <div className="toolbar-group zoom-controls">
        <ToolbarIconButton iconClass="fa-solid fa-minus" label="Zoom out" title="Zoom Out" onClick={onZoomOut} />
        <span className="zoom-level">{Math.round(scale * 100)}%</span>
        <ToolbarIconButton iconClass="fa-solid fa-plus" label="Zoom in" title="Zoom In" onClick={onZoomIn} />
      </div>
      
      <div className="toolbar-group view-modes">
        <ToolbarIconButton
          iconClass="fa-solid fa-expand"
          label="Fit page"
          title="Fit Page"
          onClick={onFitPage}
        />
        <ToolbarIconButton
          iconClass="fa-solid fa-window-maximize"
          label="Toggle fullscreen"
          title="Toggle Full Screen"
          onClick={onToggleFullScreen}
        />
      </div>


      {/* Utility Group */}
      <div className="toolbar-group utility">
        <ToolbarIconButton
          iconClass={isRightSidebarOpen ? 'fa-solid fa-eye-slash' : 'fa-solid fa-eye'}
          label={isRightSidebarOpen ? 'Collapse right panel' : 'Expand right panel'}
          title={isRightSidebarOpen ? 'Collapse right panel' : 'Expand right panel'}
          onClick={onToggleRightSidebar}
        />
        <ToolbarIconButton
          iconClass="fa-solid fa-share-nodes"
          label="Share session"
          title="Share Session"
          onClick={() => setIsQrModalOpen(true)}
        />

      </div>

      {renderModal({
        isOpen: isSummaryOpen && Boolean(summary),
        onClose: () => setIsSummaryOpen(false),
        title: 'Lecture Summary',
        size: 'lg',
        children: (
          <>
            <p className="viewer-modal-intro">Summary of the current session.</p>
            <div className="viewer-summary-modal__content">{summary}</div>
          </>
        ),
      })}

      {renderShareModal()}
    </div>
  );
}
