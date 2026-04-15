import React, { useEffect, useMemo, useState } from 'react';
import { useLocation } from 'react-router-dom';
import TeacherDashboardShell from '../components/instructor-workspace/TeacherDashboardShell';
import AppShell from '../components/ui/AppShell';
import Card from '../components/ui/Card';
import useDocumentTitle from '../hooks/useDocumentTitle';
import { getCurrentUser, updatePassword } from '../services/studentApi';
import { getToken, isTokenValid } from '../utils/auth';

function ProfileRow({ label, value }) {
  return (
    <div className="profile-row">
      <span className="profile-row__label">{label}</span>
      <span className="profile-row__value">{value || '—'}</span>
    </div>
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
  onBlur,
  onKeyUp
}) {
  return (
    <label className="profile-field" htmlFor={id}>
      <span className="profile-field__label">{label}</span>
      <div className="profile-field__control">
        <input
          id={id}
          type={visible ? 'text' : 'password'}
          className={`profile-input${error ? ' is-error' : ''}`}
          value={value}
          onChange={onChange}
          onBlur={onBlur}
          onKeyUp={onKeyUp}
          autoComplete={autoComplete}
          placeholder={placeholder}
        />
        <button type="button" className="profile-field__toggle btn btn-outline" onClick={onToggle}>
          {visible ? 'Hide' : 'Show'}
        </button>
      </div>
      {helperText ? <span className="profile-field__helper">{helperText}</span> : null}
      {error ? <span className="profile-field__error">{error}</span> : null}
    </label>
  );
}

function mapPasswordChangeError(error) {
  const code = error?.payload?.code || '';
  const message = error?.payload?.message || error?.message || 'Unable to update password right now.';

  switch (code) {
    case 'current_password_required':
    case 'current_password_incorrect':
      return {
        fieldErrors: { currentPassword: message },
        message: ''
      };
    case 'new_password_required':
    case 'password_too_weak':
    case 'password_same_as_current':
      return {
        fieldErrors: { newPassword: message },
        message: ''
      };
    case 'confirm_password_required':
    case 'password_mismatch':
      return {
        fieldErrors: { confirmPassword: message },
        message: ''
      };
    default:
      return {
        fieldErrors: {},
        message
      };
  }
}

