using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionUnlockedSlides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: courses, enrollments, materials, sessions, users tables already
            // exist in the database — this migration only adds the new table.

            migrationBuilder.CreateTable(
                name: "session_unlocked_slides",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlideIndex = table.Column<int>(type: "integer", nullable: false),
                    UnlockedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false,
                        defaultValueSql: "(NOW() AT TIME ZONE 'Asia/Hong_Kong')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_unlocked_slides", x => new { x.SessionId, x.SlideIndex });
                    table.ForeignKey(
                        name: "fk_session_unlocked_slides_sessions",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_session_unlocked_slides_sessionid",
                table: "session_unlocked_slides",
                column: "SessionId");

            // One-time backfill: unlock slides 1..CurrentSlideIndex for all Active/Ended sessions
            migrationBuilder.Sql(@"
                INSERT INTO session_unlocked_slides (""SessionId"", ""SlideIndex"", ""UnlockedAt"")
                SELECT s.""Id"", gs.slide, (NOW() AT TIME ZONE 'Asia/Hong_Kong')
                FROM sessions s
                CROSS JOIN generate_series(1, GREATEST(s.""CurrentSlideIndex"", 1)) AS gs(slide)
                WHERE s.""Status"" IN (1, 2)
                ON CONFLICT DO NOTHING;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "session_unlocked_slides");
        }
    }
}
