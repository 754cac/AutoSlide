import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import AppShell from '../components/ui/AppShell';
import HeroBanner from '../components/student-dashboard/HeroBanner';
import LiveNowSection from '../components/student-dashboard/LiveNowSection';
import CourseListContainer from '../components/student-dashboard/CourseListContainer';
import useDocumentTitle from '../hooks/useDocumentTitle';
import { getDashboardData } from '../services/studentApi';
import { getActiveSessionId } from '../utils/studentViewModels';

export default function DashboardPage() {
  useDocumentTitle('Dashboard | AutoSlide');

  const navigate = useNavigate();

  const [dashboard, setDashboard] = useState({
    user: null,
    courses: [],
    liveSessions: [],
    totalCount: 0
  });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [reloadToken, setReloadToken] = useState(0);

  useEffect(() => {
    let mounted = true;

    const loadDashboard = async () => {
      setLoading(true);
      setError('');

      try {
        const nextDashboard = await getDashboardData();
        if (!mounted) return;
        setDashboard(nextDashboard);
      } catch (err) {
        if (!mounted) return;
        setDashboard({ user: null, courses: [], liveSessions: [], totalCount: 0 });
        setError(err.message || 'Unable to load your dashboard right now.');
      } finally {
        if (mounted) {
          setLoading(false);
        }
      }
    };

    loadDashboard();

    return () => {
      mounted = false;
    };
  }, [reloadToken]);

  const userName = dashboard.user?.name || dashboard.user?.fullName || 'Student';
  const liveSessions = dashboard.liveSessions || [];
  const enrolledCourses = dashboard.courses || [];
  const liveCourseIds = useMemo(
    () => new Set(liveSessions.map((course) => String(course.id || course.courseId))),
    [liveSessions]
  );
  const nonLiveCourses = useMemo(
    () => enrolledCourses.filter((course) => {
      const courseId = String(course.id || course.courseId);
      return !liveCourseIds.has(courseId) && !getActiveSessionId(course);
    }),
    [enrolledCourses, liveCourseIds]
  );
  const hasEnrolledCourses = enrolledCourses.length > 0;
  const emptyCourseTitle = hasEnrolledCourses ? 'No non-live courses right now' : 'No enrolled courses yet';
  const emptyCourseText = hasEnrolledCourses
    ? 'All of your enrolled courses are live right now. They will appear here once a session ends.'
    : 'Courses you join will appear here once they are available.';

  const refreshDashboard = () => {
    setReloadToken((value) => value + 1);
  };

  const heroSubtitle = useMemo(() => {
    if (error) {
      return 'We could not load your workspace. Try refreshing the page.';
    }

    if (loading) {
      return 'Loading your workspace...';
    }

    if (liveSessions.length > 0) {
      return `You have ${liveSessions.length} live session(s) happening now.`;
    }

    return 'No live sessions right now. Continue with your latest course.';
  }, [error, liveSessions.length, loading]);

  const heroFacts = useMemo(() => {
    if (loading || error) {
      return [];
    }

    return [
      { label: 'Courses enrolled', value: enrolledCourses.length },
      { label: 'Live sessions', value: liveSessions.length }
    ];
  }, [enrolledCourses.length, error, liveSessions.length, loading]);

  const openCourse = (course) => {
    const courseId = course?.id || course?.courseId;
    if (!courseId) return;
    navigate(`/courses/${courseId}`);
  };

  const enterLiveSession = (course) => {
    const sessionId = course?.activeSessionId || course?.sessionId;
    if (!sessionId) {
      openCourse(course);
      return;
    }

    navigate(`/viewer/${sessionId}`);
  };

  return (
    <AppShell sidebarProps={{}}>
      <HeroBanner userName={userName} subtitle={heroSubtitle} facts={heroFacts} />

      {!loading && liveSessions.length > 0 ? (
        <LiveNowSection
          title="Live Now"
          subtitle="Join the live session or open the course workspace."
          sessions={liveSessions}
          onOpenCourse={openCourse}
          onEnterLive={enterLiveSession}
        />
      ) : null}

      <CourseListContainer
        title="My Courses"
        subtitle="Your enrolled courses live in one workspace."
        courses={nonLiveCourses}
        emptyTitle={emptyCourseTitle}
        emptyText={emptyCourseText}
        loading={loading}
        error={error}
        onRetry={refreshDashboard}
        onOpenCourse={openCourse}
        onEnterLive={enterLiveSession}
      />
    </AppShell>
  );
}
