export const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL || '/').replace(/\/$/, '');

export function apiUrl(path) {
  if (!path) return API_BASE_URL || '/';
  if (path.startsWith('http://') || path.startsWith('https://')) return path;
  if (!API_BASE_URL) return path.startsWith('/') ? path : `/${path}`;
  return `${API_BASE_URL}${path.startsWith('/') ? path : `/${path}`}`;
}
