export function getId(value) {
  return value?.id || value?.Id || null;
}

export function getActiveSessionId(course) {
  return (
    course?.activeSessionId ||
    course?.ActiveSessionId ||
    course?.sessionId ||
    course?.SessionId ||
    null
  );
}

function getActiveSessionStartedAt(course) {
  return (
    course?.activeSessionStartedAt ||
    course?.ActiveSessionStartedAt ||
    course?.sessionStartedAt ||
    course?.SessionStartedAt ||
    course?.liveStartedAt ||
    course?.LiveStartedAt ||
    null
  );
}

export function mapCourse(raw) {
  const id = getId(raw);
  return {
    id,
    code: raw?.code || raw?.Code || '',
    name: raw?.name || raw?.title || raw?.Name || 'Untitled Course',
    instructor: raw?.teacherName || raw?.instructor || raw?.Instructor || '',
    description: raw?.description || raw?.Description || '',
    activeSessionId: getActiveSessionId(raw),
    activeSessionStartedAt: getActiveSessionStartedAt(raw)
  };
}

function mapSessionStatus(statusValue) {
  if (statusValue === 1 || String(statusValue).toLowerCase() === 'active') return 'live';
  if (statusValue === 2 || String(statusValue).toLowerCase() === 'ended') return 'replay';
  return 'upcoming';
}

export function mapSession(raw, courseName = '') {
  return {
    id: getId(raw),
    title: raw?.presentationTitle || raw?.title || 'Session',
    startedAt: raw?.startedAt || raw?.StartedAt || raw?.createdAt || raw?.CreatedAt || null,
    status: mapSessionStatus(raw?.status ?? raw?.Status),
    courseName
  };
}

export function mapMaterial(raw) {
  const releaseAt = raw?.releaseAt || raw?.ReleaseAt || null;
  const uploadedAt = raw?.uploadedAt || raw?.UploadedAt || null;
  const isVisible = Boolean(raw?.isVisible ?? raw?.IsVisible);
  const now = Date.now();
  const releaseTime = releaseAt ? new Date(releaseAt).getTime() : null;

  let status = 'locked';
  let canDownload = false;

  if (isVisible || (releaseTime && releaseTime <= now)) {
    status = 'released';
    canDownload = true;
  } else if (releaseTime && releaseTime > now) {
    status = 'upcoming';
  }

  return {
    id: getId(raw),
    title: raw?.title || raw?.Title || 'Material',
    fileName: raw?.originalFileName || raw?.OriginalFileName || 'Untitled file',
    week: Number(raw?.week ?? raw?.Week ?? 0),
    releaseAt,
    uploadedAt,
    downloadUrl: raw?.downloadUrl || raw?.DownloadUrl || '',
    status,
    canDownload
  };
}

export function mapReplay(raw, courseName = '') {
  const hasTranscript = Boolean(
    raw?.hasTranscript ||
    raw?.HasTranscript ||
    raw?.transcriptUrl ||
    raw?.TranscriptUrl
  );
  const hasSummary = Boolean(
    raw?.hasSummary ||
    raw?.HasSummary ||
    raw?.summaryText ||
    raw?.SummaryText
  );

  return {
    sessionId: raw?.sessionId || raw?.SessionId || getId(raw),
    title: raw?.presentationTitle || raw?.title || 'Session replay',
    startedAt: raw?.startedAt || raw?.StartedAt || null,
    durationSeconds: raw?.durationSeconds || raw?.DurationSeconds || null,
    transcriptUrl: raw?.transcriptUrl || raw?.TranscriptUrl || null,
    hasTranscript,
    hasSummary,
    courseName
  };
}
