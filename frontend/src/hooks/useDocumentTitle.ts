import { useEffect } from 'react';

export default function useDocumentTitle(title) {
  useEffect(() => {
    if (!title || typeof document === 'undefined') return undefined;

    document.title = title;
    return undefined;
  }, [title]);
}