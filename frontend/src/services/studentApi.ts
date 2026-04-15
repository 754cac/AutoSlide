import { apiUrl } from '../utils/api';
import { authHeader } from '../utils/auth';
import { buildPagedPath, normalizePageSize, readPaginationHeaders } from '../utils/pagination';
import { mapCourse, mapMaterial, mapReplay, mapSession } from '../utils/studentViewModels';

function safeJsonParse(value) {
  if (!value) return null;
  try {
    return JSON.parse(value);
  } catch (error) {
    return null;
  }
}

function mergeHeaders(extraHeaders = {}) {
  return {
    ...authHeader(),
    ...extraHeaders
  };
}

async function requestJson(path, options = {}) {
  const response = await fetch(apiUrl(path), {
    ...options,
    headers: mergeHeaders(options.headers)
  });

  const rawText = await response.text();
  let payload = null;

  if (rawText) {
    try {
      payload = JSON.parse(rawText);
    } catch (error) {
      payload = rawText;
    }
  }

  if (!response.ok) {
    const message = payload && typeof payload === 'object'
      ? payload.error || payload.message || response.statusText
      : response.statusText || 'Request failed';
    const error = new Error(message || 'Request failed');
    error.status = response.status;
    error.payload = payload;
    throw error;
  }

  return { response, payload };
}

function normalizeUser(user) {
  if (!user) return null;

  return {
    id: user.id || user.Id || user.userId || user.UserId || null,
    name: user.fullName || user.FullName || user.name || user.Name || user.email || user.Email || 'Student',
    fullName: user.fullName || user.FullName || user.name || user.Name || '',
    role: user.role || user.Role || 'Student',
    email: user.email || user.Email || '',
    avatarUrl: user.avatarUrl || user.AvatarUrl || user.photoUrl || user.PhotoUrl || ''
  };
}

function createCourseCover(courseCode, courseName) {
  const safeCode = courseCode || 'Course';
  const safeName = courseName || 'Course';
  const initials = safeName
    .split(' ')
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0])
    .join('')
    .toUpperCase();

  const svg = `
    <svg xmlns="http://www.w3.org/2000/svg" width="1200" height="720" viewBox="0 0 1200 720">
      <defs>
        <linearGradient id="g" x1="0" x2="1" y1="0" y2="1">
          <stop offset="0%" stop-color="#5f6d52" />
          <stop offset="100%" stop-color="#8b7a60" />
        </linearGradient>
        <filter id="shadow"><feDropShadow dx="0" dy="8" stdDeviation="18" flood-color="#0f172a" flood-opacity="0.25" /></filter>
      </defs>
      <rect width="1200" height="720" fill="url(#g)" />
      <circle cx="960" cy="120" r="180" fill="rgba(255,255,255,0.12)" />
      <circle cx="200" cy="610" r="260" fill="rgba(255,255,255,0.08)" />
      <g filter="url(#shadow)">
        <rect x="72" y="72" width="250" height="250" rx="36" fill="rgba(255,255,255,0.14)" stroke="rgba(255,255,255,0.2)" />
        <text x="197" y="228" text-anchor="middle" font-family="Inter, Arial, sans-serif" font-size="104" font-weight="800" fill="#ffffff">${initials}</text>
      </g>
      <text x="360" y="204" font-family="Inter, Arial, sans-serif" font-size="30" font-weight="700" fill="rgba(255,255,255,0.72)">${safeCode}</text>
      <text x="360" y="260" font-family="Inter, Arial, sans-serif" font-size="58" font-weight="800" fill="#ffffff">${safeName}</text>
    </svg>
  `;

  return `data:image/svg+xml;charset=UTF-8,${encodeURIComponent(svg.trim())}`;
}

async function fetchPagedItems(path, pageSize = 100) {
  const safeSize = normalizePageSize(pageSize, 100);
  let page = 1;
  let allItems = [];
  let totalCount = 0;

  while (true) {
    const response = await fetch(apiUrl(buildPagedPath(path, page, safeSize)), {
      headers: mergeHeaders()
    });

    const rawText = await response.text();
    let payload = [];

    if (rawText) {
      try {
        payload = JSON.parse(rawText);
      } catch (error) {
        payload = [];
      }
    }

    if (!response.ok) {
      throw new Error(response.statusText || 'Request failed');
    }

    const pagination = readPaginationHeaders(response, safeSize);
    const pageItems = Array.isArray(payload) ? payload : [];
    totalCount = pagination.total || pageItems.length;
    allItems = allItems.concat(pageItems);

    if (allItems.length >= totalCount || pageItems.length < safeSize) {
      break;
    }

    page += 1;
  }

  return { items: allItems, totalCount };
}

