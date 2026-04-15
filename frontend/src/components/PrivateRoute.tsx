import React, { useMemo } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { isTokenValid } from '../utils/auth';
import { buildLocationPath, buildLoginRedirectUrl } from '../utils/authRedirect';

function normalizeRole(role) {
  if (!role && role !== 0) return null;

  if (typeof role === 'string') {
    const normalized = role.trim().toLowerCase();

    if (normalized === 'teacher' || normalized === 'instructor') return 'Teacher';
    if (normalized === 'student') return 'Student';
    if (/^\d+$/.test(normalized)) return normalized === '0' ? 'Teacher' : 'Student';
  }

  if (typeof role === 'number') return role === 0 ? 'Teacher' : 'Student';
  return null;
}

function resolveAuthState() {
  const token = localStorage.getItem('token');

  if (!token || !isTokenValid(token)) {
    if (token) {
      localStorage.removeItem('token');
      localStorage.removeItem('user');
    }

    return { isAuthenticated: false, userRole: null };
  }

  const userJson = localStorage.getItem('user');

  try {
    const user = userJson ? JSON.parse(userJson) : null;
    return {
      isAuthenticated: true,
      userRole: normalizeRole(user?.role)
    };
  } catch (error) {
    localStorage.removeItem('token');
    localStorage.removeItem('user');
    return { isAuthenticated: false, userRole: null };
  }
}

export default function PrivateRoute({ children, allowedRoles = null }) {
  const location = useLocation();
  const authState = useMemo(
    () => resolveAuthState(),
    [location.pathname, location.search, location.key]
  );

  if (!authState.isAuthenticated) {
    const redirectTarget = buildLocationPath(location);
    return (
      <Navigate
        to={buildLoginRedirectUrl(redirectTarget)}
        state={{ from: location }}
        replace
      />
    );
  }

  if (allowedRoles && Array.isArray(allowedRoles)) {
    if (!authState.userRole || !allowedRoles.includes(authState.userRole)) {
      return <Navigate to="/403" replace />;
    }
  }

  return children;
}
