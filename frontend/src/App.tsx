import React from 'react';
import { BrowserRouter as Router, Routes, Route, Navigate, useLocation, useParams, useSearchParams } from 'react-router-dom';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import ForgotPasswordPage from './pages/ForgotPasswordPage';
import TeacherDashboard from './pages/TeacherDashboard';
import CourseDetailsPage from './pages/CourseDetailsPage';
import StudentDashboard from './pages/StudentDashboard';
import StudentCourseView from './pages/StudentCourseView';
import ProfilePage from './pages/ProfilePage';
import NotFoundPage from './pages/NotFoundPage';
import AccessDeniedPage from './pages/AccessDeniedPage';
import LiveSessionViewer from './LiveSessionViewer';
import PrivateRoute from './components/PrivateRoute';
import { isTokenValid } from './utils/auth';
import { buildLocationPath, buildLoginRedirectUrl } from './utils/authRedirect';

function StudentDashboardRedirect() {
  return <Navigate to="/dashboard" replace />;
}

function StudentCoursesRedirect() {
  return <Navigate to="/dashboard" replace />;
}

function StudentCourseRedirect() {
  const { courseId } = useParams();
  return <Navigate to={`/courses/${courseId}`} replace />;
}

function KeyedTeacherDashboardRoute() {
  const location = useLocation();

  return <TeacherDashboard key={location.pathname} />;
}

function KeyedTeacherCourseRoute() {
  const { courseId } = useParams();

  return <CourseDetailsPage key={courseId || 'teacher-course'} />;
}

function KeyedStudentCourseRoute() {
  const { courseId } = useParams();

  return <StudentCourseView key={courseId || 'student-course'} />;
}

function HomeRedirector() {
  const [searchParams] = useSearchParams();
  const sessionId = searchParams.get("sessionId");
  
  // Check if user is authenticated with valid token
  const token = localStorage.getItem('token');
  const userJson = localStorage.getItem('user');
  const isAuthenticated = token && isTokenValid(token);
  
  // Parse user role for dashboard redirect
  let userRole = null;
  if (isAuthenticated && userJson) {
    try {
      const user = JSON.parse(userJson);
      userRole = user?.role;
    } catch (e) {
      // Invalid user data
    }
  }

  // Handle session viewer redirect
  if (sessionId) {
    const viewerLocation = {
      pathname: `/viewer/${sessionId}`,
      search: `?sessionId=${encodeURIComponent(sessionId)}`,
      hash: ''
    };
    const viewerTarget = buildLocationPath(viewerLocation);
    if (isAuthenticated) {
      return <Navigate to={viewerTarget} replace />;
    } else {
      return <Navigate to={buildLoginRedirectUrl(viewerTarget)} state={{ from: viewerLocation }} replace />;
    }
  }

  // If authenticated, go to appropriate dashboard
  if (isAuthenticated && userRole) {
    if (userRole === 'Teacher') {
      return <Navigate to="/teacher/dashboard" replace />;
    } else {
      return <Navigate to="/dashboard" replace />;
    }
  }

  // Not authenticated - go to login
  return <Navigate to="/login" replace />;
}

export default function App() {
  return (
    <Router>
      <Routes>
        {/* 1. Public / Authentication Zone */}
        <Route path="/" element={<HomeRedirector />} />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />
        <Route path="/forgot-password" element={<ForgotPasswordPage />} />

        {/* 2. Teacher Portal */}
        <Route 
          path="/teacher/dashboard" 
          element={
            <PrivateRoute allowedRoles={["Teacher"]}>
              <KeyedTeacherDashboardRoute />
            </PrivateRoute>
          } 
        />
        <Route 
          path="/teacher/courses" 
          element={
            <PrivateRoute allowedRoles={["Teacher"]}>
              <KeyedTeacherDashboardRoute />
            </PrivateRoute>
          } 
        />
        <Route 
          path="/teacher/history" 
          element={
            <PrivateRoute allowedRoles={["Teacher"]}>
              <KeyedTeacherDashboardRoute />
            </PrivateRoute>
          } 
        />
        <Route 
          path="/teacher/courses/:courseId" 
          element={
            <PrivateRoute allowedRoles={["Teacher"]}>
              <KeyedTeacherCourseRoute />
            </PrivateRoute>
          } 
        />

        {/* 3. Student Portal */}
        <Route 
          path="/student/dashboard" 
          element={
            <PrivateRoute allowedRoles={["Student"]}>
              <StudentDashboardRedirect />
            </PrivateRoute>
          } 
        />
        <Route 
          path="/dashboard" 
          element={
            <PrivateRoute allowedRoles={["Student"]}>
              <StudentDashboard />
            </PrivateRoute>
          } 
        />
        <Route 
          path="/courses" 
          element={
            <PrivateRoute allowedRoles={["Student"]}>
              <StudentCoursesRedirect />
            </PrivateRoute>
          } 
        />
        <Route 
          path="/student/courses" 
          element={
            <PrivateRoute allowedRoles={["Student"]}>
              <StudentCoursesRedirect />
            </PrivateRoute>
          } 
        />
        <Route 
          path="/courses/:courseId" 
          element={
            <PrivateRoute allowedRoles={["Student"]}>
              <KeyedStudentCourseRoute />
            </PrivateRoute>
          } 
        />
        <Route 
          path="/student/courses/:courseId" 
          element={
            <PrivateRoute allowedRoles={["Student"]}>
              <StudentCourseRedirect />
            </PrivateRoute>
          } 
        />
        <Route 
          path="/viewer/:presentationId" 
          element={
            <PrivateRoute>
              <LiveSessionViewer />
            </PrivateRoute>
          } 
        />
        <Route 
          path="/replay/:sessionId" 
          element={
            <PrivateRoute>
              <LiveSessionViewer />
            </PrivateRoute>
          } 
        />

        {/* 4. Shared / Utility Routes */}
        <Route 
          path="/profile" 
          element={
            <PrivateRoute>
              <ProfilePage />
            </PrivateRoute>
          } 
        />
        <Route path="/403" element={<AccessDeniedPage />} />
        <Route path="/404" element={<NotFoundPage />} />
        <Route path="*" element={<Navigate to="/404" replace />} />
      </Routes>
    </Router>
  );
}
