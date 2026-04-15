import React, { useRef, useState, useEffect, useCallback } from 'react';
import { Document, Page } from 'react-pdf';
import AnnotationLayer from './AnnotationLayer';
import PresenterInkLayer from './PresenterInkLayer';
import { getPdfPageViewportSize } from '../../utils/pdfViewer';

/**
 * SlideCanvas - Renders the current slide with PDF viewer, presenter ink overlay, and annotation layer.
 * Handles slide dimension tracking for proper overlay positioning.
 */
export default function SlideCanvas({ 
  pdfFile, 
  pageNumber, 
  scale, 
  onDocumentLoadSuccess,
  onPageLoadSuccess,
  onLoadError,
  annotations,
  tool,
  color,
  opacity,
  onAddAnnotation,
  onUpdateAnnotation,
  onDeleteAnnotation,
  // Ink overlay props
  inkStrokes = [],
  // Signed URL expiry recovery: called when Document fires an error (e.g. 403 after TTL)
  onUrlExpired,
}) {
  const pageContainerRef = useRef(null);
  const [pageDimensions, setPageDimensions] = useState({ width: 0, height: 0 });
  const [pageReady, setPageReady] = useState(false);

  // Reset ready state when page changes
  useEffect(() => {
    setPageReady(false);
  }, [pageNumber, pdfFile, scale]);

  const handlePageLoadSuccess = useCallback((page) => {
    if (!onPageLoadSuccess || !page?.getViewport) {
      return;
    }

    const viewportSize = getPdfPageViewportSize(page);
    if (viewportSize) {
      onPageLoadSuccess(viewportSize);
    }
  }, [onPageLoadSuccess]);

  // Track the rendered canvas size so the ink overlay stays aligned to the PDF page.
  useEffect(() => {
    if (!pageContainerRef.current) return;

    let resizeFrameId = 0;

    const updateDimensions = (width, height) => {
      if (width > 0 && height > 0) {
        setPageDimensions((prev) => (
          prev.width === width && prev.height === height
            ? prev
            : { width, height }
        ));
        setPageReady(true);
      }
    };

    const measureNow = () => {
      const rect = pageContainerRef.current.getBoundingClientRect();
      updateDimensions(rect.width, rect.height);
    };

    measureNow();

    const observer = new ResizeObserver((entries) => {
      for (const entry of entries) {
        const { width, height } = entry.contentRect;
        if (width > 0 && height > 0) {
          if (resizeFrameId) {
            cancelAnimationFrame(resizeFrameId);
          }

          resizeFrameId = requestAnimationFrame(() => {
            updateDimensions(width, height);
          });
        }
      }
    });
    
    observer.observe(pageContainerRef.current);
    return () => {
      observer.disconnect();
      if (resizeFrameId) {
        cancelAnimationFrame(resizeFrameId);
      }
    };

  }, [pageNumber, pdfFile, scale]);
  
  return (
    <div className="pdf-canvas-container">
      <div className="pdf-page-frame" ref={pageContainerRef}>
        <Document
          file={pdfFile}
          onLoadSuccess={onDocumentLoadSuccess}
          onLoadError={(err) => {
            const errMsg = err?.message ?? err?.toString() ?? '';
            const isAuthExpiry = errMsg.includes('403')
                              || errMsg.includes('401')
                              || errMsg.includes('Forbidden')
                              || errMsg.includes('Unauthorized')
                              || err?.status === 403
                              || err?.status === 401;

            console.warn('[SlideCanvas] Document load error:', errMsg,
                         '| isAuthExpiry:', isAuthExpiry);

            if (isAuthExpiry && onUrlExpired) {
              console.warn('[SlideCanvas] Auth/expiry error - triggering URL refresh');
              onUrlExpired();
            } else if (onLoadError) {
              onLoadError(err);
            }
          }}
          loading={<div className="loading-spinner">Loading PDF...</div>}
          error={
            onUrlExpired ? (
              <div
                className="slide-expired-error"
                onClick={onUrlExpired}
                style={{ cursor: 'pointer', textAlign: 'center', padding: '2rem', color: '#aaa' }}
              >
                Slide expired. Click to refresh.
              </div>
            ) : (
              <div className="loading-spinner">Failed to load slide.</div>
            )
          }
        >
          <Page 
            pageNumber={pageNumber} 
            scale={scale} 
            renderTextLayer={false}
            renderAnnotationLayer={false}
            onLoadSuccess={handlePageLoadSuccess}
          />
          {/* Real-time ink overlay from presenter - only render when we have valid dimensions */}
          {pageReady && pageDimensions.width > 0 && pageDimensions.height > 0 && (
            <PresenterInkLayer 
              strokes={inkStrokes}
              width={pageDimensions.width}
              height={pageDimensions.height}
              scale={scale}
            />
          )}
          <AnnotationLayer 
            annotations={annotations}
            tool={tool}
            color={color}
            opacity={opacity}
            onAddAnnotation={onAddAnnotation}
            onUpdateAnnotation={onUpdateAnnotation}
            onDeleteAnnotation={onDeleteAnnotation}
          />
        </Document>
      </div>
    </div>
  );
}
