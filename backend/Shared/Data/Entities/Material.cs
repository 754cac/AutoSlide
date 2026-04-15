using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BackendServer.Shared.Data.Entities
{
    [Table("materials")]
    public class Material
    {
        [Key]
        [Column("Id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("CourseId")]
        public Guid CourseId { get; set; }

        [Column("Title")]
        public string Title { get; set; } = string.Empty;

        [Column("OriginalFileName")]
        public string OriginalFileName { get; set; } = string.Empty;

        [Column("StoragePath")]
        public string StoragePath { get; set; } = string.Empty;

        [Column("Week")]
        public int Week { get; set; }

        [Column("IsVisible")]
        public bool IsVisible { get; set; } = false;

        [Column("ReleaseAt")]
        public DateTime? ReleaseAt { get; set; }

        [Column("UploadedAt")]
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    }
}
