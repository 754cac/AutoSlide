import React, { useEffect, useMemo, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { login } from '../services/authApi';
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

function validateLoginForm(email, password) {
  const nextErrors = {};
  const trimmedEmail = email.trim();

  if (!trimmedEmail) {
    nextErrors.email = 'Email is required.';
  } else if (!/^\S+@\S+\.\S+$/.test(trimmedEmail)) {
    nextErrors.email = 'Enter a valid email address.';
  }

  if (!password) {
    nextErrors.password = 'Password is required.';
  }

  return nextErrors;
}

function readStoredUser() {
  try {
    return JSON.parse(localStorage.getItem('user') || '{}');
  } catch (error) {
    return {};
  }
}

function buildStoredUser(authResult, currentUser, email) {
  const cachedUser = readStoredUser();
  const resolvedRole = normalizeRole(currentUser?.role || currentUser?.Role || authResult.role || cachedUser.role);
  const resolvedEmail = currentUser?.email || currentUser?.Email || email || cachedUser.email || '';
  const resolvedName = currentUser?.fullName || currentUser?.name || authResult.fullName || cachedUser.fullName || cachedUser.name || resolvedEmail;

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
  if (error?.status === 400 || error?.status === 401) {
    return 'Invalid email or password.';
  }

  return 'Unable to sign in right now. Check your connection and try again.';
}

function AuthHeroPanel() {
  return (
    <section className="auth-hero" aria-hidden="true">
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
        <p className="auth-hero__eyebrow">Welcome to the workspace</p>
        <h1 className="auth-hero__title">Teaching sessions, materials, and replays in one place.</h1>
        <p className="auth-hero__description">
          Sign in to access your courses, join live sessions, and continue learning with released materials and replay history.
        </p>

        <div className="auth-hero__highlights">
          <div className="auth-highlight-card">
            <span className="auth-highlight-card__label">Live sessions</span>
            <span className="auth-highlight-card__value">Jump in as a class starts</span>
          </div>
          <div className="auth-highlight-card">
            <span className="auth-highlight-card__label">Released materials</span>
            <span className="auth-highlight-card__value">Slide decks and resources in context</span>
          </div>
          <div className="auth-highlight-card">
            <span className="auth-highlight-card__label">Replay history</span>
            <span className="auth-highlight-card__value">Review sessions whenever you need</span>
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
  error,
  visible,
  onToggle,
  autoComplete = 'current-password',
  placeholder = ''
}) {
  const inputId = id || 'password';
  const helperId = `${inputId}-helper`;
  const errorId = `${inputId}-error`;
  const showError = Boolean(error);

  return (
    <div className="auth-field">
      <label className="auth-label" htmlFor={inputId}>
        {label}
      </label>
      <div className="auth-password-control">
        <input
          id={inputId}
          className={`auth-input${showError ? ' is-error' : ''}`}
          type={visible ? 'text' : 'password'}
          value={value}
          onChange={onChange}
          autoComplete={autoComplete}
          placeholder={placeholder}
          aria-invalid={showError}
          aria-describedby={`${helperId}${showError ? ` ${errorId}` : ''}`}
        />
        <button
          type="button"
          className="auth-password-toggle"
          onClick={onToggle}
          aria-label={visible ? 'Hide password' : 'Show password'}
          aria-pressed={visible}
        >
          {visible ? 'Hide' : 'Show'}
        </button>
      </div>
      <div id={helperId} className="auth-field__helper">
        Password must be entered to sign in.
      </div>
      {showError ? (
        <div id={errorId} className="auth-field__error" role="alert">
          {error}
        </div>
      ) : null}
    </div>
  );
}

export default function LoginPage() {
  const [checkingSession, setCheckingSession] = useState(true);
  const rememberedEmail = useMemo(() => localStorage.getItem('autoslide.rememberedEmail') || '', []);
  const [email, setEmail] = useState(rememberedEmail);
  const [password, setPassword] = useState('');
  const [rememberMe, setRememberMe] = useState(Boolean(rememberedEmail));
  const [showPassword, setShowPassword] = useState(false);
  const [loading, setLoading] = useState(false);
  const [submitAttempted, setSubmitAttempted] = useState(false);
  const [touched, setTouched] = useState({ email: false, password: false });
  const [formError, setFormError] = useState('');

  const location = useLocation();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const redirectParam = readRedirectParam(searchParams);
  const stateRedirect = buildLocationPath(location.state?.from);
  const preservedRedirect = stateRedirect || redirectParam;

  const emailErrors = useMemo(() => validateLoginForm(email, password), [email, password]);
  const emailError = (submitAttempted || touched.email) ? emailErrors.email : '';
  const passwordError = (submitAttempted || touched.password) ? emailErrors.password : '';
  const hasFieldErrors = Boolean(emailErrors.email || emailErrors.password);
  const submitDisabled = loading;

  const persistRememberedEmail = (nextRememberMe, nextEmail) => {
    if (nextRememberMe) {
      localStorage.setItem('autoslide.rememberedEmail', nextEmail);
    } else {
      localStorage.removeItem('autoslide.rememberedEmail');
    }
  };

  const getRedirectTarget = (user) => {
    const defaultTarget = normalizeRole(user?.role) === 'Teacher' ? '/teacher/dashboard' : '/dashboard';
    return sanitizeInternalRedirect(preservedRedirect, defaultTarget);
  };

  const navigatePostLogin = (user) => {
    navigate(getRedirectTarget(user), { replace: true });
  };

  useEffect(() => {
    document.title = 'Login | AutoSlide';

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
          const storedUser = buildStoredUser(
            {
              role: currentUser.role,
              fullName: currentUser.fullName,
              email: currentUser.email
            },
            currentUser,
            currentUser.email || rememberedEmail
          );

          localStorage.setItem('user', JSON.stringify(storedUser));
          navigatePostLogin(storedUser);
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
  }, [navigate, rememberedEmail, preservedRedirect]);

  const handleSubmit = async (event) => {
    event.preventDefault();
    setSubmitAttempted(true);
    setFormError('');
    setTouched({ email: true, password: true });

    if (hasFieldErrors) {
      return;
    }

    try {
      setLoading(true);
      const trimmedEmail = email.trim();
      const authResult = await login({ email: trimmedEmail, password });

      if (!authResult.token) {
        throw new Error('Login failed');
      }

      setToken(authResult.token);

      let currentUser = null;
      try {
        currentUser = await getCurrentUser();
      } catch (error) {
        currentUser = null;
      }

      const storedUser = buildStoredUser(authResult, currentUser, trimmedEmail);
      localStorage.setItem('user', JSON.stringify(storedUser));
      persistRememberedEmail(rememberMe, trimmedEmail);
      navigatePostLogin(storedUser);
    } catch (error) {
      setFormError(normalizeApiError(error));
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
            <p className="auth-card__eyebrow">Student and teacher access</p>
            <h2 className="auth-card__title">Login to Workspace</h2>
            <p className="auth-card__subtitle">
              Use your institutional email and password to enter your AutoSlide workspace.
            </p>
          </div>

          {formError ? (
            <div className="auth-banner auth-banner--error" role="alert">
              {formError}
            </div>
          ) : null}

          <form className="auth-form" onSubmit={handleSubmit} noValidate>
            <div className="auth-field">
              <label className="auth-label" htmlFor="email">
                Email
              </label>
              <input
                id="email"
                className={`auth-input${emailError ? ' is-error' : ''}`}
                type="email"
                value={email}
                onChange={(event) => {
                  const nextEmail = event.target.value;
                  setEmail(nextEmail);
                  setFormError('');
                  if (rememberMe) {
                    persistRememberedEmail(true, nextEmail);
                  }
                }}
                onBlur={() => setTouched((prev) => ({ ...prev, email: true }))}
                autoComplete="email"
                placeholder="name@university.edu"
                aria-invalid={Boolean(emailError)}
                aria-describedby={emailError ? 'login-email-error' : undefined}
                disabled={loading}
              />
              {emailError ? (
                <div id="login-email-error" className="auth-field__error" role="alert">
                  {emailError}
                </div>
              ) : null}
            </div>

            <PasswordField
              id="login-password"
              label="Password"
              value={password}
              onChange={(event) => {
                setPassword(event.target.value);
                setFormError('');
              }}
              error={passwordError}
              visible={showPassword}
              onToggle={() => setShowPassword((value) => !value)}
              autoComplete="current-password"
              placeholder="Enter your password"
            />

            <div className="auth-form__meta-row">
              <label className="auth-checkbox">
                <input
                  type="checkbox"
                  checked={rememberMe}
                  onChange={(event) => {
                    const nextRememberMe = event.target.checked;
                    setRememberMe(nextRememberMe);
                    persistRememberedEmail(nextRememberMe, email.trim());
                  }}
                />
                <span>Remember me</span>
              </label>

              <Link className="auth-link" to={{ pathname: '/forgot-password', search: location.search }}>
                Forgot password?
              </Link>
            </div>

            <button className="auth-submit" type="submit" disabled={submitDisabled}>
              {loading ? 'Signing in...' : 'Login to Workspace'}
            </button>
          </form>

          <div className="auth-card__footer">
            <span>Need an account?</span>
            <Link to={{ pathname: '/register', search: location.search }} className="auth-link auth-link--strong">
              Register
            </Link>
          </div>
        </div>
      </section>
    </main>
  );
}
