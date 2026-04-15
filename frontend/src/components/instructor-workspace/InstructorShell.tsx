import React, { useEffect, useRef } from 'react';
import { useLocation } from 'react-router-dom';
import SidebarNav from '../ui/SidebarNav';

export default function InstructorShell({
  brandTitle = 'AutoSlide',
  brandSubtitle = 'Instructor Workspace',
  ariaLabel = 'Instructor navigation',
  primaryAction = null,
  user = null,
  children
}) {
  const location = useLocation();
  const contentRef = useRef(null);

  useEffect(() => {
    if (typeof window === 'undefined' || !('scrollRestoration' in window.history)) return undefined;

    const previousScrollRestoration = window.history.scrollRestoration;
    window.history.scrollRestoration = 'manual';

    return () => {
      window.history.scrollRestoration = previousScrollRestoration;
    };
  }, []);

  useEffect(() => {
    contentRef.current?.scrollTo({ top: 0, left: 0, behavior: 'auto' });
  }, [location.pathname]);

  return (
    <div className="dashboard-root instructor-shell">
      <SidebarNav
        brandTitle={brandTitle}
        brandSubtitle={brandSubtitle}
        ariaLabel={ariaLabel}
        primaryAction={primaryAction}
        user={user}
      />

      <div className="dashboard-main instructor-shell__main">
        <div className="dashboard-content instructor-shell__content" ref={contentRef}>
          {children}
        </div>
      </div>
    </div>
  );
}
