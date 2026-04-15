import React from 'react';
import Card from '../ui/Card';
import SectionHeader from '../ui/SectionHeader';

export default function StudentSectionCard({
  title,
  subtitle,
  children,
  footer = null,
  className = ''
}) {
  return (
    <section className={`student-section course-detail-panel ${className}`.trim()}>
      <Card className="course-detail-panel__card">
        <SectionHeader title={title} subtitle={subtitle} />
        <div className="course-detail-panel__content">{children}</div>
        {footer}
      </Card>
    </section>
  );
}