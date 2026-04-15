import React from 'react';

export default function SectionHeader({ title, subtitle, actionLabel, onAction }) {
  return (
    <div className="section-header">
      <div>
        <h2 className="section-header__title">{title}</h2>
        {subtitle ? <p className="section-header__subtitle">{subtitle}</p> : null}
      </div>
      {actionLabel && onAction ? (
        <button type="button" className="btn btn-outline" onClick={onAction}>
          {actionLabel}
        </button>
      ) : null}
    </div>
  );
}
