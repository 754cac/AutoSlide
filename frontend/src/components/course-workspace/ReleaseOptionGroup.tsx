import React from 'react';

export default function ReleaseOptionGroup({
  value = 'now',
  onChange,
  disabled = false,
  label = 'Release Option',
  name = 'materialReleaseMode',
}) {
  return (
    <fieldset className="course-quick-upload-card__release-group">
      <legend className="course-quick-upload-card__label">{label}</legend>
      <label className="course-quick-upload-card__radio">
        <input
          className="course-quick-upload-card__radio-input"
          style={{ flex: 'none' }}
          type="radio"
          name={name}
          value="now"
          checked={value === 'now'}
          onChange={() => onChange('now')}
          disabled={disabled}
        />
        <span className="course-quick-upload-card__radio-content">Release Now</span>
      </label>
      <label className="course-quick-upload-card__radio">
        <input
          className="course-quick-upload-card__radio-input"
          style={{ flex: 'none' }}
          type="radio"
          name={name}
          value="schedule"
          checked={value === 'schedule'}
          onChange={() => onChange('schedule')}
          disabled={disabled}
        />
        <span className="course-quick-upload-card__radio-content">Schedule Release</span>
      </label>
    </fieldset>
  );
}
