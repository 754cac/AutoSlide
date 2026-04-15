import React from 'react';
import { Link } from 'react-router-dom';
import useDocumentTitle from '../hooks/useDocumentTitle';

export default function AccessDeniedPage() {
  useDocumentTitle('Access Denied | AutoSlide');

  return (
    <div className="container mx-auto p-4 text-center">
      <h1 className="text-4xl font-bold mb-4 text-red-600">403 - Access Denied</h1>
      <p className="mb-4">You do not have permission to view this page.</p>
      <Link to="/" style={{ color: '#5f6d52', textDecoration: 'underline' }}>Go Home</Link>
    </div>
  );
}
