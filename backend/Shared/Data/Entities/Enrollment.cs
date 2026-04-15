using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BackendServer.Shared.Data.Entities
{
    public enum EnrollmentStatus
    {
        Pending,
        Enrolled
    }

    [Table("enrollments")]
    public class Enrollment
    {
        [Key]
        [Column("Id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("CourseId")]
        public Guid CourseId { get; set; }

        [Column("StudentId")]
        public Guid StudentId { get; set; }

        [Column("Status")]
        public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Enrolled;

        [Column("EnrolledAt")]
        public DateTime EnrolledAt { get; set; } = DateTime.UtcNow;
    }
}
