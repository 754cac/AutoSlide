import React from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';

const DEFAULT_NAV_ITEMS = [
  {
    key: 'dashboard',
    label: 'Dashboard',
    icon: 'dashboard',
    to: '/dashboard',
    toByRole: {
      Student: '/dashboard',
      Teacher: '/teacher/dashboard',
      Instructor: '/teacher/dashboard'
    },
    activeMatch: ['/dashboard', '/teacher/dashboard']
  },
  {
    key: 'courses',
    label: 'Courses',
    icon: 'courses',
    toByRole: {
      Teacher: '/teacher/courses',
      Instructor: '/teacher/courses'
    },
    activeMatch: ['/teacher/courses'],
    hiddenForRoles: ['Instructor']
  },
  {
    key: 'history',
    label: 'History',
    icon: 'history',
    toByRole: {
      Teacher: '/teacher/history',
      Instructor: '/teacher/history'
    },
    activeMatch: ['/teacher/history'],
    hiddenForRoles: ['Instructor']
  },
  { key: 'profile', label: 'Profile', icon: 'profile', to: '/profile', activeMatch: ['/profile'] }
];

const ICONS = {
  dashboard: 'fa-solid fa-gauge',
  courses: 'fa-solid fa-book',
  history: 'fa-solid fa-clock-rotate-left',
  profile: 'fa-solid fa-circle-user'
};

function safeParseUser() {
  if (typeof window === 'undefined') return {};

  try {
    return JSON.parse(localStorage.getItem('user') || '{}');
  } catch (error) {
    return {};
  }
}

function normalizeRole(role) {
  const value = String(role || '').trim().toLowerCase();

  if (value === 'teacher' || value === 'instructor') return 'Instructor';
  if (value === 'student') return 'Student';
  return role || 'Student';
}

function getRoleVariants(role) {
  const normalized = String(role || '').trim().toLowerCase();

  if (normalized === 'teacher' || normalized === 'instructor') return ['Teacher', 'Instructor'];
  if (normalized === 'student') return ['Student'];

  return [normalizeRole(role)];
}

function getDisplayName(user) {
  return user?.fullName || user?.name || user?.email || 'Student';
}

function getInitials(displayName) {
  const parts = String(displayName || '')
    .trim()
    .split(/\s+/)
    .filter(Boolean);

  if (parts.length === 0) return 'A';

  return parts.slice(0, 2).map((part) => part[0]).join('').toUpperCase();
}

function isActivePath(item, pathname, target) {
  if (item.activeMatch && Array.isArray(item.activeMatch)) {
    return item.activeMatch.some((matchPath) => pathname === matchPath || pathname.startsWith(`${matchPath}/`));
  }

  if (!target) return false;

  return pathname === target || pathname.startsWith(`${target}/`);
}

function matchesRoleList(roleVariants, roleList = []) {
  if (!Array.isArray(roleList) || roleList.length === 0) return true;

  return roleList.some((role) => roleVariants.some((variant) => variant.toLowerCase() === String(role).toLowerCase()));
}

function resolveNavTarget(item, roleVariants) {
  if (item.toByRole && typeof item.toByRole === 'object') {
    for (const variant of roleVariants) {
      if (item.toByRole[variant]) {
        return item.toByRole[variant];
      }
    }
  }

  return item.to || null;
}

function shouldDisableItem(item, roleVariants) {
  if (item.disabled === true) return true;
  if (!Array.isArray(item.disabledForRoles) || item.disabledForRoles.length === 0) return false;

  return item.disabledForRoles.some((role) => roleVariants.some((variant) => variant.toLowerCase() === String(role).toLowerCase()));
}

export default function SidebarNav({
  navItems = DEFAULT_NAV_ITEMS,
  brandTitle = 'AutoSlide',
  brandSubtitle = 'Hybrid Classroom',
  ariaLabel = 'Workspace navigation',
  primaryAction = null,
  user = null
}) {
  const location = useLocation();
  const navigate = useNavigate();
  const resolvedUser = user || safeParseUser();
  const displayName = getDisplayName(resolvedUser);
  const roleLabel = normalizeRole(resolvedUser?.role || resolvedUser?.Role || 'Student');
  const roleVariants = getRoleVariants(roleLabel);
  const initials = getInitials(displayName);

  const visibleNavItems = Array.isArray(navItems)
    ? navItems.filter((item) => {
        if (!matchesRoleList(roleVariants, item.roles || item.visibleForRoles || item.allowedRoles)) {
          return false;
        }

        const target = resolveNavTarget(item, roleVariants);
        if (!target && !shouldDisableItem(item, roleVariants)) {
          return false;
        }

        if (Array.isArray(item.hiddenForRoles) && item.hiddenForRoles.some((role) => roleVariants.some((variant) => variant.toLowerCase() === String(role).toLowerCase()))) {
          return false;
        }

        return true;
      })
    : [];

  const handleLogout = () => {
    localStorage.removeItem('token');
    localStorage.removeItem('user');
    navigate('/login');
  };

  return (
    <aside className="dashboard-sidebar">
      <div className="dashboard-sidebar__body">
        <div className="dashboard-sidebar__brand">
          <span className="dashboard-sidebar__logo">{brandTitle.charAt(0).toUpperCase()}</span>
          <div>
            <p className="dashboard-sidebar__wordmark">{brandTitle}</p>
            <p className="dashboard-sidebar__subbrand">{brandSubtitle}</p>
          </div>
        </div>

        {primaryAction ? <div className="dashboard-sidebar__primary-action">{primaryAction}</div> : null}

        <nav className="dashboard-sidebar__nav" aria-label={ariaLabel}>
          {visibleNavItems.map((item) => {
            const target = resolveNavTarget(item, roleVariants);
            const disabled = shouldDisableItem(item, roleVariants) || !target;
            const active = isActivePath(item, location.pathname, target);
            const icon = ICONS[item.icon || item.key] || ICONS.dashboard;

            if (disabled) {
              return (
                <span
                  key={item.key}
                  className={`dashboard-nav-link${active ? ' is-active' : ''} is-disabled`}
                  aria-disabled="true"
                >
                  <span className="dashboard-nav-link__icon">{icon}</span>
                  <span>{item.label}</span>
                </span>
              );
            }

            return (
              <Link
                key={item.key}
                to={target}
                reloadDocument
                className={`dashboard-nav-link${active ? ' is-active' : ''}`}
              >
                <span className="dashboard-nav-link__icon">
                  <i className={icon} aria-hidden="true" />
                </span>
                <span>{item.label}</span>
              </Link>
            );
          })}
        </nav>

        <div className="dashboard-sidebar__spacer" aria-hidden="true" />
      </div>

      <div className="dashboard-sidebar__footer">
        <div className="dashboard-sidebar__profile">
          <span className="dashboard-sidebar__avatar" aria-hidden="true">
            {initials}
          </span>
          <div className="dashboard-sidebar__profile-copy">
            <p className="dashboard-sidebar__profile-name">{displayName}</p>
            <p className="dashboard-sidebar__profile-role">{roleLabel}</p>
          </div>
        </div>

        <button type="button" className="btn btn-outline dashboard-sidebar__logout" onClick={handleLogout}>
          Logout
        </button>
      </div>
    </aside>
  );
}
