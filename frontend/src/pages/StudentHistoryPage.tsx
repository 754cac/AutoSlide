import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import useSecureDownload from '../hooks/useSecureDownload';
import AppShell from '../components/ui/AppShell';
import Card from '../components/ui/Card';
import SectionHeader from '../components/ui/SectionHeader';
import PaginationControls from '../components/ui/PaginationControls';
import ReplayRow from '../components/student/ReplayRow';
import { authHeader } from '../utils/auth';
import { apiUrl } from '../utils/api';
import { buildPagedPath, clampPage, getPaginationState, readPaginationHeaders } from '../utils/pagination';
import { mapCourse, mapReplay } from '../utils/studentViewModels';

export default function StudentHistoryPage() {
  const navigate = useNavigate();
  const { downloadFile } = useSecureDownload();
  const [rows, setRows] = useState([]);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [totalCount, setTotalCount] = useState(0);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    let mounted = true;

    const fetchList = async (path, headers) => {
      try {
        const response = await fetch(apiUrl(path), { headers });
        if (!response.ok) return { rows: [], total: 0 };
        const pagination = readPaginationHeaders(response, pageSize);
        return {
          rows: await response.json(),
          total: pagination.total
        };
      } catch (err) {
        return { rows: [], total: 0 };
      }
    };

    const loadHistory = async () => {
      setIsLoading(true);
      setError('');

      try {
        const headers = authHeader();
        const coursesResponse = await fetch(apiUrl(buildPagedPath('/api/courses', 1, 100)), { headers });

        if (!coursesResponse.ok) {
          throw new Error('Unable to load replay history.');
        }

        const courseRows = (await coursesResponse.json()).map(mapCourse).filter((course) => course.id);

        const historyRows = await Promise.all(
          courseRows.map(async (course) => {
            const history = await fetchList(buildPagedPath(`/api/courses/${course.id}/history`, page, pageSize), headers);
            return {
              rows: history.rows.map((item) => mapReplay(item, course.name)),
              total: history.total
            };
          })
        );

        if (!mounted) return;

        setRows(historyRows.flatMap((row) => row.rows));
        setTotalCount(historyRows.reduce((sum, row) => sum + row.total, 0));
      } catch (err) {
        if (mounted) {
          setError(err.message || 'Unable to load replay history.');
        }
      } finally {
        if (mounted) {
          setIsLoading(false);
        }
      }
    };

    loadHistory();

    return () => {
      mounted = false;
    };
  }, [page, pageSize]);

  const sortedRows = useMemo(() => {
    return [...rows].sort((a, b) => {
      const aTime = a.startedAt ? new Date(a.startedAt).getTime() : 0;
      const bTime = b.startedAt ? new Date(b.startedAt).getTime() : 0;
      return bTime - aTime;
    });
  }, [rows]);

  const pagination = useMemo(() => getPaginationState(totalCount, page, pageSize), [totalCount, page, pageSize]);

  useEffect(() => {
    setPage((currentPage) => clampPage(currentPage, pagination.totalPages));
  }, [pagination.totalPages]);

  const onWatch = (replay) => {
    if (!replay.sessionId) return;
    navigate(`/viewer/${replay.sessionId}`);
  };

  const onDownload = (replay) => {
    if (!replay.sessionId) return;
    downloadFile(
      replay.sessionId,
      `${replay.title || 'session'}.pdf`,
      apiUrl(`/api/sessions/${replay.sessionId}/download?format=pdf_ink`)
    );
  };

  return (
    <AppShell title="History" subtitle="Replay completed sessions and continue learning.">
      <section className="student-section">
        <SectionHeader title="Replay History" subtitle="Sessions available for review." />

        {error ? (
          <Card>
            <p className="student-error-text">{error}</p>
          </Card>
        ) : null}

        {isLoading ? (
          <Card>
            <p className="student-muted-text">Loading replay sessions...</p>
          </Card>
        ) : null}

        {!isLoading && sortedRows.length === 0 ? (
          <Card>
            <h3 className="student-empty-title">No replay sessions yet</h3>
            <p className="student-muted-text">Completed sessions will appear here once available.</p>
          </Card>
        ) : null}

        {!isLoading && sortedRows.length > 0 ? (
          <Card>
            <div className="student-list-wrap">
              {sortedRows.map((replay) => (
                <ReplayRow
                  key={`${replay.sessionId}-${replay.startedAt || 'history-row'}`}
                  replay={replay}
                  onWatch={onWatch}
                  onDownload={onDownload}
                />
              ))}
            </div>
          </Card>
        ) : null}

        <PaginationControls
          page={pagination.currentPage}
          pageSize={pagination.pageSize}
          totalCount={pagination.totalItems}
          itemCount={sortedRows.length}
          onPageChange={(nextPage) => setPage(clampPage(nextPage, pagination.totalPages))}
          onPageSizeChange={(nextSize) => {
            setPage(1);
            setPageSize(nextSize);
          }}
        />
      </section>
    </AppShell>
  );
}