export async function getCurrentUser() {
  const cachedUser = normalizeUser(safeJsonParse(localStorage.getItem('user')));

  try {
    const { payload } = await requestJson('/api/auth/me');
    return normalizeUser(payload) || cachedUser;
  } catch (error) {
    return cachedUser;
  }
}

export async function getAnnotatedPdfDownload(sessionId) {
  const { payload } = await requestJson(`/api/sessions/${sessionId}/exports/annotated-pdf`);
  return payload;
}

export async function updatePassword({ currentPassword, newPassword, confirmPassword }) {
  const response = await fetch(apiUrl('/api/auth/change-password'), {
    method: 'POST',
    headers: mergeHeaders({
      'Content-Type': 'application/json'
    }),
    body: JSON.stringify({
      currentPassword,
      newPassword,
      confirmNewPassword: confirmPassword
    })
  });

  const payload = safeJsonParse(await response.text()) || {};

  if (!response.ok) {
    const error = new Error(payload.message || 'Unable to update password right now.');
    error.status = response.status;
    error.payload = payload;
    throw error;
  }

  return {
    message: payload.message || 'Password updated successfully. Your current session stays active.',
    sessionRemainsActive: payload.sessionRemainsActive !== false
  };
}

export async function getEnrolledCourses(options = {}) {
  const { all = true, page = 1, pageSize = 25 } = options;

  if (all) {
    const { items, totalCount } = await fetchPagedItems('/api/courses', 100);
    return {
      courses: items.map(mapCourse).filter((course) => course.id),
      totalCount
    };
  }

  const response = await fetch(apiUrl(buildPagedPath('/api/courses', page, pageSize)), {
    headers: mergeHeaders()
  });

  const rawText = await response.text();
  let payload = [];

  if (rawText) {
    try {
      payload = JSON.parse(rawText);
    } catch (error) {
      payload = [];
    }
  }

  if (!response.ok) {
    throw new Error(response.statusText || 'Unable to load courses.');
  }

  const pagination = readPaginationHeaders(response, pageSize);
  return {
    courses: (Array.isArray(payload) ? payload : []).map(mapCourse).filter((course) => course.id),
    totalCount: pagination.total,
    page,
    pageSize: normalizePageSize(pageSize, 25)
  };
}

export async function getCourseById(courseId) {
  const { courses } = await getEnrolledCourses({ all: true });
  return courses.find((course) => String(course.id) === String(courseId)) || null;
}

export async function getCourseSessions(courseId, options = {}) {
  const page = options.page || 1;
  const pageSize = options.pageSize || 25;

  const response = await fetch(apiUrl(buildPagedPath(`/api/courses/${courseId}/sessions`, page, pageSize)), {
    headers: mergeHeaders()
  });

  const rawText = await response.text();
  let payload = [];

  if (rawText) {
    try {
      payload = JSON.parse(rawText);
    } catch (error) {
      payload = [];
    }
  }

  if (!response.ok) {
    throw new Error(response.statusText || 'Unable to load sessions.');
  }

  const pagination = readPaginationHeaders(response, pageSize);
  return {
    sessions: (Array.isArray(payload) ? payload : []).map((item) => mapSession(item, options.courseName || '')),
    totalCount: pagination.total,
    page,
    pageSize: normalizePageSize(pageSize, 25)
  };
}

export async function getCourseHistory(courseId, options = {}) {
  const page = options.page || 1;
  const pageSize = options.pageSize || 25;

  const response = await fetch(apiUrl(buildPagedPath(`/api/courses/${courseId}/history`, page, pageSize)), {
    headers: mergeHeaders()
  });

  const rawText = await response.text();
  let payload = [];

  if (rawText) {
    try {
      payload = JSON.parse(rawText);
    } catch (error) {
      payload = [];
    }
  }

  if (!response.ok) {
    throw new Error(response.statusText || 'Unable to load replay history.');
  }

  const pagination = readPaginationHeaders(response, pageSize);
  return {
    sessions: (Array.isArray(payload) ? payload : []).map((item) => mapReplay(item, options.courseName || '')),
    totalCount: pagination.total,
    page,
    pageSize: normalizePageSize(pageSize, 25)
  };
}

