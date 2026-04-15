import React from 'react';

export default function TabNav({ tabs, activeTab, onChange }) {
  return (
    <div className="student-tabs" role="tablist" aria-label="Course sections">
      {tabs.map((item) => (
        <button
          key={item.id}
          type="button"
          role="tab"
          aria-selected={activeTab === item.id}
          className={`student-tab${activeTab === item.id ? ' is-active' : ''}`}
          onClick={() => onChange(item.id)}
        >
          {item.label}
          <span className="student-tab__count">{item.count ?? 0}</span>
        </button>
      ))}
    </div>
  );
}
