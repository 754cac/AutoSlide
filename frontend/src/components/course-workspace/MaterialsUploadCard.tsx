import React from 'react';
import MaterialUploadForm from './MaterialUploadForm';

export default function MaterialsUploadCard({
  materialTitle,
  materialReleaseMode,
  materialReleaseAt,
  materialFile,
  pending = false,
  uploadError = '',
  maxUploadSizeLabel = '5 MB',
  onMaterialTitleChange,
  onMaterialReleaseModeChange,
  onMaterialReleaseAtChange,
  onMaterialFileChange,
  onSubmit
}) {
  return (
    <MaterialUploadForm
      heading="Upload Material"
      subtitle="Add slides, handouts, or resources for this course."
      materialTitle={materialTitle}
      materialReleaseMode={materialReleaseMode}
      materialReleaseAt={materialReleaseAt}
      materialFile={materialFile}
      pending={pending}
      onMaterialTitleChange={onMaterialTitleChange}
      onMaterialReleaseModeChange={onMaterialReleaseModeChange}
      onMaterialReleaseAtChange={onMaterialReleaseAtChange}
      onMaterialFileChange={onMaterialFileChange}
      onSubmit={onSubmit}
      buttonLabel="Upload Material"
      uploadError={uploadError}
      maxUploadSizeLabel={maxUploadSizeLabel}
      releaseOptionName="materialsUploadReleaseMode"
    />
  );
}
