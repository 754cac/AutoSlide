import React from 'react';
import Card from '../ui/Card';
import PaginationControls from '../ui/PaginationControls';
import StatusBadge from '../ui/StatusBadge';
import { formatHKT } from '../../utils/dateUtils';

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

function getReleaseLabel(material) {
  if (!material.releaseAt) return 'No release date';
  const isUpcoming = material.status === 'upcoming';
  return `${isUpcoming ? 'Scheduled for' : 'Released on'} ${formatHKT(material.releaseAt)}`;
}

function getSizeLabel(material) {
  return material.fileSizeLabel || material.sizeLabel || material.size || 'Size unavailable';
}

function MaterialRow({ material, onDownload, onDelete, isDownloading }) {
  const canDownload = Boolean(material.downloadUrl || material.url || material.downloadUrl);
  const canDelete = Boolean(onDelete);
  const busy = isDownloading && canDownload;
  const fileIconClass = getFileIconClass(material.fileName);

  return (
    <article className={`course-material-row${material.status === 'locked' ? ' course-material-row--muted' : ''}`}>
      <div className="course-material-row__icon" aria-hidden="true">
        <i className={fileIconClass} aria-hidden="true" />
      </div>

      <div className="course-material-row__body">
        <div className="course-material-row__title-line">
          <p className="course-material-row__title">{material.title}</p>
        </div>

        <p className="course-material-row__meta">
          {material.fileName}
          {getSizeLabel(material) ? ` · ${getSizeLabel(material)}` : ''}
        </p>

        <p className="course-material-row__release-text">{getReleaseLabel(material)}</p>

        <div className="course-material-row__badges">
          <StatusBadge status={material.status} />
        </div>
      </div>

      <div className="course-material-row__actions">
        <button
          type="button"
          className="btn btn-outline course-material-row__download-button"
          disabled={!canDownload || busy}
          onClick={() => onDownload(material)}
        >
          <i className="fa-solid fa-download" aria-hidden="true" />
          {busy ? 'Downloading...' : 'Download'}
        </button>

        {canDelete ? (
          <button
            type="button"
            className="btn btn-text course-material-row__delete-button"
            onClick={() => onDelete(material)}
            aria-label="Delete material"
          >
              <i className="fa-solid fa-trash-can" aria-hidden="true" />
          </button>
        ) : null}
      </div>
    </article>
  );
}

function groupMaterialsByWeek(materialsByWeek = {}) {
  return Object.keys(materialsByWeek)
    .map((weekKey) => Number(weekKey))
    .sort((left, right) => left - right)
    .map((weekValue) => ({
      week: weekValue,
      rows: materialsByWeek[weekValue] || []
    }));
}

export default function MaterialsLibraryCard({
  materialsByWeek = {},
  summary = '',
  loading = false,
  error = '',
  searchValue = '',
  statusValue = 'all',
  onSearchChange,
  onStatusChange,
  onDownload,
  onDelete,
  isDownloading = false,
  totalCount = 0,
  page,
  pageSize,
  onPageChange,
  onPageSizeChange,
  hasMaterials = false
}) {
  const groups = groupMaterialsByWeek(materialsByWeek);
  const hasFilters = Boolean(searchValue?.trim()) || statusValue !== 'all';
  const emptyTitle = hasMaterials && hasFilters ? 'No materials match your filters' : 'No materials uploaded yet';
  const emptyText = hasMaterials && hasFilters
    ? 'Try a broader search or reset the status filter.'
    : 'Upload a file above to populate the materials library.';

  return (
    <Card className="course-materials-panel course-material-library-card">
      <div className="course-material-library-card__header">
        <div className="course-material-library-card__heading">
          <p className="course-material-library-card__eyebrow">Materials Library</p>
          <p className="course-material-library-card__subtitle">{summary}</p>
        </div>

        <div className="course-material-library-card__filters">
          <input
            className="course-quick-upload-card__input course-material-library-card__search"
            type="search"
            value={searchValue}
            onChange={(event) => onSearchChange?.(event.target.value)}
            placeholder="Search materials"
          />
          <select
            className="course-quick-upload-card__input course-material-library-card__select"
            value={statusValue}
            onChange={(event) => onStatusChange?.(event.target.value)}
          >
            <option value="all">All status</option>
            <option value="released">Released</option>
            <option value="upcoming">Scheduled</option>
          </select>
        </div>
      </div>

      {error ? <p className="course-material-library-card__error">{error}</p> : null}

      {loading ? (
        <div className="course-material-library-card__empty">
          <p className="course-material-library-card__empty-title">Loading materials...</p>
          <p className="course-material-library-card__empty-text">Fetching the full materials library for this course.</p>
        </div>
      ) : null}

      {!loading && groups.length === 0 ? (
        <div className="course-material-library-card__empty">
          <p className="course-material-library-card__empty-title">{emptyTitle}</p>
          <p className="course-material-library-card__empty-text">{emptyText}</p>
        </div>
      ) : null}

      {groups.length > 0 ? (
        <div className="course-material-library-card__groups">
          {groups.map((group) => (
            <Card key={group.week} className="course-material-group-card">
              <div className="course-material-list">
                {group.rows.map((material) => (
                  <MaterialRow
                    key={material.id}
                    material={material}
                    onDownload={onDownload}
                    onDelete={onDelete}
                    isDownloading={isDownloading}
                  />
                ))}
              </div>
            </Card>
          ))}
        </div>
      ) : null}

      <PaginationControls
        page={page}
        pageSize={pageSize}
        totalCount={totalCount}
        onPageChange={onPageChange}
        onPageSizeChange={onPageSizeChange}
      />
    </Card>
  );
}