using Supabase;

namespace BackendServer.Shared.Services
{
    public interface IStorageService
    {
        Task<string> UploadAsync(Stream stream, string storagePath, string contentType);
        Task<string> GetSignedUrlAsync(string storagePath, int expirySeconds = 300);
        Task DeleteAsync(string storagePath);

        /// <summary>
        /// Returns a signed URL with a Content-Disposition download filename.
        /// Use for multi-bucket access (presentations, slides, uploads …).
        /// </summary>
        Task<string> GetSignedDownloadUrlAsync(string bucket, string path, string downloadFileName, int expirySeconds = 3600);

        /// <summary>
        /// Same as GetSignedDownloadUrlAsync but returns null instead of throwing when the
        /// object does not exist (e.g. inked files before the ink PR ships).
        /// </summary>
        Task<string?> GetSignedDownloadUrlIfExistsAsync(string bucket, string path, string downloadFileName, int expirySeconds = 3600);

        /// <summary>
        /// Upload to a specific bucket (e.g. "slides", "presentations").
        /// </summary>
        Task<string> UploadToBucketAsync(Stream stream, string bucket, string storagePath, string contentType);
    }

    public class StorageService : IStorageService
    {
        private readonly Client _supabase;
        private const string BucketName = "uploads";

        public StorageService(Client supabase)
        {
            _supabase = supabase;
        }

        public Task<string> UploadAsync(Stream stream, string storagePath, string contentType)
            => UploadToBucketAsync(stream, BucketName, storagePath, contentType);

        public async Task<string> UploadToBucketAsync(Stream stream, string bucket, string storagePath, string contentType)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();

            await _supabase.Storage
                .From(bucket)
                .Upload(bytes, storagePath, new Supabase.Storage.FileOptions
                {
                    ContentType = contentType,
                    Upsert = true
                });

            return storagePath;
        }

        public async Task<string> GetSignedUrlAsync(string storagePath, int expirySeconds = 300)
        {
            var url = await _supabase.Storage
                .From(BucketName)
                .CreateSignedUrl(storagePath, expirySeconds);

            return url;
        }

        public async Task DeleteAsync(string storagePath)
        {
            await _supabase.Storage
                .From(BucketName)
                .Remove(new List<string> { storagePath });
        }

        public async Task<string> GetSignedDownloadUrlAsync(
            string bucket, string path, string downloadFileName, int expirySeconds = 3600)
        {
            var signed = await _supabase.Storage
                .From(bucket)
                .CreateSignedUrl(path, expirySeconds);

            return $"{signed}&download={Uri.EscapeDataString(downloadFileName)}";
        }

        public async Task<string?> GetSignedDownloadUrlIfExistsAsync(
            string bucket, string path, string downloadFileName, int expirySeconds = 3600)
        {
            try { return await GetSignedDownloadUrlAsync(bucket, path, downloadFileName, expirySeconds); }
            catch { return null; }
        }
    }
}
