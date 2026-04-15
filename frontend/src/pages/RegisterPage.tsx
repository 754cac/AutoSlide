import React, { useEffect, useMemo, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { register } from '../services/authApi';
import { getCurrentUser } from '../services/studentApi';
import { clearToken, getToken, isTokenValid, setToken } from '../utils/auth';
import { buildLocationPath, readRedirectParam, sanitizeInternalRedirect } from '../utils/authRedirect';

function normalizeRole(role) {
  if (role === null || role === undefined || role === '') return 'Student';

  if (typeof role === 'string') {
    if (role === 'Teacher' || role === 'Student') return role;
    if (role === '0') return 'Teacher';
    if (role === '1') return 'Student';
    const lowered = role.toLowerCase();
    if (lowered === 'teacher') return 'Teacher';
    if (lowered === 'student') return 'Student';
  }

  if (typeof role === 'number') {
    return role === 0 ? 'Teacher' : 'Student';
  }

  return 'Student';
}

function validateRegisterForm(values) {
  const errors = {};
  const trimmedEmail = values.email.trim();
  const trimmedName = values.fullName.trim();
  const password = values.password;
  const confirmPassword = values.confirmPassword;

  if (!trimmedName) {
    errors.fullName = 'Full name is required.';
  }

  if (!trimmedEmail) {
    errors.email = 'Email is required.';
  } else if (!/^\S+@\S+\.\S+$/.test(trimmedEmail)) {
    errors.email = 'Enter a valid email address.';
  }

  if (!password) {
    errors.password = 'Password is required.';
  } else if (password.length < 8) {
    errors.password = 'Use at least 8 characters.';
  }

  if (!confirmPassword) {
    errors.confirmPassword = 'Please confirm your password.';
  } else if (confirmPassword !== password) {
    errors.confirmPassword = 'Passwords do not match.';
  }

  return errors;
}

function readStoredUser() {
  try {
    return JSON.parse(localStorage.getItem('user') || '{}');
  } catch (error) {
    return {};
  }
}

function buildStoredUser(authResult, currentUser, email, fullName) {
  const cachedUser = readStoredUser();
  const resolvedRole = normalizeRole(currentUser?.role || currentUser?.Role || authResult.role || cachedUser.role);
  const resolvedEmail = currentUser?.email || currentUser?.Email || email || cachedUser.email || '';
  const resolvedName = currentUser?.fullName || currentUser?.name || authResult.fullName || fullName || cachedUser.fullName || cachedUser.name || resolvedEmail;

  return {
    id: currentUser?.id || currentUser?.Id || currentUser?.userId || currentUser?.UserId || cachedUser.id || null,
    name: resolvedName,
    fullName: resolvedName,
    role: resolvedRole,
    email: resolvedEmail,
    avatarUrl: currentUser?.avatarUrl || currentUser?.AvatarUrl || cachedUser.avatarUrl || ''
  };
}

function normalizeApiError(error) {
  const message = String(error?.payload?.message || error?.payload || error?.message || '').toLowerCase();

  if (message.includes('email already exists') || message.includes('already exists')) {
    return 'That email is already in use.';
  }

  if (error?.status === 400) {
    return 'Please check your details and try again.';
  }

  return 'Unable to create your account right now. Check your connection and try again.';
}

function AuthHeroPanel() {
  return (
    <section className="auth-hero auth-hero--register" aria-hidden="true">
      <div className="auth-hero__glow auth-hero__glow--one" />
      <div className="auth-hero__glow auth-hero__glow--two" />

      <div className="auth-brand-block">
        <div className="auth-brand-mark">A</div>
        <div>
          <p className="auth-brand-name">AutoSlide</p>
          <p className="auth-brand-subtitle">Hybrid Classroom Workspace</p>
        </div>
      </div>

      <div className="auth-hero__content">
        <p className="auth-hero__eyebrow">Create your account</p>
        <h1 className="auth-hero__title">Join the workspace your courses already use.</h1>
        <p className="auth-hero__description">
          Sign up with your name, email, and password to access the AutoSlide workspace. Course access is added separately through enrollment.
        </p>

        <div className="auth-hero__highlights">
          <div className="auth-highlight-card">
            <span className="auth-highlight-card__label">Account setup</span>
            <span className="auth-highlight-card__value">Create a single sign-in for the platform</span>
          </div>
          <div className="auth-highlight-card">
            <span className="auth-highlight-card__label">Course access</span>
            <span className="auth-highlight-card__value">Enrollment stays managed by the classroom workflow</span>
          </div>
          <div className="auth-highlight-card">
            <span className="auth-highlight-card__label">Workspace ready</span>
            <span className="auth-highlight-card__value">Move straight into your dashboard after signing up</span>
          </div>
        </div>
      </div>
    </section>
  );
}

function PasswordField({
  id,
  label,
  value,
  onChange,
  visible,
  onToggle,
  autoComplete,
  error,
  placeholder,
  helperText,
  onBlur
}) {
  const inputId = id || 'password';
  const helperId = `${inputId}-helper`;
  const errorId = `${inputId}-error`;
  const showError = Boolean(error);

  return (
    <label className="auth-field" htmlFor={inputId}>
      <span className="auth-label">{label}</span>
      <div className="auth-password-control">
        <input
          id={inputId}
          type={visible ? 'text' : 'password'}
          className={`auth-input${showError ? ' is-error' : ''}`}
          value={value}
          onChange={onChange}
          onBlur={onBlur}
          autoComplete={autoComplete}
          placeholder={placeholder}
          aria-invalid={showError}
          aria-describedby={`${helperId}${showError ? ` ${errorId}` : ''}`}
        />
        <button type="button" className="auth-password-toggle" onClick={onToggle} aria-label={visible ? 'Hide password' : 'Show password'} aria-pressed={visible}>
          {visible ? 'Hide' : 'Show'}
        </button>
      </div>
      {helperText ? <span id={helperId} className="auth-field__helper">{helperText}</span> : null}
      {showError ? (
        <span id={errorId} className="auth-field__error" role="alert">
          {error}
        </span>
      ) : null}
    </label>
  );
}

export default function RegisterPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const [searchParams] = useSearchParams();
  const redirectParam = readRedirectParam(searchParams);
  const stateRedirect = buildLocationPath(location.state?.from);
  const preservedRedirect = stateRedirect || redirectParam;

  const [checkingSession, setCheckingSession] = useState(true);
  const [form, setForm] = useState({
    fullName: '',
    email: '',
    password: '',
    confirmPassword: '',
    role: '1'
  });
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);
  const [loading, setLoading] = useState(false);
  const [submitAttempted, setSubmitAttempted] = useState(false);
  const [touched, setTouched] = useState({ fullName: false, email: false, password: false, confirmPassword: false });
  const [formError, setFormError] = useState('');
  const [successMessage, setSuccessMessage] = useState('');

  const validationErrors = useMemo(() => validateRegisterForm(form), [form]);
  const showErrors = submitAttempted || touched.fullName || touched.email || touched.password || touched.confirmPassword;
  const hasFieldErrors = Boolean(
    validationErrors.fullName || validationErrors.email || validationErrors.password || validationErrors.confirmPassword
  );
  const submitDisabled = loading;

  const getRedirectTarget = (user) => {
    const defaultTarget = normalizeRole(user?.role) === 'Teacher' ? '/teacher/dashboard' : '/dashboard';
    return sanitizeInternalRedirect(preservedRedirect, defaultTarget);
  };

  const persistAuth = (authResult, currentUser) => {
    const storedUser = buildStoredUser(authResult, currentUser, form.email.trim(), form.fullName.trim());
    localStorage.setItem('user', JSON.stringify(storedUser));
    return storedUser;
  };

  useEffect(() => {
    document.title = 'Register | AutoSlide';

    let isMounted = true;

    const hydrateExistingSession = async () => {
      const token = getToken();

      if (!token || !isTokenValid(token)) {
        if (token) {
          clearToken();
          localStorage.removeItem('user');
        }

        if (isMounted) {
          setCheckingSession(false);
        }
        return;
      }

      try {
        const currentUser = await getCurrentUser();
        if (!isMounted) return;

        if (currentUser) {
          const cachedUser = readStoredUser();
          const user = buildStoredUser(
            {
              role: currentUser.role,
              fullName: currentUser.fullName,
              email: currentUser.email
            },
            currentUser,
            currentUser.email || cachedUser.email || ''
          );

          localStorage.setItem('user', JSON.stringify(user));
          navigate(getRedirectTarget(user), { replace: true });
          return;
        }
      } catch (error) {
        clearToken();
        localStorage.removeItem('user');
      }

      if (isMounted) {
        setCheckingSession(false);
      }
    };

    hydrateExistingSession();

    return () => {
      isMounted = false;
    };
  }, [navigate, preservedRedirect]);

  const updateField = (field, value) => {
    setForm((prev) => ({ ...prev, [field]: value }));
    setFormError('');
    setSuccessMessage('');
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    setSubmitAttempted(true);
    setFormError('');
    setSuccessMessage('');
    setTouched({ fullName: true, email: true, password: true, confirmPassword: true });

    if (hasFieldErrors) {
      return;
    }

    try {
      setLoading(true);
      const trimmedEmail = form.email.trim();
      const trimmedName = form.fullName.trim();
      const authResult = await register({
        email: trimmedEmail,
        password: form.password,
        fullName: trimmedName,
        role: Number(form.role)
      });

      if (!authResult.token) {
        throw new Error('Registration failed');
      }

      setToken(authResult.token);

      let currentUser = null;
      try {
        currentUser = await getCurrentUser();
      } catch (error) {
        currentUser = null;
      }

      const storedUser = persistAuth(authResult, currentUser);
      navigate(getRedirectTarget(storedUser), { replace: true });
    } catch (error) {
      setFormError(normalizeApiError(error));
      setForm((prev) => ({
        ...prev,
        password: '',
        confirmPassword: ''
      }));
      setShowPassword(false);
      setShowConfirmPassword(false);
    } finally {
      setLoading(false);
    }
  };

  if (checkingSession) {
    return (
      <main className="auth-page">
        <section className="auth-panel auth-panel--full">
          <div className="auth-panel__loading">
            <div className="auth-spinner" aria-hidden="true" />
            <p>Loading your workspace...</p>
          </div>
        </section>
      </main>
    );
  }

  return (
    <main className="auth-page">
      <AuthHeroPanel />

      <section className="auth-panel">
        <div className="auth-panel__mobile-brand">
          <div className="auth-brand-mark">A</div>
          <div>
            <p className="auth-brand-name">AutoSlide</p>
            <p className="auth-brand-subtitle">Hybrid Classroom Workspace</p>
          </div>
        </div>

        <div className="auth-card">
          <div className="auth-card__header">
            <p className="auth-card__eyebrow">Create your account</p>
            <h2 className="auth-card__title">Register for AutoSlide</h2>
            <p className="auth-card__subtitle">
              Set up your workspace account. Course access is managed separately through enrollment.
            </p>
          </div>

          {formError ? (
            <div className="auth-banner auth-banner--error" role="alert">
              {formError}
            </div>
          ) : null}

          {successMessage ? (
            <div className="auth-banner auth-banner--success" role="status">
              {successMessage}
            </div>
          ) : null}

          <form className="auth-form" onSubmit={handleSubmit} noValidate>
            <div className="auth-field">
              <label className="auth-label" htmlFor="fullName">
                Full name
              </label>
              <input
                id="fullName"
                className={`auth-input${showErrors && validationErrors.fullName ? ' is-error' : ''}`}
                type="text"
                value={form.fullName}
                onChange={(event) => updateField('fullName', event.target.value)}
                onBlur={() => setTouched((prev) => ({ ...prev, fullName: true }))}
                autoComplete="name"
                placeholder="Your full name"
                aria-invalid={Boolean(showErrors && validationErrors.fullName)}
                aria-describedby={validationErrors.fullName ? 'register-fullname-error' : undefined}
                disabled={loading}
              />
              {showErrors && validationErrors.fullName ? (
                <span id="register-fullname-error" className="auth-field__error" role="alert">
                  {validationErrors.fullName}
                </span>
              ) : null}
            </div>

            <div className="auth-field">
              <label className="auth-label" htmlFor="email">
                Email
              </label>
              <input
                id="email"
                className={`auth-input${showErrors && validationErrors.email ? ' is-error' : ''}`}
                type="email"
                value={form.email}
                onChange={(event) => updateField('email', event.target.value)}
                onBlur={() => setTouched((prev) => ({ ...prev, email: true }))}
                autoComplete="email"
                placeholder="name@university.edu"
                aria-invalid={Boolean(showErrors && validationErrors.email)}
                aria-describedby={validationErrors.email ? 'register-email-error' : undefined}
                disabled={loading}
              />
              {showErrors && validationErrors.email ? (
                <span id="register-email-error" className="auth-field__error" role="alert">
                  {validationErrors.email}
                </span>
              ) : null}
            </div>

            <PasswordField
              id="register-password"
              label="Password"
              value={form.password}
              onChange={(event) => updateField('password', event.target.value)}
              visible={showPassword}
              onToggle={() => setShowPassword((value) => !value)}
              autoComplete="new-password"
              error={showErrors ? validationErrors.password : ''}
              placeholder="At least 8 characters"
              helperText="Use a secure password with at least 8 characters."
              onBlur={() => setTouched((prev) => ({ ...prev, password: true }))}
            />

            <PasswordField
              id="confirm-password"
              label="Confirm password"
              value={form.confirmPassword}
              onChange={(event) => updateField('confirmPassword', event.target.value)}
              visible={showConfirmPassword}
              onToggle={() => setShowConfirmPassword((value) => !value)}
              autoComplete="new-password"
              error={showErrors ? validationErrors.confirmPassword : ''}
              placeholder="Re-enter your password"
              helperText="Must match your password exactly."
              onBlur={() => setTouched((prev) => ({ ...prev, confirmPassword: true }))}
            />

            <div className="auth-field">
              <label className="auth-label" htmlFor="role">
                Account type
              </label>
              <select
                id="role"
                className="auth-input"
                value={form.role}
                onChange={(event) => updateField('role', event.target.value)}
                disabled={loading}
              >
                <option value="1">Student</option>
                <option value="0">Teacher</option>
              </select>
              <span className="auth-field__helper">
                Choose the account type supported by your access request.
              </span>
            </div>

            <div className="auth-field__helper auth-field__helper--muted">
              Course enrollment is handled separately after account creation.
            </div>

            <button className="auth-submit" type="submit" disabled={submitDisabled}>
              {loading ? 'Creating account...' : 'Create Workspace Account'}
            </button>
          </form>

          <div className="auth-card__footer">
            <span>Already have an account?</span>
            <Link to={{ pathname: '/login', search: location.search }} className="auth-link auth-link--strong">
              Login
            </Link>
          </div>
        </div>
      </section>
    </main>
  );
}
