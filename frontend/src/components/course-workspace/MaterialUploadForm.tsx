import React from 'react';
import Card from '../ui/Card';
import FileUploadDropzone from './FileUploadDropzone';
import ReleaseOptionGroup from './ReleaseOptionGroup';

function stripExtension(fileName) {
  return fileName.replace(/\.[^/.]+$/, '');
}

function getDatePart(dateTimeValue) {
  return dateTimeValue?.split('T')?.[0] || '';
}

function getTimePart(dateTimeValue) {
  return dateTimeValue?.split('T')?.[1] || '';
}

export default function MaterialUploadForm({
  heading = 'Upload Material',
  subtitle = 'Add slides, handouts, or resources for this course.',
  materialTitle,
  materialReleaseMode,
  materialReleaseAt,
  materialFile,
  pending = false,
  onMaterialTitleChange,
  onMaterialReleaseModeChange,
  onMaterialReleaseAtChange,
  onMaterialFileChange,
  onSubmit,
  buttonLabel = 'Upload Material',
  note,
  releaseOptionName,
  uploadError = '',
  maxUploadSizeLabel = '5 MB',
}) {
  const handleFileChange = (file) => {
    if (file && !materialTitle?.trim()) {
      onMaterialTitleChange(stripExtension(file.name));
    }

    onMaterialFileChange(file);
  };

  return (
    <Card className="course-quick-upload-card course-material-upload-card">
      <div className="course-quick-upload-card__header">
        <div>
          <p className="course-quick-upload-card__eyebrow">{heading}</p>
          <p className="course-quick-upload-card__subtitle">{subtitle}</p>
        </div>
      </div>

      <form className="course-quick-upload-card__form" onSubmit={onSubmit}>
        <label className="course-quick-upload-card__field">
          <span className="course-quick-upload-card__label">Material Title</span>
          <input
            className="course-quick-upload-card__input"
            placeholder="e.g. Lab 3 Resources"
            value={materialTitle}
            onChange={(event) => onMaterialTitleChange(event.target.value)}
            type="text"
            disabled={pending}
          />
        </label>

        <ReleaseOptionGroup
          value={materialReleaseMode}
          onChange={onMaterialReleaseModeChange}
          disabled={pending}
          name={releaseOptionName}
        />

        {materialReleaseMode === 'schedule' ? (
          <div className="course-quick-upload-card__schedule-grid">
            <label className="course-quick-upload-card__field">
              <span className="course-quick-upload-card__label course-quick-upload-card__label--compact">Date</span>
              <input
                className="course-quick-upload-card__input"
                type="date"
                value={getDatePart(materialReleaseAt)}
                onChange={(event) => onMaterialReleaseAtChange(`${event.target.value}T${getTimePart(materialReleaseAt) || '00:00'}`)}
                disabled={pending}
              />
            </label>
            <label className="course-quick-upload-card__field">
              <span className="course-quick-upload-card__label course-quick-upload-card__label--compact">Time</span>
              <input
                className="course-quick-upload-card__input"
                type="time"
                value={getTimePart(materialReleaseAt)}
                onChange={(event) => onMaterialReleaseAtChange(`${getDatePart(materialReleaseAt) || new Date().toISOString().slice(0, 10)}T${event.target.value}`)}
                disabled={pending}
              />
            </label>
          </div>
        ) : null}

        <FileUploadDropzone
          file={materialFile}
          pending={pending}
          accept=".pdf,.docx,.ppt,.pptx,.zip"
          emptyLabel="Drop your file here or browse"
          hint={`Maximum size ${maxUploadSizeLabel}`}
          onFileChange={handleFileChange}
        />

        {uploadError ? <p className="course-quick-upload-card__error">{uploadError}</p> : null}

        <button type="submit" className="btn btn-primary course-quick-upload-card__submit" disabled={pending}>
          {pending ? 'Uploading...' : buttonLabel}
        </button>

        {(note || materialReleaseMode === 'schedule') ? (
          <p className="course-material-upload-card__note">
            {note || 'Files scheduled in the future remain hidden until the release time.'}
          </p>
        ) : null}
      </form>
    </Card>
  );
}
