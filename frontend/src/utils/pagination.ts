export const PAGE_SIZE_OPTIONS = [25, 50, 100];

export function normalizePageSize(value, fallback = 25) {
  const numeric = Number(value);
  if (PAGE_SIZE_OPTIONS.includes(numeric)) return numeric;
  return fallback;
}

export function clampPage(page, totalPages) {
  const safeTotalPages = Math.max(1, Number(totalPages) || 1);
  const numericPage = Number(page) || 1;
  return Math.min(Math.max(1, numericPage), safeTotalPages);
}

export function getPaginationState(totalItems, page = 1, pageSize = 25) {
  const total = Math.max(0, Number(totalItems) || 0);
  const safePageSize = normalizePageSize(pageSize);
  const totalPages = Math.max(1, Math.ceil(total / safePageSize));
  const currentPage = clampPage(page, totalPages);
  const startItem = total === 0 ? 0 : (currentPage - 1) * safePageSize + 1;
  const endItem = total === 0 ? 0 : Math.min(currentPage * safePageSize, total);

  return {
    totalItems: total,
    pageSize: safePageSize,
    currentPage,
    totalPages,
    startItem,
    endItem,
    canPrev: currentPage > 1,
    canNext: currentPage < totalPages
  };
}

export function buildPagedPath(path, page = 1, pageSize = 25) {
  const safePage = Math.max(1, Number(page) || 1);
  const safeSize = normalizePageSize(pageSize);
  const skip = (safePage - 1) * safeSize;
  const divider = path.includes('?') ? '&' : '?';
  return `${path}${divider}skip=${skip}&take=${safeSize}`;
}

export function readPaginationHeaders(response, defaultPageSize = 25) {
  const total = Number(response.headers.get('X-Total-Count') || 0);
  const skip = Number(response.headers.get('X-Skip') || 0);
  const take = normalizePageSize(response.headers.get('X-Take') || defaultPageSize, defaultPageSize);
  return {
    total: Number.isFinite(total) ? total : 0,
    skip: Number.isFinite(skip) ? skip : 0,
    take
  };
}

export function toPage(skip = 0, take = 25) {
  const safeTake = normalizePageSize(take);
  return Math.floor((Math.max(0, Number(skip) || 0) / safeTake)) + 1;
}
