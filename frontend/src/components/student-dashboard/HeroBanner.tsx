import React from 'react';

export default function HeroBanner({ userName, subtitle, facts = [] }) {
  const resolvedFacts = Array.isArray(facts)
    ? facts.filter((fact) => fact && fact.label && fact.value !== undefined && fact.value !== null)
    : [];

  return (
    <section className="dashboard-hero">
      <div className="dashboard-hero__copy">
        <p className="dashboard-hero__eyebrow">Student workspace</p>
        <h1 className="dashboard-hero__title">Welcome back, {userName}.</h1>
        <p className="dashboard-hero__subtitle">{subtitle}</p>
      </div>

      {resolvedFacts.length > 0 ? (
        <dl className="dashboard-hero__facts">
          {resolvedFacts.map((fact) => (
            <div key={fact.label} className="dashboard-hero__fact">
              <dt className="dashboard-hero__fact-label">{fact.label}</dt>
              <dd className="dashboard-hero__fact-value">{fact.value}</dd>
            </div>
          ))}
        </dl>
      ) : null}
    </section>
  );
}
