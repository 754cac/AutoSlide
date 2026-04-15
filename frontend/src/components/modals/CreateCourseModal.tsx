import React, { useState } from 'react';
import { apiUrl } from '../../utils/api';

const CreateCourseModal = ({ onClose, onCreated }) => {
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState('');

  const handleSubmit = async (e) => {
    e.preventDefault();

    if (submitting) return;

    setSubmitting(true);
    setSubmitError('');

    const token = localStorage.getItem('token');
    try {
      const response = await fetch(apiUrl('/api/courses'), {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify({ code, name })
      });

      const responseText = await response.text();
      let payload = null;

      if (responseText) {
        try {
          payload = JSON.parse(responseText);
        } catch (error) {
          payload = null;
        }
      }

      if (!response.ok) {
        setSubmitError(
          payload?.message || payload?.error || 'Failed to create course. Please try again.'
        );
        return;
      }

      onClose();

      if (typeof onCreated === 'function') {
        await onCreated(payload);
      }
    } catch (error) {
      setSubmitError('Failed to create course. Please try again.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div style={{
      position: 'fixed', top: 0, left: 0, right: 0, bottom: 0,
      backgroundColor: 'rgba(0,0,0,0.5)', display: 'flex', justifyContent: 'center', alignItems: 'center', zIndex: 1000
    }}>
      <div style={{
        backgroundColor: '#fbf8f2', padding: '2rem', borderRadius: '12px', width: '400px', boxShadow: '0 20px 40px rgba(15,23,42,0.16)', border: '1px solid rgba(123,107,86,0.18)'
      }}>
        <h2 style={{ marginTop: 0, marginBottom: '1.5rem' }}>Create New Course</h2>
        <form onSubmit={handleSubmit}>
          <div style={{ marginBottom: '1rem' }}>
            <label style={{ display: 'block', marginBottom: '0.5rem', fontWeight: '500' }}>Course Code</label>
            <input 
              type="text" 
              value={code}
              onChange={(e) => setCode(e.target.value)}
              disabled={submitting}
              style={{ width: '100%', padding: '0.5rem', border: '1px solid #d7ccb8', borderRadius: '8px', background: '#fffdf8' }}
              placeholder="e.g. CS101"
              required
            />
          </div>
          <div style={{ marginBottom: '1.5rem' }}>
            <label style={{ display: 'block', marginBottom: '0.5rem', fontWeight: '500' }}>Course Name</label>
            <input 
              type="text" 
              value={name}
              onChange={(e) => setName(e.target.value)}
              disabled={submitting}
              style={{ width: '100%', padding: '0.5rem', border: '1px solid #d7ccb8', borderRadius: '8px', background: '#fffdf8' }}
              placeholder="e.g. Intro to CS"
              required
            />
          </div>

          {submitError ? (
            <p role="alert" style={{ color: '#9f1239', margin: '0 0 1rem 0' }}>
              {submitError}
            </p>
          ) : null}

          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '1rem' }}>
            <button 
              type="button" 
              onClick={onClose}
              disabled={submitting}
              style={{ padding: '0.5rem 1rem', border: 'none', background: '#eee6d8', borderRadius: '8px', cursor: 'pointer', color: '#4a463d' }}
            >
              Cancel
            </button>
            <button 
              type="submit"
              disabled={submitting}
              style={{ padding: '0.5rem 1rem', border: 'none', background: '#5f6d52', color: 'white', borderRadius: '8px', cursor: 'pointer' }}
            >
              {submitting ? 'Creating...' : 'Create'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
};

export default CreateCourseModal;
