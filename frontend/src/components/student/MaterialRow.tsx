import React from 'react';
import { formatHKT } from '../../utils/dateUtils';
import StatusBadge from '../ui/StatusBadge';

function getFileIconClass(fileName) {
  if (!fileName) return 'fa-solid fa-file-lines';

  const extension = fileName.split('.').pop()?.toLowerCase();

  switch (extension) {
    case 'pdf':
      return 'fa-solid fa-file-pdf';
    case 'doc':
    case 'docx':
      return 'fa-solid fa-file-word';
    case 'ppt':
    case 'pptx':
      return 'fa-solid fa-file-powerpoint';
    case 'xls':
    case 'xlsx':
      return 'fa-solid fa-file-excel';
    case 'zip':
    case 'rar':
    case '7z':
      return 'fa-solid fa-file-zipper';
    case 'png':
    case 'jpg':
    case 'jpeg':
    case 'gif':
    case 'webp':
      return 'fa-solid fa-file-image';
    case 'mp4':
    case 'mov':
    case 'webm':
      return 'fa-solid fa-file-video';
    default:
      return 'fa-solid fa-file-lines';
  }
}

export default function MaterialRow({ material, onDownload, isDownloading = false }) {
  const canDownload = Boolean(material.canDownload);
  const isBusy = isDownloading && canDownload;
  const isLocked = !canDownload;
  const fileIconClass = getFileIconClass(material.fileName);

  return (
    <article className={`course-material-row${isLocked ? ' course-material-row--muted' : ''}`}>
      <div className="course-material-row__icon" aria-hidden="true">
        <i className={fileIconClass} aria-hidden="true" />
      </div>

      <div className="course-material-row__body">
        <p className="course-material-row__title">{material.title}</p>
        <p className="course-material-row__meta">
          {material.fileName}
          {material.releaseAt ? ` · ${formatHKT(material.releaseAt)}` : ''}
        </p>
      </div>

      <div className="course-material-row__actions">
        <StatusBadge status={material.status} />
        <button
          type="button"
          className="btn btn-outline"
          disabled={isLocked || isBusy}
          onClick={() => onDownload(material)}
        >
          {isBusy ? 'Downloading...' : isLocked ? 'Locked' : 'Download'}
        </button>
      </div>
    </article>
  );
}
