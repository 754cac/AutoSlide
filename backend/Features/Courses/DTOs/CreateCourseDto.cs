using System.ComponentModel.DataAnnotations;

namespace BackendServer.Features.Courses.DTOs
{
    public class CreateCourseDto
    {
        [Required]
        public string Code { get; set; } = string.Empty;

        [Required]
        public string Name { get; set; } = string.Empty;
    }
}
