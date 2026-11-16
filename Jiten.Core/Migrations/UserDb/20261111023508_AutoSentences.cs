using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class AutoSentences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutoSentencesMaxPerSync",
                schema: "user",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoAddedAt",
                schema: "user",
                table: "MinedSentences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutoSentencesJson",
                schema: "user",
                table: "AnkiNoteTypeSetups",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MinedSentenceRejections",
                schema: "user",
                columns: table => new
                {
                    MinedSentenceRejectionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MinedWordId = table.Column<long>(type: "bigint", nullable: false),
                    ExampleSentenceId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinedSentenceRejections", x => x.MinedSentenceRejectionId);
                    table.ForeignKey(
                        name: "FK_MinedSentenceRejections_MinedWords_MinedWordId",
                        column: x => x.MinedWordId,
                        principalSchema: "user",
                        principalTable: "MinedWords",
                        principalColumn: "MinedWordId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MinedSentenceRejections_MinedWordId",
                schema: "user",
                table: "MinedSentenceRejections",
                column: "MinedWordId");

            migrationBuilder.CreateIndex(
                name: "IX_MinedSentenceRejections_UserId_MinedWordId_ExampleSentenceId",
                schema: "user",
                table: "MinedSentenceRejections",
                columns: new[] { "UserId", "MinedWordId", "ExampleSentenceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MinedSentenceRejections",
                schema: "user");

            migrationBuilder.DropColumn(
                name: "AutoSentencesMaxPerSync",
                schema: "user",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AutoAddedAt",
                schema: "user",
                table: "MinedSentences");

            migrationBuilder.DropColumn(
                name: "AutoSentencesJson",
                schema: "user",
                table: "AnkiNoteTypeSetups");
        }
    }
}
