using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class SentenceFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EndMs",
                schema: "jiten",
                table: "ExampleSentences",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "English",
                schema: "jiten",
                table: "ExampleSentences",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PictureMs",
                schema: "jiten",
                table: "ExampleSentences",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceText",
                schema: "jiten",
                table: "ExampleSentences",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StartMs",
                schema: "jiten",
                table: "ExampleSentences",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SentenceCorrections",
                schema: "jiten",
                columns: table => new
                {
                    SentenceCorrectionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExampleSentenceId = table.Column<long>(type: "bigint", nullable: false),
                    DeckId = table.Column<int>(type: "integer", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    SourceText = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: true),
                    English = table.Column<string>(type: "text", nullable: true),
                    StartMs = table.Column<int>(type: "integer", nullable: true),
                    EndMs = table.Column<int>(type: "integer", nullable: true),
                    PictureMs = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentenceCorrections", x => x.SentenceCorrectionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentenceCorrections_DeckId",
                schema: "jiten",
                table: "SentenceCorrections",
                column: "DeckId");

            migrationBuilder.CreateIndex(
                name: "IX_SentenceCorrections_ExampleSentenceId",
                schema: "jiten",
                table: "SentenceCorrections",
                column: "ExampleSentenceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentenceCorrections",
                schema: "jiten");

            migrationBuilder.DropColumn(
                name: "EndMs",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "English",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "PictureMs",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "SourceText",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "StartMs",
                schema: "jiten",
                table: "ExampleSentences");
        }
    }
}