export async function getCourseMaterials(courseId, options = {}) {
  const page = options.page || 1;
  const pageSize = options.pageSize || 25;

  const response = await fetch(apiUrl(buildPagedPath(`/api/courses/${courseId}/materials`, page, pageSize)), {
    headers: mergeHeaders()
  });

  const rawText = await response.text();
  let payload = [];

  if (rawText) {
    try {
      payload = JSON.parse(rawText);
    } catch (error) {
      payload = [];
    }
  }

  if (!response.ok) {
    throw new Error(response.statusText || 'Unable to load materials.');
  }

  const pagination = readPaginationHeaders(response, pageSize);
  return {
    materials: (Array.isArray(payload) ? payload : []).map(mapMaterial),
    totalCount: pagination.total,
    page,
    pageSize: normalizePageSize(pageSize, 25)
  };
}

export function getLiveSessions(courses = []) {
  return courses
    .filter((course) => Boolean(course.activeSessionId))
    .map((course) => ({
      ...course,
      courseCode: course.code || course.courseCode || '',
      courseName: course.name || course.courseName || 'Course',
      sessionId: course.activeSessionId,
      startedAt: course.activeSessionStartedAt || course.sessionStartedAt || course.startedAt || null,
      isLive: true
    }));
}

export async function getLatestReplay(courses = []) {
  const courseList = courses.length > 0 ? courses : (await getEnrolledCourses({ all: true })).courses;
  const replayCandidates = [];

  await Promise.all(
    courseList.map(async (course) => {
      try {
        const { sessions } = await getCourseHistory(course.id, {
          page: 1,
          pageSize: 1,
          courseName: course.name
        });
        const latest = sessions[0];
        if (!latest) return;

        replayCandidates.push({
          sessionId: latest.sessionId || latest.id,
          sessionTitle: latest.title,
          courseName: course.name,
          date: latest.startedAt,
          hasTranscript: Boolean(latest.hasTranscript),
          hasSummary: Boolean(latest.hasSummary),
          courseId: course.id
        });
      } catch (error) {
      }
    })
  );

  if (replayCandidates.length === 0) return null;

  replayCandidates.sort((left, right) => {
    const leftTime = left.date ? new Date(left.date).getTime() : 0;
    const rightTime = right.date ? new Date(right.date).getTime() : 0;
    return rightTime - leftTime;
  });

  return replayCandidates[0];
}

async function getCourseSummary(course) {
  const [historyResult, materialsResult] = await Promise.all([
    getCourseHistory(course.id, {
      page: 1,
      pageSize: 1,
      courseName: course.name
    }),
    getCourseMaterials(course.id, {
      page: 1,
      pageSize: 1
    })
  ]);

  const latestReplay = historyResult.sessions[0] || null;

  return {
    ...course,
    coverUrl: createCourseCover(course.code, course.name),
    materialsCount: materialsResult.totalCount || 0,
    lastSession: latestReplay?.startedAt || null,
    latestReplay
  };
}

export async function getDashboardData() {
  const [user, courseResult] = await Promise.all([
    getCurrentUser(),
    getEnrolledCourses({ all: true })
  ]);

  const courses = await Promise.all(courseResult.courses.map((course) => getCourseSummary(course)));
  const liveSessions = getLiveSessions(courses);
  const latestReplay = await getLatestReplay(courses);

  return {
    user,
    courses,
    liveSessions,
    latestReplay,
    totalCount: courseResult.totalCount
  };
}

export async function getCourseDetailData(courseId, options = {}) {
  const course = await getCourseById(courseId);
  const courseName = options.courseName || course?.name || '';

  const [sessions, history, materials] = await Promise.all([
    getCourseSessions(courseId, {
      page: options.sessionsPage || 1,
      pageSize: options.sessionsPageSize || 25,
      courseName
    }),
    getCourseHistory(courseId, {
      page: options.historyPage || 1,
      pageSize: options.historyPageSize || 25,
      courseName
    }),
    getCourseMaterials(courseId, {
      page: options.materialsPage || 1,
      pageSize: options.materialsPageSize || 25
    })
  ]);

  return {
    course,
    sessions,
    history,
    materials
  };
}
