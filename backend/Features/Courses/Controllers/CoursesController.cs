using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using System.Diagnostics;
using System.Security.Claims;
using BackendServer.Shared.Data;
using BackendServer.Shared.Data.Entities;
using BackendServer.Features.Courses.DTOs;

namespace BackendServer.Features.Courses.Controllers
{
    [Authorize]
    [Route("api/courses")]
    [ApiController]
    public class CoursesController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<CoursesController> _logger;

        public CoursesController(AppDbContext context, ILogger<CoursesController> logger)
        {
            _context = context;
            _logger = logger;
        }

        [HttpGet("my-courses")]
        [Authorize(Roles = "Student")]
        public async Task<IActionResult> GetStudentCourses([FromQuery] int? skip = null, [FromQuery] int? take = null)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var (safeSkip, safeTake) = NormalizePaging(skip, take);

            var query = _context.Enrollments
                .Where(e => e.StudentId == Guid.Parse(userId))
                .Join(_context.Courses, e => e.CourseId, c => c.Id, (e, c) => new { e, c })
                .Select(ec => new 
                {
                    ec.c.Id,
                    ec.c.Name,
                    ec.c.Code,
                    ec.c.Description,
                    ActiveSessionId = _context.Sessions
                        .Where(s => s.CourseId == ec.e.CourseId && s.EndedAt == null)
                        .OrderByDescending(s => s.CreatedAt)
                        .Select(s => (Guid?)s.Id)
                        .FirstOrDefault()
                });

            var totalCount = await query.CountAsync();
            var courses = await query
                .OrderBy(c => c.Name)
                .Skip(safeSkip)
                .Take(safeTake)
                .ToListAsync();

            AddPaginationHeaders(totalCount, safeSkip, safeTake);

