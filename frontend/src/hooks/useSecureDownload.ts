import { useState } from 'react';
import { apiUrl } from '../utils/api';

function tryGetFileNameFromContentDisposition(contentDisposition) {
  if (!contentDisposition) return null;

  const starMatch = /filename\*=UTF-8''([^;]+)/i.exec(contentDisposition);
  if (starMatch?.[1]) {
    try {
      return decodeURIComponent(starMatch[1]);
    } catch {
      return starMatch[1];
    }
  }

  const match = /filename="?([^";]+)"?/i.exec(contentDisposition);
  return match?.[1] ?? null;
}

export default function useSecureDownload() {
  const [isDownloading, setIsDownloading] = useState(false);

  const getLocalBackendBaseUrl = () => {
    if (typeof window === 'undefined') return null;

    const { hostname, protocol } = window.location;
    if (hostname !== 'localhost' && hostname !== '127.0.0.1') {
      return null;
    }

    return `${protocol}//${hostname}:5000`;
  };

  const readErrorMessage = async (response) => {
    const contentType = response.headers.get('content-type') || '';
    const text = await response.text().catch(() => '');

    if (!text) {
      return response.statusText || '';
    }

    if (contentType.includes('application/json')) {
      try {
        const payload = JSON.parse(text);
        return payload.error || payload.message || payload.title || payload.detail || response.statusText || '';
      } catch {
        return response.statusText || '';
      }
    }

    const trimmed = text.trim();
    if (!trimmed || trimmed.startsWith('<')) {
      return response.statusText || '';
    }

    return trimmed;
  };

  const triggerBrowserDownload = (url, suggestedFileName) => {
    const link = document.createElement('a');
    link.href = url;
    if (suggestedFileName) {
      link.setAttribute('download', suggestedFileName);
    }
    link.rel = 'noopener noreferrer';
    document.body.appendChild(link);
    link.click();
    link.remove();
  };

  const downloadFile = async (id, suggestedFileName, endpointType = 'materials') => {
    setIsDownloading(true);
    try {
      const token = localStorage.getItem('token');
      if (endpointType === 'materials') {
        const primaryUrl = apiUrl(`/api/materials/${id}/url`);
        const fallbackBaseUrl = getLocalBackendBaseUrl();
        const candidateUrls = fallbackBaseUrl ? [primaryUrl, `${fallbackBaseUrl}/api/materials/${id}/url`] : [primaryUrl];

        let response = null;
        let lastError = null;

        for (const candidateUrl of candidateUrls) {
          try {
            response = await fetch(candidateUrl, {
              method: 'GET',
              headers: {
                Authorization: `Bearer ${token}`
              }
            });
            break;
          } catch (error) {
            lastError = error;
          }
        }

        if (!response) {
          throw lastError || new Error('Unable to reach the material download endpoint.');
        }

        if (!response.ok) {
          const message = await readErrorMessage(response);
          throw new Error(message || `Download failed (${response.status})`);
        }

        const payload = await response.json().catch(() => ({}));
        const signedUrl = payload.url || payload.Url;

        if (!signedUrl) {
          throw new Error('Unable to resolve a download URL for this material.');
        }

        triggerBrowserDownload(signedUrl, suggestedFileName);
        return;
      }

      const url = typeof endpointType === 'string' && (endpointType.startsWith('http') || endpointType.startsWith('/'))
        ? apiUrl(endpointType)
        : endpointType === 'sessions'
          ? apiUrl(`/api/sessions/${id}/download`)
          : apiUrl(`/api/sessions/${id}/download`);

      const response = await fetch(url, {
        method: 'GET',
        headers: {
          Authorization: `Bearer ${token}`
        }
      });

      if (!response.ok) {
        const message = await readErrorMessage(response);
        throw new Error(message || `Download failed (${response.status})`);
      }

      const blob = await response.blob();
      const cd = response.headers.get('content-disposition');
      const fileName = tryGetFileNameFromContentDisposition(cd) || suggestedFileName || `file_${id}`;

      const blobUrl = window.URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = blobUrl;
      link.setAttribute('download', fileName);
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.URL.revokeObjectURL(blobUrl);
    } catch (error) {
      console.error('Download error:', error);
      alert(error?.message || 'Failed to download file.');
    } finally {
      setIsDownloading(false);
    }
  };

  return { downloadFile, isDownloading };
}
