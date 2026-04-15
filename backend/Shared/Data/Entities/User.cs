using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BackendServer.Shared.Data.Entities
{
    public enum UserRole
    {
        Teacher,
        Student
    }

    [Table("users")]
    public class User
    {
        [Key]
        [Column("Id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("Email")]
        public string Email { get; set; } = string.Empty;

        [Column("PasswordHash")]
        public string PasswordHash { get; set; } = string.Empty;

        [Column("FullName")]
        public string FullName { get; set; } = string.Empty;

        [Column("Role")]
        public UserRole Role { get; set; }

        [Column("IsRegistered")]
        public bool IsRegistered { get; set; } = true;

        [Column("CreatedAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
