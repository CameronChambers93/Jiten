using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class SentenceChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SentenceChecks",
                schema: "jiten",
                columns: table => new
                {
                    ExampleSentenceId = table.Column<long>(type: "bigint", nullable: false),
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    CheckedText = table.Column<string>(type: "text", nullable: false),
                    CheckedEnglish = table.Column<string>(type: "text", nullable: true),
                    EnglishOrigin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LeasedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    FindingsJson = table.Column<string>(type: "text", nullable: false),
                    Checker = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ResolutionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentenceChecks", x => x.ExampleSentenceId);
                    table.ForeignKey(
                        name: "FK_SentenceChecks_ExampleSentences_ExampleSentenceId",
                        column: x => x.ExampleSentenceId,
                        principalSchema: "jiten",
                        principalTable: "ExampleSentences",
                        principalColumn: "SentenceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentenceChecks_Status_DeckId",
                schema: "jiten",
                table: "SentenceChecks",
                columns: new[] { "Status", "DeckId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentenceChecks",
                schema: "jiten");
        }
    }
}
