using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class SentenceSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SentenceSubmissions",
                schema: "jiten",
                columns: table => new
                {
                    SentenceSubmissionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExampleSentenceId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    English = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartMs = table.Column<int>(type: "integer", nullable: true),
                    EndMs = table.Column<int>(type: "integer", nullable: true),
                    PictureMs = table.Column<int>(type: "integer", nullable: true),
                    SameTextIds = table.Column<long[]>(type: "bigint[]", nullable: false),
                    BaseCorrectionId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReviewerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentenceSubmissions", x => x.SentenceSubmissionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentenceSubmissions_ExampleSentenceId",
                schema: "jiten",
                table: "SentenceSubmissions",
                column: "ExampleSentenceId");

            migrationBuilder.CreateIndex(
                name: "IX_SentenceSubmissions_Status_CreatedAt",
                schema: "jiten",
                table: "SentenceSubmissions",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SentenceSubmissions_UserId_ExampleSentenceId",
                schema: "jiten",
                table: "SentenceSubmissions",
                columns: new[] { "UserId", "ExampleSentenceId" },
                unique: true,
                filter: "\"Status\" = 'pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentenceSubmissions",
                schema: "jiten");
        }
    }
}
