import React from 'react';
import { Link, useLocation } from 'react-router-dom';
import useDocumentTitle from '../hooks/useDocumentTitle';

export default function ForgotPasswordPage() {
  useDocumentTitle('Forgot Password | AutoSlide');
  const location = useLocation();

  return (
    <div className="container mx-auto p-4">
      <h1 className="text-2xl font-bold mb-4">Forgot Password</h1>
      <p>Password recovery functionality coming soon.</p>
      <Link to={{ pathname: '/login', search: location.search }} style={{ color: '#5f6d52', textDecoration: 'underline' }}>Back to Login</Link>
    </div>
  );
}
