import React from 'react';

export default function FileUploadZone({
  file,
  pending = false,
  accept = '.pdf,.zip,.ppt,.pptx',
  onFileChange,
  wrapperClassName = 'course-quick-upload-card__dropzone',
  inputClassName,
  iconClassName = 'course-quick-upload-card__dropzone-icon',
  textClassName = 'course-quick-upload-card__dropzone-text',
  hintClassName = 'course-quick-upload-card__dropzone-hint',
  emptyLabel = 'Drop your file here or browse',
  hint = 'Maximum size 50MB',
  icon = <i className="fa-solid fa-upload" aria-hidden="true" />
}) {
  const handleChange = (event) => {
    const selectedFile = event.target.files?.[0] || null;
    if (onFileChange) {
      onFileChange(selectedFile);
    }
  };

  return (
    <label className={wrapperClassName}>
      <input
        className={inputClassName}
        type="file"
        accept={accept}
        onChange={handleChange}
        disabled={pending}
      />
      <span className={iconClassName} aria-hidden="true">
        {icon}
      </span>
      <span className={textClassName}>{file ? file.name : emptyLabel}</span>
      <span className={hintClassName}>{hint}</span>
    </label>
  );
}