using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BackendServer.Shared.Data;
using BackendServer.Shared.Data.Entities;
using BackendServer.Features.Auth.DTOs;
using BackendServer.Features.Auth.Services;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authorization;

namespace BackendServer.Features.Auth.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly PasswordService _passwordService;
        private readonly IConfiguration _configuration;

        public AuthController(AppDbContext context, PasswordService passwordService, IConfiguration configuration)
        {
            _context = context;
            _passwordService = passwordService;
            _configuration = configuration;
        }

        [HttpPost("fix-duplicates")]
        public async Task<IActionResult> FixDuplicates([FromQuery] string email)
        {
            var normalizedEmail = email.Trim().ToLower();
            var allUsers = await _context.Users.ToListAsync();
            var users = allUsers.Where(u => u.Email.Trim().ToLower() == normalizedEmail).ToList();

            if (users.Count < 2) 
                return Ok(new { message = "No duplicates found for " + normalizedEmail });

            var realUser = users.FirstOrDefault(u => u.IsRegistered);
            var shadowUser = users.FirstOrDefault(u => !u.IsRegistered);

            if (realUser == null || shadowUser == null)
                return BadRequest($"Found {users.Count} records but couldn't distinguish Real vs Shadow.");

            var enrollments = await _context.Enrollments
                .Where(e => e.StudentId == shadowUser.Id)
                .ToListAsync();

            foreach (var enrollment in enrollments)
            {
                var alreadyEnrolled = await _context.Enrollments
                    .AnyAsync(e => e.StudentId == realUser.Id && e.CourseId == enrollment.CourseId);

                if (!alreadyEnrolled)
                {
                    enrollment.StudentId = realUser.Id;
                }
                else
                {
                    _context.Enrollments.Remove(enrollment);
                }
            }

            _context.Users.Remove(shadowUser);
            await _context.SaveChangesAsync();

            return Ok(new { message = $"Fixed! Moved {enrollments.Count} enrollments to active account." });
        }

        [HttpPost("register")]
        public async Task<ActionResult<AuthResponseDto>> Register(RegisterDto request)
        {
            var normalizedEmail = request.Email.Trim().ToLower();
            var existingUser = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

            if (existingUser != null)
            {
                if (existingUser.IsRegistered)
                {
                    return BadRequest("Email already exists.");
                }

                existingUser.PasswordHash = _passwordService.HashPassword(request.Password);
                existingUser.FullName = request.FullName;
                existingUser.IsRegistered = true;
                existingUser.Role = request.Role;

                _context.Users.Update(existingUser);
                await _context.SaveChangesAsync();

                var activatedB = await ActivatePendingEnrollmentsAsync(existingUser.Id);

                if (activatedB > 0)
                    Console.WriteLine($"[Register] Activated {activatedB} pending enrollment(s) for {existingUser.Email}");

                var token = CreateToken(existingUser);

                return Ok(new AuthResponseDto
                {
                    Token = token,
                    FullName = existingUser.FullName,
                    Role = existingUser.Role.ToString()
                });
            }

            var user = new User
            {
                Email = normalizedEmail,
                PasswordHash = _passwordService.HashPassword(request.Password),
                FullName = request.FullName,
                Role = request.Role,
                IsRegistered = true
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            await ActivatePendingEnrollmentsAsync(user.Id);

            var newToken = CreateToken(user);

            return Ok(new AuthResponseDto
            {
                Token = newToken,
                FullName = user.FullName,
                Role = user.Role.ToString()
            });
        }

        [HttpPost("fix-enrollments")]
        [Authorize(Roles = "Teacher")]
        public async Task<IActionResult> DevFixEnrollments([FromHeader(Name = "X-Dev-Secret")] string? secret)
        {
            var expected = _configuration["DevSecret"];
            if (string.IsNullOrEmpty(expected) || secret != expected)
            {
                return NotFound();
            }

            var shadowIds = await _context.Users
                .Where(u => !u.IsRegistered)
                .Select(u => u.Id)
                .ToListAsync();

            var pendingEnrollments = await _context.Enrollments
                .Where(e => e.Status == EnrollmentStatus.Pending && !shadowIds.Contains(e.StudentId))
                .ToListAsync();

            foreach (var enrollment in pendingEnrollments)
            {
                enrollment.Status = EnrollmentStatus.Enrolled;
            }

            var fixed_ = pendingEnrollments.Count;
            if (fixed_ > 0)
            {
                await _context.SaveChangesAsync();
            }

            return Ok(new { message = $"Activated {fixed_} enrollment(s)." });
        }

        [HttpPost("login")]
        public async Task<ActionResult<AuthResponseDto>> Login(LoginDto request)
        {
            var normalizedEmail = request.Email.Trim().ToLower();
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);
            if (user == null)
            {
                return BadRequest("User not found.");
            }

            if (!_passwordService.VerifyPassword(request.Password, user.PasswordHash))
            {
                return BadRequest("Wrong password.");
            }

            var token = CreateToken(user);

            return Ok(new AuthResponseDto
            {
                Token = token,
                FullName = user.FullName,
                Role = user.Role.ToString()
            });
        }

        [HttpPost("change-password")]
        [Authorize]
        public async Task<ActionResult<ChangePasswordResponse>> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
        {
            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdValue, out var userId))
            {
                return Unauthorized();
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user == null)
            {
                return Unauthorized();
            }

            var currentPassword = request?.CurrentPassword ?? string.Empty;
            var newPassword = request?.NewPassword ?? string.Empty;
            var confirmNewPassword = request?.ConfirmNewPassword ?? string.Empty;

            if (string.IsNullOrWhiteSpace(currentPassword))
            {
                return BadRequest(CreatePasswordError("current_password_required", "Current password is required."));
            }

            if (!_passwordService.VerifyPassword(currentPassword, user.PasswordHash))
            {
                return BadRequest(CreatePasswordError("current_password_incorrect", "Current password is incorrect."));
            }

            if (string.IsNullOrWhiteSpace(newPassword))
            {
                return BadRequest(CreatePasswordError("new_password_required", "New password is required."));
            }

            if (string.IsNullOrWhiteSpace(confirmNewPassword))
            {
                return BadRequest(CreatePasswordError("confirm_password_required", "Please confirm your new password."));
            }

            if (!string.Equals(newPassword, confirmNewPassword, StringComparison.Ordinal))
            {
                return BadRequest(CreatePasswordError("password_mismatch", "New password and confirmation do not match."));
            }

            if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
            {
                return BadRequest(CreatePasswordError("password_same_as_current", "New password must differ from current password."));
            }

            if (!IsPasswordStrongEnough(newPassword))
            {
                return BadRequest(CreatePasswordError("password_too_weak", "Use at least 8 characters."));
            }

            user.PasswordHash = _passwordService.HashPassword(newPassword);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(new ChangePasswordResponse
            {
                Message = "Password updated successfully. Your current session stays active.",
                SessionRemainsActive = true
            });
        }

        private string CreateToken(User user)
        {
            var jwtSettings = _configuration.GetSection("JwtSettings");
            var jwtKey = jwtSettings["Key"] ?? "super_secret_key_that_is_long_enough_12345_and_even_longer_to_satisfy_hmacsha512_requirement";
            var issuer = jwtSettings["Issuer"] ?? "AutoSlideBackend";
            var audience = jwtSettings["Audience"] ?? "AutoSlideUsers";
            var durationMinutes = int.TryParse(jwtSettings["DurationInMinutes"], out var mins) ? mins : 1440;

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role.ToString())
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha512Signature);

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(durationMinutes),
                signingCredentials: creds
            );

            var jwt = new JwtSecurityTokenHandler().WriteToken(token);

            return jwt;
        }

        [HttpGet("me")]
        [Authorize]
        public IActionResult GetMe()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var email = User.FindFirst(ClaimTypes.Email)?.Value;
            var role = User.FindFirst(ClaimTypes.Role)?.Value;

            return Ok(new { UserId = userId, Email = email, Role = role });
        }

        private static ChangePasswordErrorResponse CreatePasswordError(string code, string message)
        {
            return new ChangePasswordErrorResponse
            {
                Code = code,
                Message = message
            };
        }

        private static bool IsPasswordStrongEnough(string password)
        {
            return !string.IsNullOrWhiteSpace(password) && password.Length >= 8;
        }

        private async Task<int> ActivatePendingEnrollmentsAsync(Guid studentId)
        {
            var pendingEnrollments = await _context.Enrollments
                .Where(e => e.StudentId == studentId && e.Status == EnrollmentStatus.Pending)
                .ToListAsync();

            if (pendingEnrollments.Count == 0)
            {
                return 0;
            }

            foreach (var enrollment in pendingEnrollments)
            {
                enrollment.Status = EnrollmentStatus.Enrolled;
            }

            await _context.SaveChangesAsync();
            return pendingEnrollments.Count;
        }
    }
}
