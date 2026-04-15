import React from 'react';
import Card from '../ui/Card';

function buildChips({ liveCount, materialsCount, sessionsCount, replaysCount }) {
  const chips = [];

  if (liveCount > 0) chips.push({ label: 'Live Now', tone: 'live' });
  if (materialsCount > 0) chips.push({ label: `${materialsCount} Materials`, tone: 'neutral' });
  if (sessionsCount > 0) chips.push({ label: `${sessionsCount} Sessions`, tone: 'neutral' });
  if (replaysCount > 0) chips.push({ label: `${replaysCount} Replays`, tone: 'neutral' });

  return chips;
}

export default function CourseHero({
  course,
  instructor,
  schedule,
  location,
  liveCount = 0,
  materialsCount = 0,
  sessionsCount = 0,
  replaysCount = 0
}) {
  const chips = buildChips({ liveCount, materialsCount, sessionsCount, replaysCount });

  return (
    <Card className="course-hero-card course-hero-card--detail">
      <div className="course-hero-card__main">
        <p className="course-hero-card__code">{course?.code || 'Course'}</p>
        <h2 className="course-hero-card__name">{course?.name || 'Course'}</h2>
        <p className="course-hero-card__meta">{instructor || course?.instructor || 'Instructor information will appear here.'}</p>

        {schedule || location ? (
          <p className="course-hero-card__submeta">
            {schedule ? schedule : null}
            {schedule && location ? ' · ' : null}
            {location ? location : null}
          </p>
        ) : null}
      </div>

      {chips.length > 0 ? (
        <div className="course-hero-card__stats" aria-label="Course metrics">
          {chips.map((chip) => (
            <span
              key={chip.label}
              className={`course-hero-card__stat${chip.tone === 'live' ? ' course-hero-card__stat--live' : ''}`}
            >
              {chip.label}
            </span>
          ))}
        </div>
      ) : null}
    </Card>
  );
}
