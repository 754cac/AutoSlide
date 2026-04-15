import React from 'react';

const DEFAULT_TABS = [
  { key: 'overview', label: 'Overview' },
  { key: 'roster', label: 'Roster' },
  { key: 'history', label: 'History' },
  { key: 'materials', label: 'Materials' }
];

export default function CourseWorkspaceHeader({
  activeTab,
  tabs = DEFAULT_TABS,
  onTabChange,
  actions,
  contextLabel = 'Course Workspace',
  courseLabel = ''
}) {
  return (
    <div className="course-workspace-header">
      <div className="course-workspace-header__copy">
        <div className="course-workspace-header__eyebrow-wrap">
          <p className="course-workspace-header__eyebrow">{contextLabel}</p>
          {courseLabel ? <span className="course-workspace-header__course-label">{courseLabel}</span> : null}
        </div>

        <nav className="course-workspace-tabs" aria-label="Course sections">
          {tabs.map((tab) => {
            const isActive = activeTab === tab.key;
            return (
              <button
                key={tab.key}
                type="button"
                role="tab"
                aria-selected={isActive}
                className={`course-workspace-tab${isActive ? ' is-active' : ''}`}
                onClick={() => onTabChange(tab.key)}
              >
                {tab.label}
              </button>
            );
          })}
        </nav>
      </div>

      {actions ? <div className="course-workspace-header__actions">{actions}</div> : null}
    </div>
  );
}