            return Ok(courses);
        }

        [HttpGet("{courseId}/replays")]
        public async Task<IActionResult> GetCourseReplays(Guid courseId, [FromQuery] int? skip = null, [FromQuery] int? take = null)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);   
            var role = User.FindFirstValue(ClaimTypes.Role);
            if (userIdClaim == null) return Unauthorized();

            var userId = Guid.Parse(userIdClaim);
            var (safeSkip, safeTake) = NormalizePaging(skip, take);

            var course = await _context.Courses
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == courseId);

            if (course == null) return NotFound();

            if (role == "Teacher")
            {
                if (course.TeacherId != userId) return Forbid();
            }
            else if (role == "Student")
            {
                var isEnrolled = await _context.Enrollments
                    .AsNoTracking()
                    .AnyAsync(e => e.CourseId == courseId && e.StudentId == userId);

                if (!isEnrolled) return Forbid();
            }
            else
            {
                return Forbid();
            }

            var replayQuery = _context.Sessions
                .AsNoTracking()
                .Where(s => s.CourseId == courseId
                            && s.Status == SessionStatus.Ended
                            && s.TranscriptStoragePath != null)
                .OrderByDescending(s => s.StartedAt ?? s.CreatedAt);

            var totalCount = await replayQuery.CountAsync();

            var replays = await replayQuery
                .Skip(safeSkip)
                .Take(safeTake)
                .Select(s => new
                {
                    s.Id,
                    s.PresentationTitle,
                    s.StartedAt,
                    s.EndedAt,
                    s.SlideCount
                })
                .ToListAsync();

            AddPaginationHeaders(totalCount, safeSkip, safeTake);

            return Ok(replays);
        }

        [HttpGet("{courseId}/history")]
        public async Task<IActionResult> GetCourseHistory(Guid courseId, [FromQuery] int? skip = null, [FromQuery] int? take = null)
        {
            var endpointWatch = Stopwatch.StartNew();
            var dbWatch = Stopwatch.StartNew();

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var role = User.FindFirstValue(ClaimTypes.Role);
            if (userIdClaim == null) return Unauthorized();

            var userId = Guid.Parse(userIdClaim);
            var (safeSkip, safeTake) = NormalizePaging(skip, take);

            var course = await _context.Courses
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == courseId);

            var courseLookupMs = dbWatch.ElapsedMilliseconds;
            dbWatch.Restart();

            if (course == null) return NotFound();

            if (role == "Teacher")
            {
                if (course.TeacherId != userId) return Forbid();
            }
            else if (role == "Student")
            {
                var isEnrolled = await _context.Enrollments
                    .AsNoTracking()
                    .AnyAsync(e => e.CourseId == courseId && e.StudentId == userId);
                if (!isEnrolled) return Forbid();
            }
            else
            {
                return Forbid();
            }

            var authCheckMs = dbWatch.ElapsedMilliseconds;
            dbWatch.Restart();

            var query = _context.Sessions
                .AsNoTracking()
                .Where(s => s.CourseId == courseId);

            if (role == "Student")
            {
                query = query.Where(s => s.Status == SessionStatus.Ended && s.TranscriptStoragePath != null);
            }

            var totalCount = await query.CountAsync();

            var countMs = dbWatch.ElapsedMilliseconds;
            dbWatch.Restart();

            var result = await query
                .OrderByDescending(s => s.StartedAt ?? s.CreatedAt)
                .Skip(safeSkip)
                .Take(safeTake)
                .Select(s => new
                {
                    s.Id,
                    s.PresentationTitle,
                    s.StartedAt,
                    s.EndedAt,
                    s.SlideCount,
                    status = (int)s.Status,
                    hasTranscript = s.TranscriptStoragePath != null,
                    hasSummary = s.SummaryText != null,
                    durationSeconds = s.StartedAt.HasValue && s.EndedAt.HasValue
                        ? (s.EndedAt.Value - s.StartedAt.Value).TotalSeconds
                        : (double?)null
                })
                .ToListAsync();

            var pageMs = dbWatch.ElapsedMilliseconds;
            endpointWatch.Stop();

            _logger.LogInformation(
                "Course history timing courseId={CourseId} role={Role} skip={Skip} take={Take} totalCount={TotalCount} rows={RowCount} courseLookupMs={CourseLookupMs} authCheckMs={AuthCheckMs} countMs={CountMs} pageMs={PageMs} totalMs={TotalMs} transcriptSigningInList={TranscriptSigningInList}",
                courseId,
                role,
                safeSkip,
                safeTake,
                totalCount,
                result.Count,
                courseLookupMs,
                authCheckMs,
                countMs,
                pageMs,
                endpointWatch.ElapsedMilliseconds,
                false);

            AddPaginationHeaders(totalCount, safeSkip, safeTake);

            return Ok(result);
        }

        [Authorize]
        [HttpGet("{courseId}/sessions")]
        public async Task<IActionResult> GetCourseSessions(Guid courseId, [FromQuery] int? skip = null, [FromQuery] int? take = null)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var role = User.FindFirstValue(ClaimTypes.Role);
            if (userIdClaim == null) return Unauthorized();
            var userId = Guid.Parse(userIdClaim);
            var (safeSkip, safeTake) = NormalizePaging(skip, take);

            var course = await _context.Courses.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == courseId);
            if (course == null) return NotFound();

            if (role == "Teacher")
            {
                if (course.TeacherId != userId) return Forbid();
            }
            else if (role == "Student")
            {
                var isEnrolled = await _context.Enrollments.AsNoTracking()
                    .AnyAsync(e => e.CourseId == courseId
                               && e.StudentId == userId
                               && e.Status == EnrollmentStatus.Enrolled);
                if (!isEnrolled) return Forbid();
            }
            else
            {
                return Forbid();
            }

            var sessionsQuery = _context.Sessions
                .AsNoTracking()
                .Where(s => s.CourseId == courseId)
                .OrderByDescending(s => s.CreatedAt);

            var totalCount = await sessionsQuery.CountAsync();

            var sessions = await sessionsQuery
                .Skip(safeSkip)
                .Take(safeTake)
                .Select(s => new
                {
                    id = s.Id,
                    s.PresentationTitle,
                    status = (int)s.Status,
                    s.SlideCount,
                    s.TotalSlides,
                    s.CurrentSlideIndex,
                    s.AllowStudentDownload,
                    s.CreatedAt,
                    s.StartedAt,
                    s.EndedAt
                })
                .ToListAsync();

            AddPaginationHeaders(totalCount, safeSkip, safeTake);

            return Ok(sessions);
        }

        [HttpGet]
        public async Task<IActionResult> GetMyCourses([FromQuery] int? skip = null, [FromQuery] int? take = null)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var role = User.FindFirstValue(ClaimTypes.Role);

            if (userIdClaim == null) return Unauthorized();
            
            var userId = Guid.Parse(userIdClaim);
            var (safeSkip, safeTake) = NormalizePaging(skip, take);

            if (role == "Teacher")
            {
                var query = _context.Courses
                    .Where(c => c.TeacherId == userId)
                    .Select(c => new 
                    {
                        c.Id,
                        c.Code,
                        c.Name,
                        StudentCount = _context.Enrollments.Count(e => e.CourseId == c.Id)
                    });

                var totalCount = await query.CountAsync();
                var courses = await query
                    .OrderBy(c => c.Name)
                    .Skip(safeSkip)
                    .Take(safeTake)
                    .ToListAsync();

                AddPaginationHeaders(totalCount, safeSkip, safeTake);

                return Ok(courses);
            }
            else
            {
                return await GetStudentCourses(skip, take);
            }
        }

        [HttpGet("{id}/materials")]
        public async Task<IActionResult> GetCourseMaterials(Guid id, [FromQuery] int? skip = null, [FromQuery] int? take = null)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var role = User.FindFirstValue(ClaimTypes.Role);
            if (userIdClaim == null) return Unauthorized();
            var userId = Guid.Parse(userIdClaim);
            var (safeSkip, safeTake) = NormalizePaging(skip, take);

            var course = await _context.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (course == null) return NotFound();

            var isTeacher = string.Equals(role, "Teacher", StringComparison.OrdinalIgnoreCase);
            var isStudent = string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase);

            IQueryable<Material> query = _context.Materials.AsNoTracking().Where(m => m.CourseId == id);

            if (isTeacher)
            {
                if (course.TeacherId != userId) return Forbid();
            }
            else if (isStudent)
            {
                var isEnrolled = await _context.Enrollments
                    .AsNoTracking()
                    .AnyAsync(e => e.CourseId == id && e.StudentId == userId && e.Status == EnrollmentStatus.Enrolled);

                if (!isEnrolled) return Forbid();
                var now = DateTime.UtcNow;
                query = query.Where(m => m.IsVisible || (m.ReleaseAt != null && m.ReleaseAt <= now));
            }
            else
            {
                return Forbid();
            }

            var totalCount = await query.CountAsync();

            var materials = await query
                .OrderBy(m => m.Week)
                .ThenBy(m => m.ReleaseAt)
                .Skip(safeSkip)
                .Take(safeTake)
                .Select(m => new
                {
                    m.Id,
                    m.Title,
                    m.OriginalFileName,
                    m.StoragePath,
                    m.Week,
                    m.IsVisible,
                    m.ReleaseAt,
                    m.UploadedAt
                })
                .ToListAsync();

            var baseUrl = Request.Scheme + "://" + Request.Host.Value;
            var result = materials.Select(m => new
            {
                m.Id,
                m.Title,
                m.OriginalFileName,
                m.StoragePath,
                m.Week,
                m.IsVisible,
                m.ReleaseAt,
                m.UploadedAt,
                downloadUrl = baseUrl + "/api/materials/" + m.Id + "/url"
            });

            AddPaginationHeaders(totalCount, safeSkip, safeTake);

            return Ok(result);
        }

        [Authorize(Roles = "Teacher")]
        [HttpPost]
        public async Task<IActionResult> CreateCourse([FromBody] CreateCourseDto dto)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdClaim == null) return Unauthorized();
            var userId = Guid.Parse(userIdClaim);

            var course = new Course
            {
                TeacherId = userId,
                Code = dto.Code,
                Name = dto.Name
            };

            _context.Courses.Add(course);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetCourseDetails), new { id = course.Id }, course);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetCourseDetails(Guid id)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdClaim == null) return Unauthorized();
            var userId = Guid.Parse(userIdClaim);

            var course = await _context.Courses
                .FirstOrDefaultAsync(c => c.Id == id);

            if (course == null) return NotFound();
            if (course.TeacherId != userId) return Forbid();

            return Ok(course);
        }

        [Authorize(Roles = "Teacher")]
        [HttpGet("{id}/roster")]
        public async Task<IActionResult> GetRoster(Guid id, [FromQuery] int? skip = null, [FromQuery] int? take = null)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdClaim == null) return Unauthorized();
            var userId = Guid.Parse(userIdClaim);
            var (safeSkip, safeTake) = NormalizePaging(skip, take);

            var course = await _context.Courses
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id);

            if (course == null) return NotFound();
            if (course.TeacherId != userId) return Forbid();

            var rosterQuery = _context.Enrollments
                .Where(e => e.CourseId == id)
                .Join(_context.Users, e => e.StudentId, u => u.Id, (e, u) => new
                {
                    Email = u.Email,
                    Name = u.FullName,
                    Status = (int)e.Status,
                    IsRegistered = u.IsRegistered
                });

            var totalCount = await rosterQuery.CountAsync();

            var roster = await rosterQuery
                .OrderBy(r => r.Email)
                .Skip(safeSkip)
                .Take(safeTake)
                .ToListAsync();

            AddPaginationHeaders(totalCount, safeSkip, safeTake);

            return Ok(roster);
        }

        [Authorize(Roles = "Teacher")]
        [HttpPost("{id}/enroll")]
        public async Task<IActionResult> EnrollStudents(Guid id, [FromBody] EnrollDto dto)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdClaim == null) return Unauthorized();
            var userId = Guid.Parse(userIdClaim);

            var course = await _context.Courses.FindAsync(id);
            if (course == null) return NotFound();
            if (course.TeacherId != userId) return Forbid();

            var distinctEmails = dto.Emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim().ToLower()).Distinct().ToList();

            foreach (var email in distinctEmails)
            {
                if (!IsValidEmail(email)) continue;

                var student = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email);

                if (student == null)
                {
                    student = new User
                    {
                        Email = email,
                        Role = UserRole.Student,
                        FullName = "Pending Registration",
                        PasswordHash = "SHADOW_ACCOUNT",
                        IsRegistered = false
                    };
                    _context.Users.Add(student);
                    await _context.SaveChangesAsync(); 
                }

                var existingEnrollment = await _context.Enrollments
                    .FirstOrDefaultAsync(e => e.CourseId == id && e.StudentId == student.Id);

                if (existingEnrollment == null)
                {
                    var enrollment = new Enrollment
                    {
                        CourseId = id,
                        StudentId = student.Id,
                        Status = !student.IsRegistered ? EnrollmentStatus.Pending : EnrollmentStatus.Enrolled
                    };
                    _context.Enrollments.Add(enrollment);
                }
            }

            await _context.SaveChangesAsync();
            return Ok();
        }

        [Authorize(Roles = "Teacher")]
        [HttpDelete("{id}/enroll/{studentEmail}")]
        public async Task<IActionResult> RemoveStudent(Guid id, string studentEmail)
        {
             var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdClaim == null) return Unauthorized();
            var userId = Guid.Parse(userIdClaim);

            var course = await _context.Courses.FindAsync(id);
            if (course == null) return NotFound();
            if (course.TeacherId != userId) return Forbid();
            
            var student = await _context.Users.FirstOrDefaultAsync(u => u.Email == studentEmail);
            if (student == null) return NotFound("Student not found");

            var enrollment = await _context.Enrollments
                .FirstOrDefaultAsync(e => e.CourseId == id && e.StudentId == student.Id);
            
            if (enrollment != null)
            {
                _context.Enrollments.Remove(enrollment);
                await _context.SaveChangesAsync();
            }
            
            return Ok();
        }

        private bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        private static (int Skip, int Take) NormalizePaging(int? skip, int? take)
        {
            var safeSkip = Math.Max(0, skip ?? 0);
            var requestedTake = take ?? 25;
            var allowedTake = requestedTake switch
            {
                50 => 50,
                100 => 100,
                _ => 25
            };

            return (safeSkip, allowedTake);
        }

        private void AddPaginationHeaders(int totalCount, int skip, int take)
        {
            Response.Headers["X-Total-Count"] = totalCount.ToString();
            Response.Headers["X-Skip"] = skip.ToString();
            Response.Headers["X-Take"] = take.ToString();
        }
    }
}