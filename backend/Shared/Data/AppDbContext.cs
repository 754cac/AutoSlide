using Microsoft.EntityFrameworkCore;
using BackendServer.Shared.Data.Entities;

namespace BackendServer.Shared.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<Course> Courses { get; set; }
        public DbSet<Session> Sessions { get; set; }
        public DbSet<Enrollment> Enrollments { get; set; }
        public DbSet<Material> Materials { get; set; }
        public DbSet<SessionUnlockedSlide> SessionUnlockedSlides { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema(null);

            // global naming convention: tables lowercase, columns quoted PascalCase
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                // table names in schema are lowercase
                entity.SetTableName(entity.GetTableName()!.ToLowerInvariant());

                foreach (var property in entity.GetProperties())
                {
                    property.SetColumnName(property.Name);
                }

                foreach (var key in entity.GetKeys())
                    key.SetName(key.GetName()!.ToLowerInvariant());

                foreach (var fk in entity.GetForeignKeys())
                    fk.SetConstraintName(fk.GetConstraintName()!.ToLowerInvariant());

                foreach (var index in entity.GetIndexes())
                    index.SetDatabaseName(index.GetDatabaseName()!.ToLowerInvariant());
            }

            // ── User ──
            modelBuilder.Entity<User>(e =>
            {
                e.ToTable("users");
                e.Property(p => p.Id).HasColumnName("Id");
                e.Property(p => p.Email).HasColumnName("Email");
                e.Property(p => p.PasswordHash).HasColumnName("PasswordHash");
                e.Property(p => p.FullName).HasColumnName("FullName");
                e.Property(p => p.Role).HasColumnName("Role").HasConversion<int>();
                e.Property(p => p.IsRegistered).HasColumnName("IsRegistered");
                e.Property(p => p.CreatedAt).HasColumnName("CreatedAt").HasColumnType("timestamp with time zone");
            });

            // ── Course ──
            modelBuilder.Entity<Course>(e =>
            {
                e.ToTable("courses");
                e.Property(p => p.Id).HasColumnName("Id");
                e.Property(p => p.Code).HasColumnName("Code");
                e.Property(p => p.Name).HasColumnName("Name");
                e.Property(p => p.Description).HasColumnName("Description").IsRequired(false);
                e.Property(p => p.TeacherId).HasColumnName("TeacherId");
                e.Property(p => p.ActiveSessionId).HasColumnName("ActiveSessionId").IsRequired(false);
                e.Property(p => p.CreatedAt).HasColumnName("CreatedAt").HasColumnType("timestamp with time zone");
            });

            // ── Session ──
            modelBuilder.Entity<Session>(e =>
            {
                e.ToTable("sessions");
                e.Property(p => p.Id).HasColumnName("Id");
                e.Property(p => p.PresentationTitle).HasColumnName("PresentationTitle");
                e.Property(p => p.SlideCount).HasColumnName("SlideCount");
                e.Property(p => p.CourseId).HasColumnName("CourseId").IsRequired(false);
                e.Property(p => p.PresenterToken).HasColumnName("PresenterToken");
                e.Property(p => p.Status).HasColumnName("Status").HasConversion<int>();
                e.Property(p => p.CreatedAt).HasColumnName("CreatedAt").HasColumnType("timestamp with time zone");
                e.Property(p => p.StartedAt).HasColumnName("StartedAt").IsRequired(false).HasColumnType("timestamp with time zone");
                e.Property(p => p.EndedAt).HasColumnName("EndedAt").IsRequired(false).HasColumnType("timestamp with time zone");
                e.Property(p => p.TranscriptStoragePath).HasColumnName("TranscriptStoragePath").IsRequired(false);
                e.Property(p => p.SummaryText).HasColumnName("SummaryText").IsRequired(false);
                e.Property(p => p.AnnotatedPptxStoragePath).HasColumnName("AnnotatedPptxStoragePath").IsRequired(false);
                e.Property(p => p.CurrentSlideIndex).HasColumnName("CurrentSlideIndex").HasDefaultValue(0);
                e.Property(p => p.TotalSlides).HasColumnName("TotalSlides").HasDefaultValue(0);
                e.Property(p => p.AllowStudentDownload).HasColumnName("AllowStudentDownload").HasDefaultValue(false);
                e.Property(p => p.DownloadAvailableAt).HasColumnName("DownloadAvailableAt").IsRequired(false).HasColumnType("timestamp with time zone");
            });

            // ── Enrollment ──
            modelBuilder.Entity<Enrollment>(e =>
            {
                e.ToTable("enrollments");
                e.Property(p => p.Id).HasColumnName("Id");
                e.Property(p => p.CourseId).HasColumnName("CourseId");
                e.Property(p => p.StudentId).HasColumnName("StudentId");
                e.Property(p => p.Status).HasColumnName("Status").HasConversion<int>();
                e.Property(p => p.EnrolledAt).HasColumnName("EnrolledAt").HasColumnType("timestamp with time zone");
                e.HasIndex(en => new { en.CourseId, en.StudentId }).IsUnique();
            });

            // ── Material ──
            modelBuilder.Entity<Material>(e =>
            {
                e.ToTable("materials");
                e.Property(p => p.Id).HasColumnName("Id");
                e.Property(p => p.CourseId).HasColumnName("CourseId");
                e.Property(p => p.Title).HasColumnName("Title");
                e.Property(p => p.OriginalFileName).HasColumnName("OriginalFileName");
                e.Property(p => p.StoragePath).HasColumnName("StoragePath");
                e.Property(p => p.Week).HasColumnName("Week");
                e.Property(p => p.IsVisible).HasColumnName("IsVisible");
                e.Property(p => p.ReleaseAt).HasColumnName("ReleaseAt").IsRequired(false).HasColumnType("timestamp with time zone");
                e.Property(p => p.UploadedAt).HasColumnName("UploadedAt").HasColumnType("timestamp with time zone");
            });

            // ── SessionUnlockedSlide ──
            modelBuilder.Entity<SessionUnlockedSlide>(e =>
            {
                e.ToTable("session_unlocked_slides");
                e.HasKey(p => new { p.SessionId, p.SlideIndex });
                e.Property(p => p.UnlockedAt)
                    .HasColumnName("UnlockedAt")
                    .HasColumnType("timestamp without time zone")
                    .HasDefaultValueSql("(NOW() AT TIME ZONE 'Asia/Hong_Kong')");
                e.HasOne(p => p.Session)
                 .WithMany()
                 .HasForeignKey(p => p.SessionId)
                 .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
