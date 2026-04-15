export const PDF_VIEWER_MIN_SCALE = 0.25;
export const PDF_VIEWER_MAX_SCALE = 4;
export const PDF_VIEWER_ZOOM_STEP = 1.15;
export const PDF_VIEWER_MIN_ZOOM_MULTIPLIER = 0.05;
export const PDF_VIEWER_MAX_ZOOM_MULTIPLIER = 16;

export function clampValue(value, min, max) {
  if (!Number.isFinite(value)) {
    return min;
  }

  return Math.min(max, Math.max(min, value));
}

export function getPdfPageViewportSize(page) {
  if (!page?.getViewport) {
    return null;
  }

  const viewport = page.getViewport({ scale: 1 });
  if (!viewport) {
    return null;
  }

  const { width, height } = viewport;
  if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) {
    return null;
  }

  return { width, height };
}

export function getPdfFitScale(pageSize, viewportSize, options = {}) {
  const {
    mode = 'page',
    minScale = PDF_VIEWER_MIN_SCALE,
    maxScale = PDF_VIEWER_MAX_SCALE,
  } = options;

  if (!pageSize?.width || !pageSize?.height || !viewportSize?.width || !viewportSize?.height) {
    return 1;
  }

  if (mode === 'actual') {
    return 1;
  }

  const widthScale = viewportSize.width / pageSize.width;
  const heightScale = viewportSize.height / pageSize.height;
  const fitScale = mode === 'width' ? widthScale : Math.min(widthScale, heightScale);

  return clampValue(fitScale, minScale, maxScale);
}

export function getAspectRatioStyle(size) {
  if (!size?.width || !size?.height) {
    return undefined;
  }

  return `${size.width} / ${size.height}`;
}