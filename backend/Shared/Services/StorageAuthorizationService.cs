using Microsoft.EntityFrameworkCore;
using BackendServer.Shared.Data;
using BackendServer.Shared.Data.Entities;

namespace BackendServer.Shared.Services
{
    public class StorageAuthorizationService
    {
        private readonly AppDbContext _db;

        public StorageAuthorizationService(AppDbContext db)
        {
            _db = db;
        }

        public async Task<bool> CanAccessMaterialAsync(
            Guid userId,
            UserRole role,
            Guid materialId,
            CancellationToken ct)
        {
            var material = await _db.Materials
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == materialId, ct);

            if (material == null) return false;

            if (role == UserRole.Teacher)
            {
                var course = await _db.Courses
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == material.CourseId, ct);

                return course != null && course.TeacherId == userId;
            }

            if (role == UserRole.Student)
            {
                var isEnrolled = await _db.Enrollments
                    .AsNoTracking()
                    .AnyAsync(e => e.CourseId == material.CourseId
                                && e.StudentId == userId
                                && e.Status == EnrollmentStatus.Enrolled, ct);

                if (!isEnrolled) return false;

                return material.IsVisible
                    || (material.ReleaseAt != null && material.ReleaseAt <= DateTime.UtcNow);
            }

            return false;
        }
    }
}
