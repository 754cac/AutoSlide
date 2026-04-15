import React from 'react';

export default function TopBar({ name, role, avatarUrl }) {
  return (
    <header className="dashboard-topbar">
      <div className="dashboard-topbar__identity">
        <img src={avatarUrl} alt={`${name} avatar`} className="dashboard-topbar__avatar" />
        <div>
          <p className="dashboard-topbar__name">{name}</p>
          <p className="dashboard-topbar__role">{role}</p>
        </div>
      </div>
    </header>
  );
}
