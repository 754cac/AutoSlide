export function buildLocationPath(locationLike) {
  if (!locationLike) return '';

  if (typeof locationLike === 'string') {
    return locationLike;
  }

  const pathname = typeof locationLike.pathname === 'string' ? locationLike.pathname : '';
  const search = typeof locationLike.search === 'string' ? locationLike.search : '';
  const hash = typeof locationLike.hash === 'string' ? locationLike.hash : '';

  return `${pathname}${search}${hash}`;
}

export function sanitizeInternalRedirect(target, fallback = '/dashboard') {
  if (typeof target !== 'string') return fallback;

  const trimmed = target.trim();
  if (!trimmed || !trimmed.startsWith('/') || trimmed.startsWith('//')) {
    return fallback;
  }

  return trimmed;
}

export function readRedirectParam(searchParams) {
  if (!searchParams) return '';

  return searchParams.get('redirect') || searchParams.get('returnUrl') || '';
}

export function buildLoginRedirectUrl(target) {
  const safeTarget = sanitizeInternalRedirect(target, '/dashboard');
  return `/login?redirect=${encodeURIComponent(safeTarget)}`;
}