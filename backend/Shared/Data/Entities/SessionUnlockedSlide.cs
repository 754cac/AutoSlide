using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BackendServer.Shared.Data.Entities
{
    /// <summary>
    /// Tracks which slides have been unlocked in a live session.
    /// One row per (session, slideIndex) pair — 1-based indices.
    /// Rows are only ever inserted, never deleted, so the unlock set
    /// is monotonically growing (past unlocks are never revoked).
    /// </summary>
    [Table("session_unlocked_slides")]
    public class SessionUnlockedSlide
    {
        [Column("SessionId")]
        public Guid SessionId { get; set; }

        /// <summary>1-based slide index (matches PDF page numbers and frontend page state).</summary>
        [Column("SlideIndex")]
        public int SlideIndex { get; set; }

        [Column("UnlockedAt")]
        public DateTime UnlockedAt { get; set; } = DateTime.UtcNow;

        // Navigation property
        public Session Session { get; set; } = null!;
    }
}
