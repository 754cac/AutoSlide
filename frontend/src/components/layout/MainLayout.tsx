import React from 'react';

export default function MainLayout({ children }) {
  return (
    <div className="main-layout viewer-shell">
      {children}
    </div>
  );
}
