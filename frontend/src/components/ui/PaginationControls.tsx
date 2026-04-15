import React from 'react';
import { PAGE_SIZE_OPTIONS, getPaginationState } from '../../utils/pagination';

export default function PaginationControls({
  page,
  pageSize,
  totalCount,
  itemCount,
  onPageChange,
  onPageSizeChange
}) {
  const pagination = getPaginationState(totalCount, page, pageSize);

  return (
    <div className="pagination-controls" role="navigation" aria-label="Pagination">
      <div className="pagination-controls__summary">
        {pagination.totalItems > 0 ? `Showing ${pagination.startItem}-${pagination.endItem} of ${pagination.totalItems}` : 'Showing 0 items'}
      </div>

      <div className="pagination-controls__actions">
        <label className="pagination-controls__label">
          Per page
          <select
            value={pagination.pageSize}
            onChange={(event) => onPageSizeChange(Number(event.target.value))}
            className="pagination-controls__select"
          >
            {PAGE_SIZE_OPTIONS.map((size) => (
              <option key={size} value={size}>{size}</option>
            ))}
          </select>
        </label>

        <button
          type="button"
          className="btn btn-outline pagination-controls__nav"
          onClick={() => onPageChange(pagination.currentPage - 1)}
          disabled={!pagination.canPrev}
          aria-label="Previous page"
          title="Previous page"
        >
          <i className="fa-solid fa-chevron-left" aria-hidden="true" />
        </button>
        <span className="pagination-controls__page">Page {pagination.currentPage}</span>
        <button
          type="button"
          className="btn btn-outline pagination-controls__nav"
          onClick={() => onPageChange(pagination.currentPage + 1)}
          disabled={!pagination.canNext}
          aria-label="Next page"
          title="Next page"
        >
          <i className="fa-solid fa-chevron-right" aria-hidden="true" />
        </button>
      </div>
    </div>
  );
}
