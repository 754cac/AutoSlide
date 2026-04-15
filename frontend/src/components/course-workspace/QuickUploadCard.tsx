import React from 'react';
import MaterialUploadForm from './MaterialUploadForm';

export default function QuickUploadCard({
  materialTitle,
  materialReleaseMode,
  materialReleaseAt,
  materialFile,
  pending = false,
  nextReleaseLabel = '',
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
      heading="Quick Upload"
      subtitle="Add a material now or schedule its release for later."
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
      buttonLabel="Submit & Continue"
      note={nextReleaseLabel ? `Next scheduled release: ${nextReleaseLabel}` : undefined}
      uploadError={uploadError}
      maxUploadSizeLabel={maxUploadSizeLabel}
      releaseOptionName="overviewUploadReleaseMode"
    />
  );
}
