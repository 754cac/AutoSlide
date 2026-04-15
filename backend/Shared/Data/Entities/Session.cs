using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BackendServer.Shared.Data.Entities
{
    public enum SessionStatus
    {
        Created,
        Active,
        Ended
    }

    [Table("sessions")]
    public class Session
    {
        [Key]
        [Column("Id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Column("PresentationTitle")]
        public string PresentationTitle { get; set; } = string.Empty;

        [Column("SlideCount")]
        public int SlideCount { get; set; }

        [Column("CourseId")]
        public Guid? CourseId { get; set; }

        [Column("PresenterToken")]
        public string PresenterToken { get; set; } = string.Empty;

        [Column("Status")]
        public SessionStatus Status { get; set; } = SessionStatus.Created;

        [Column("CreatedAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("StartedAt")]
        public DateTime? StartedAt { get; set; }

        [Column("EndedAt")]
        public DateTime? EndedAt { get; set; }

        [Column("TranscriptStoragePath")]
        public string? TranscriptStoragePath { get; set; }

        [Column("SummaryText")]
        public string? SummaryText { get; set; }

        [Column("AnnotatedPptxStoragePath")]
        public string? AnnotatedPptxStoragePath { get; set; }

        // ── Secure slide delivery ──
        /// <summary>Server-side slide index gate: students may only request pages 1..CurrentSlideIndex.</summary>
        [Column("CurrentSlideIndex")]
        public int CurrentSlideIndex { get; set; } = 0;

        /// <summary>Number of single-page PDFs stored in Supabase slides bucket (set after PDF split).</summary>
        [Column("TotalSlides")]
        public int TotalSlides { get; set; } = 0;

        // ── Download control ──
        /// <summary>Instructor toggles this to let enrolled students download materials after session ends.</summary>
        [Column("AllowStudentDownload")]
        public bool AllowStudentDownload { get; set; } = false;

        /// <summary>Optional scheduled release time for student downloads.</summary>
        [Column("DownloadAvailableAt")]
        public DateTime? DownloadAvailableAt { get; set; }
    }
}
