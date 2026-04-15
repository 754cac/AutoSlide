using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackendServer.Migrations
{
    /// <inheritdoc />
    public partial class FixSnapshotSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SessionUnlockedSlides_Sessions",
                table: "SessionUnlockedSlides");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SessionUnlockedSlides",
                table: "SessionUnlockedSlides");

            migrationBuilder.DropIndex(
                name: "idx_session_unlockedslides_sessionid",
                table: "SessionUnlockedSlides");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SessionUnlockedSlides_SlideIndex",
                table: "SessionUnlockedSlides");

            migrationBuilder.DropColumn(
                name: "UnlockReason",
                table: "SessionUnlockedSlides");

            migrationBuilder.DropColumn(
                name: "UnlockedBy",
                table: "SessionUnlockedSlides");

            migrationBuilder.RenameTable(
                name: "SessionUnlockedSlides",
                newName: "session_unlocked_slides");

            migrationBuilder.AlterColumn<DateTime>(
                name: "UnlockedAt",
                table: "session_unlocked_slides",
                type: "timestamp without time zone",
                nullable: false,
                defaultValueSql: "(NOW() AT TIME ZONE 'Asia/Hong_Kong')",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "NOW()");

            migrationBuilder.AddPrimaryKey(
                name: "PK_session_unlocked_slides",
                table: "session_unlocked_slides",
                columns: new[] { "SessionId", "SlideIndex" });

            migrationBuilder.AddForeignKey(
                name: "fk_session_unlocked_slides_sessions_sessionid",
                table: "session_unlocked_slides",
                column: "SessionId",
                principalTable: "sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_session_unlocked_slides_sessions_sessionid",
                table: "session_unlocked_slides");

            migrationBuilder.DropPrimaryKey(
                name: "PK_session_unlocked_slides",
                table: "session_unlocked_slides");

            migrationBuilder.RenameTable(
                name: "session_unlocked_slides",
                newName: "SessionUnlockedSlides");

            migrationBuilder.AlterColumn<DateTime>(
                name: "UnlockedAt",
                table: "SessionUnlockedSlides",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "NOW()",
                oldClrType: typeof(DateTime),
                oldType: "timestamp without time zone",
                oldDefaultValueSql: "(NOW() AT TIME ZONE 'Asia/Hong_Kong')");

            migrationBuilder.AddColumn<string>(
                name: "UnlockReason",
                table: "SessionUnlockedSlides",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnlockedBy",
                table: "SessionUnlockedSlides",
                type: "text",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_SessionUnlockedSlides",
                table: "SessionUnlockedSlides",
                columns: new[] { "SessionId", "SlideIndex" });

            migrationBuilder.CreateIndex(
                name: "idx_session_unlockedslides_sessionid",
                table: "SessionUnlockedSlides",
                column: "SessionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SessionUnlockedSlides_SlideIndex",
                table: "SessionUnlockedSlides",
                sql: "\"SlideIndex\" >= 1");

            migrationBuilder.AddForeignKey(
                name: "FK_SessionUnlockedSlides_Sessions",
                table: "SessionUnlockedSlides",
                column: "SessionId",
                principalTable: "sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