export default function ProfilePage() {
  const location = useLocation();

  useDocumentTitle('Profile | AutoSlide');

  const [user, setUser] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [reloadToken, setReloadToken] = useState(0);

  const [passwordForm, setPasswordForm] = useState({
    currentPassword: '',
    newPassword: '',
    confirmPassword: ''
  });
  const [touched, setTouched] = useState({});
  const [showPasswords, setShowPasswords] = useState({
    currentPassword: false,
    newPassword: false,
    confirmPassword: false
  });
  const [submitState, setSubmitState] = useState({ status: 'idle', message: '', fieldErrors: {} });

  const loadProfile = async () => {
    setLoading(true);
    setError('');

    try {
      const currentUser = await getCurrentUser();
      setUser(currentUser || null);
    } catch (loadError) {
      setError(loadError.message || 'Unable to load profile data.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadProfile();
  }, [reloadToken, location.key]);

  const localUser = useMemo(() => {
    try {
      return JSON.parse(localStorage.getItem('user') || '{}');
    } catch (parseError) {
      return {};
    }
  }, []);

  const profile = user || localUser || {};
  const passwordErrors = useMemo(() => {
    const currentPassword = passwordForm.currentPassword;
    const newPassword = passwordForm.newPassword;
    const confirmPassword = passwordForm.confirmPassword;
    const errors = {};

    if (!currentPassword) errors.currentPassword = 'Current password is required.';
    if (!newPassword) errors.newPassword = 'New password is required.';
    else if (newPassword.length < 8) errors.newPassword = 'Use at least 8 characters.';
    else if (newPassword === currentPassword) errors.newPassword = 'New password must differ from current password.';
    if (!confirmPassword) errors.confirmPassword = 'Please confirm your new password.';
    else if (confirmPassword !== newPassword) errors.confirmPassword = 'Passwords do not match.';

    return errors;
  }, [passwordForm]);

  const combinedPasswordErrors = {
    ...passwordErrors,
    ...(submitState.fieldErrors || {})
  };

  const canSubmitPassword = Object.keys(passwordErrors).length === 0 && submitState.status !== 'loading';

  const displayName = profile.fullName || profile.name || profile.email || 'Profile';
  const email = profile.email || profile.Email || '';
  const role = profile.role || profile.Role || 'Student';
  const isInstructorRole = role === 'Teacher' || role === 'Instructor';
  const ShellComponent = isInstructorRole ? TeacherDashboardShell : AppShell;
  const shellProps = isInstructorRole
    ? {}
    : {
        title: 'Profile',
        subtitle: 'Account summary and security settings.'
      };

  const setPasswordField = (field, value) => {
    setPasswordForm((prev) => ({ ...prev, [field]: value }));
    setSubmitState({ status: 'idle', message: '', fieldErrors: {} });
  };

  const handleSubmitPassword = async (event) => {
    event.preventDefault();

    setTouched({ currentPassword: true, newPassword: true, confirmPassword: true });

    if (!canSubmitPassword) {
      setSubmitState((prev) => ({
        ...prev,
        status: 'idle',
        message: '',
        fieldErrors: {}
      }));
      return;
    }

    try {
      setSubmitState({ status: 'loading', message: '', fieldErrors: {} });
      const result = await updatePassword({
        currentPassword: passwordForm.currentPassword,
        newPassword: passwordForm.newPassword,
        confirmPassword: passwordForm.confirmPassword
      });
      setPasswordForm({ currentPassword: '', newPassword: '', confirmPassword: '' });
      setTouched({});
      setShowPasswords({
        currentPassword: false,
        newPassword: false,
        confirmPassword: false
      });
      setSubmitState({
        status: 'success',
        message: result.message || 'Password updated successfully. Your current session stays active.',
        fieldErrors: {}
      });
    } catch (submitError) {
      const mappedError = mapPasswordChangeError(submitError);

      if (Object.keys(mappedError.fieldErrors).length > 0) {
        setSubmitState({
          status: 'idle',
          message: '',
          fieldErrors: mappedError.fieldErrors
        });
        return;
      }

      setSubmitState({
        status: 'error',
        message: mappedError.message || 'Unable to update password.',
        fieldErrors: {}
      });
    }
  };

  if (loading) {
    return (
      <ShellComponent {...shellProps}>
        <div className="profile-page">
          <Card className="profile-hero-card">
            <p className="profile-hero-card__eyebrow">Account overview</p>
            <h2 className="profile-hero-card__title">Loading profile...</h2>
            <p className="profile-hero-card__subtitle">Retrieving your account details now.</p>
          </Card>
          <Card>
            <p className="student-muted-text">Loading account data...</p>
          </Card>
        </div>
      </ShellComponent>
    );
  }

  if (error) {
    return (
      <ShellComponent {...shellProps}>
        <div className="profile-page">
          <Card className="profile-alert-card">
            <div>
              <h2 className="profile-section-title">Unable to load profile</h2>
              <p className="student-muted-text">{error}</p>
            </div>
            <button type="button" className="btn btn-outline" onClick={() => setReloadToken((value) => value + 1)}>
              Retry
            </button>
          </Card>
        </div>
      </ShellComponent>
    );
  }

  return (
    <ShellComponent {...shellProps}>
      <div className="profile-page">
        <Card className="profile-hero-card">
          <div>
            <p className="profile-hero-card__eyebrow">Account overview</p>
            <h2 className="profile-hero-card__title">{displayName}</h2>
            <p className="profile-hero-card__subtitle">{email || 'No email address available'}</p>
          </div>
          <div className="profile-hero-card__chips">
            <span className="profile-chip">{role}</span>
          </div>
        </Card>

        <div className="profile-layout">
          <div className="profile-stack">
            <Card className="profile-section-card">
              <h3 className="profile-section-title">Identity</h3>
              <div className="profile-kv-grid">
                <ProfileRow label="Display name" value={displayName} />
                <ProfileRow label="Email" value={email} />
                <ProfileRow label="Role" value={role} />
              </div>
            </Card>

          </div>

          <div className="profile-stack">
            <Card className="profile-section-card">
              <h3 className="profile-section-title">Change Password</h3>
              <p className="profile-section-subtitle">
                Set a new password for this account. Your current session stays active after a successful change.
              </p>

              <form className="profile-password-form" onSubmit={handleSubmitPassword} noValidate>
                <PasswordField
                  id="current-password"
                  label="Current password"
                  value={passwordForm.currentPassword}
                  onChange={(event) => setPasswordField('currentPassword', event.target.value)}
                  visible={showPasswords.currentPassword}
                  onToggle={() => setShowPasswords((prev) => ({ ...prev, currentPassword: !prev.currentPassword }))}
                  autoComplete="current-password"
                  error={(touched.currentPassword || submitState.status === 'error' || submitState.fieldErrors.currentPassword) ? combinedPasswordErrors.currentPassword : ''}
                  placeholder="Enter your current password"
                  helperText="Required to verify your identity before changing your password."
                  onBlur={() => setTouched((prev) => ({ ...prev, currentPassword: true }))}
                />

                <PasswordField
                  id="new-password"
                  label="New password"
                  value={passwordForm.newPassword}
                  onChange={(event) => setPasswordField('newPassword', event.target.value)}
                  visible={showPasswords.newPassword}
                  onToggle={() => setShowPasswords((prev) => ({ ...prev, newPassword: !prev.newPassword }))}
                  autoComplete="new-password"
                  error={(touched.newPassword || submitState.status === 'error' || submitState.fieldErrors.newPassword) ? combinedPasswordErrors.newPassword : ''}
                  placeholder="At least 8 characters"
                  helperText="Use at least 8 characters and make it different from your current password."
                  onBlur={() => setTouched((prev) => ({ ...prev, newPassword: true }))}
                />

                <PasswordField
                  id="confirm-password"
                  label="Confirm new password"
                  value={passwordForm.confirmPassword}
                  onChange={(event) => setPasswordField('confirmPassword', event.target.value)}
                  visible={showPasswords.confirmPassword}
                  onToggle={() => setShowPasswords((prev) => ({ ...prev, confirmPassword: !prev.confirmPassword }))}
                  autoComplete="new-password"
                  error={(touched.confirmPassword || submitState.status === 'error' || submitState.fieldErrors.confirmPassword) ? combinedPasswordErrors.confirmPassword : ''}
                  placeholder="Re-enter the new password"
                  helperText="Must match your new password exactly."
                  onBlur={() => setTouched((prev) => ({ ...prev, confirmPassword: true }))}
                />

                <div className="profile-password-form__support-note" aria-live="polite">
                  <span className="profile-field__helper">Your current session remains active after a successful password change.</span>
                </div>

                {submitState.message ? (
                  <div
                    className={`profile-form-banner profile-form-banner--${submitState.status}`}
                    aria-live="polite"
                  >
                    {submitState.message}
                  </div>
                ) : null}

                <div className="profile-form-actions">
                  <button
                    type="submit"
                    className="btn btn-primary"
                    disabled={!canSubmitPassword}
                  >
                    {submitState.status === 'loading' ? 'Updating...' : 'Update Password'}
                  </button>
                </div>
              </form>
            </Card>

          </div>
        </div>
      </div>
    </ShellComponent>
  );
}
