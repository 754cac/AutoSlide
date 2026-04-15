import React from 'react';
import SidebarNav from './SidebarNav';
import PageContainer from './PageContainer';

export default function AppShell({ sidebarProps, children }) {
  return (
    <div className="student-app-shell">
      <SidebarNav {...sidebarProps} />
      <div className="student-app-shell__main">
        <PageContainer>{children}</PageContainer>
      </div>
    </div>
  );
}
