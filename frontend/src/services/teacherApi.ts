import { apiUrl } from '../utils/api';
import { authHeader } from '../utils/auth';
import { buildPagedPath, readPaginationHeaders } from '../utils/pagination';
import { getCurrentUser, getCourseHistory, getCourseMaterials, getCourseSessions } from './studentApi';

function mergeHeaders(extraHeaders = {}) {
  return {
    ...authHeader(),
    ...extraHeaders
  };
}

function safeJsonParse(text) {
  if (!text) return null;

  try {
    return JSON.parse(text);
  } catch (error) {
    return null;
  }
}

async function requestJson(path) {
  const response = await fetch(apiUrl(path), {
    headers: mergeHeaders()
  });

  const payload = safeJsonParse(await response.text());

  if (!response.ok) {
    const message = payload && typeof payload === 'object'
      ? payload.error || payload.message || response.statusText
      : response.statusText || 'Request failed';
    throw new Error(message || 'Request failed');
  }

  return { response, payload };
}

async function fetchAllTeacherCourses() {
  const safePageSize = 100;
  let page = 1;
  let allCourses = [];
  let totalCount = 0;

  while (true) {
    const { response, payload } = await requestJson(buildPagedPath('/api/courses', page, safePageSize));
    const nextPageItems = Array.isArray(payload) ? payload : [];
    const pagination = readPaginationHeaders(response, safePageSize);

    totalCount = pagination.total || nextPageItems.length;
    allCourses = allCourses.concat(nextPageItems);

    if (allCourses.length >= totalCount || nextPageItems.length < safePageSize) {
      break;
    }

    page += 1;
  }

  return allCourses.map((course) => ({
    id: course.Id || course.id,
    code: course.Code || course.code || '',
    name: course.Name || course.name || 'Untitled Course',
    studentCount: Number(course.StudentCount || course.studentCount || 0)
  }));
}

function getCourseStatus(course) {
  if (course.activeSessionId) return 'Live';
  if (course.materialCount > 0 || course.studentCount > 0 || course.latestSessionTitle) return 'Ready';
  return 'Draft';
}

function getStatusTone(status) {
  if (status === 'Live') return 'live';
  if (status === 'Ready') return 'released';
  return 'locked';
}

export async function getTeacherDashboardData() {
  const [user, baseCourses] = await Promise.all([
    getCurrentUser(),
    fetchAllTeacherCourses()
  ]);

  const courseSummaries = await Promise.all(baseCourses.map(async (course) => {
    const [sessionsResult, historyResult, materialsResult] = await Promise.all([
      getCourseSessions(course.id, { page: 1, pageSize: 25, courseName: course.name }),
      getCourseHistory(course.id, { page: 1, pageSize: 25, courseName: course.name }),
      getCourseMaterials(course.id, { page: 1, pageSize: 1 })
    ]);

    const activeSession = (sessionsResult.sessions || []).find((session) => session.status === 'live') || null;
    const latestSession = (historyResult.sessions || [])[0] || null;
    const latestSessionId = latestSession?.sessionId || latestSession?.id || null;
    const materialCount = Number(materialsResult.totalCount || 0);
    const status = getCourseStatus({
      activeSessionId: activeSession?.id,
      studentCount: course.studentCount,
      materialCount,
      latestSessionTitle: latestSession?.title
    });

    return {
      ...course,
      studentCount: course.studentCount,
      materialCount,
      activeSessionId: activeSession?.id || null,
      activeSessionTitle: activeSession?.title || '',
      latestSessionTitle: latestSession?.title || '',
      latestSessionId,
      latestSessionAt: latestSession?.startedAt || null,
      status,
      statusTone: getStatusTone(status)
    };
  }));

  courseSummaries.sort((left, right) => left.name.localeCompare(right.name));

  const stats = courseSummaries.reduce((accumulator, course) => {
    accumulator.courses += 1;
    accumulator.students += course.studentCount;
    accumulator.materials += course.materialCount;
    if (course.activeSessionId) accumulator.liveSessions += 1;
    if (course.status === 'Draft') accumulator.drafts += 1;
    return accumulator;
  }, {
    courses: 0,
    students: 0,
    materials: 0,
    liveSessions: 0,
    drafts: 0
  });

  const recentSessions = courseSummaries
    .filter((course) => Boolean(course.latestSessionAt))
    .map((course) => ({
      sessionId: course.latestSessionId || null,
      title: course.latestSessionTitle || 'Session history',
      courseName: course.name,
      courseCode: course.code,
      startedAt: course.latestSessionAt,
      status: 'Replay',
      statusTone: 'released',
      courseId: course.id
    }))
    .sort((left, right) => {
      const leftTime = left.startedAt ? new Date(left.startedAt).getTime() : 0;
      const rightTime = right.startedAt ? new Date(right.startedAt).getTime() : 0;
      return rightTime - leftTime;
    })
    .slice(0, 6);

  return {
    user,
    courses: courseSummaries,
    recentSessions,
    stats,
    totalCount: courseSummaries.length
  };
}
