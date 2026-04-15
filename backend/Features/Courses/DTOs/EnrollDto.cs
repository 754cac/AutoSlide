using System.ComponentModel.DataAnnotations;

namespace BackendServer.Features.Courses.DTOs
{
    public class EnrollDto
    {
        [Required]
        public List<string> Emails { get; set; } = new List<string>();
    }
}
