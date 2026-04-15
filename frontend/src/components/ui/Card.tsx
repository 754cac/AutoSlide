import React from 'react';

export default function Card({
  children,
  className = '',
  interactive = false,
  ...rest
}) {
  const interactiveClass = interactive ? ' student-card--interactive' : '';
  return (
    <section className={`student-card${interactiveClass} ${className}`.trim()} {...rest}>
      {children}
    </section>
  );
}
