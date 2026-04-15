import React from 'react';
import { Link, useLocation } from 'react-router-dom';

const NAV_ITEMS = [
  { to: '/dashboard', label: 'Dashboard', key: 'dashboard' },
  { to: '/history', label: 'History', key: 'history' },
  { to: '/profile', label: 'Profile', key: 'profile' }
];

const ICONS = {
  dashboard: 'fa-solid fa-gauge',
  history: 'fa-solid fa-clock-rotate-left',
  person: 'fa-solid fa-circle-user'
};

export default function SidebarNav({ navItems = NAV_ITEMS }) {
  const location = useLocation();

  return (
    <aside className="dashboard-sidebar">
      <div className="dashboard-sidebar__brand">
        <span className="dashboard-sidebar__logo">A</span>
        <div>
          <p className="dashboard-sidebar__wordmark">AutoSlide</p>
          <p className="dashboard-sidebar__subbrand">Hybrid Classroom</p>
        </div>
      </div>

      <nav className="dashboard-sidebar__nav" aria-label="Student navigation">
        {navItems.map((item) => {
          const active = location.pathname === item.to;

          return (
            <Link key={item.key} to={item.to} className={`dashboard-nav-link${active ? ' is-active' : ''}`}>
              <span className="dashboard-nav-link__icon">
                <i className={ICONS[item.icon || item.key]} aria-hidden="true" />
              </span>
              <span>{item.label}</span>
            </Link>
          );
        })}
      </nav>
    </aside>
  );
}
