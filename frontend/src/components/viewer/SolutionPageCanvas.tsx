import React, { useCallback } from 'react';

export default function SolutionPageCanvas({
  imageUrl,
  alt,
  renderedSize,
  onImageLoad,
}) {
  const handleLoad = useCallback((event) => {
    if (!onImageLoad) {
      return;
    }

    const imageElement = event.currentTarget;
    if (!imageElement?.naturalWidth || !imageElement?.naturalHeight) {
      return;
    }

    onImageLoad({
      width: imageElement.naturalWidth,
      height: imageElement.naturalHeight,
    });
  }, [onImageLoad]);

  const frameStyle = renderedSize
    ? {
        width: `${renderedSize.width}px`,
        height: `${renderedSize.height}px`,
      }
    : undefined;

  const imageStyle = renderedSize
    ? {
        width: '100%',
        height: '100%',
      }
    : {
        width: 'auto',
        height: 'auto',
        maxWidth: 'none',
        maxHeight: 'none',
      };

  return (
    <div className="solution-page-canvas">
      {imageUrl ? (
        <div className="solution-page-frame" style={frameStyle}>
          <img
            src={imageUrl}
            alt={alt}
            className="solution-page-image"
            style={imageStyle}
            onLoad={handleLoad}
          />
        </div>
      ) : (
        <div className="loading-spinner">Loading solution page...</div>
      )}
    </div>
  );
}