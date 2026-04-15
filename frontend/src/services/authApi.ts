import { apiUrl } from '../utils/api';

function readJsonSafely(text) {
  if (!text) return {};

  try {
    return JSON.parse(text);
  } catch (error) {
    return { message: text };
  }
}

export async function login({ email, password }) {
  const response = await fetch(apiUrl('/api/auth/login'), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({ email, password })
  });

  const payload = readJsonSafely(await response.text());

  if (!response.ok) {
    const error = new Error('Login failed');
    error.status = response.status;
    error.payload = payload;
    throw error;
  }

  return {
    token: payload.token || payload.Token || '',
    fullName: payload.fullName || payload.FullName || '',
    role: payload.role || payload.Role || 'Student'
  };
}

export async function register({ email, password, fullName, role }) {
  const response = await fetch(apiUrl('/api/auth/register'), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      email,
      password,
      fullName,
      role: Number(role)
    })
  });

  const payload = readJsonSafely(await response.text());

  if (!response.ok) {
    const error = new Error('Registration failed');
    error.status = response.status;
    error.payload = payload;
    throw error;
  }

  return {
    token: payload.token || payload.Token || '',
    fullName: payload.fullName || payload.FullName || '',
    role: payload.role || payload.Role || 'Student'
  };
}
