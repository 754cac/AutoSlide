import React from 'react';
import Card from '../ui/Card';

export default function OverviewStatCard({ icon, value, label, detail, badge, tone = 'blue', loading = false }) {
  if (loading) {
    return (
      <Card className={`overview-stat-card overview-stat-card--${tone} overview-stat-card--loading`}>
        <div className="overview-stat-card__skeleton overview-stat-card__skeleton--icon" />
        <div className="overview-stat-card__skeleton overview-stat-card__skeleton--value" />
        <div className="overview-stat-card__skeleton overview-stat-card__skeleton--label" />
      </Card>
    );
  }

  return (
    <Card className={`overview-stat-card overview-stat-card--${tone}`}>
      <div className="overview-stat-card__top">
        <div className="overview-stat-card__icon" aria-hidden="true">
          {icon}
        </div>
        {badge ? <span className="overview-stat-card__badge">{badge}</span> : null}
      </div>
      <div className="overview-stat-card__value">{value}</div>
      <div className="overview-stat-card__label">{label}</div>
      {detail ? <div className="overview-stat-card__detail">{detail}</div> : null}
    </Card>
  );
}
