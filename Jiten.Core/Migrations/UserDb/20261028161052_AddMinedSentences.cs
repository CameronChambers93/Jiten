using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class AddMinedSentences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MinedWords",
                schema: "user",
                columns: table => new
                {
                    MinedWordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: true),
                    ReadingIndex = table.Column<byte>(type: "smallint", nullable: true),
                    Expression = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Reading = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinedWords", x => x.MinedWordId);
                    table.ForeignKey(
                        name: "FK_MinedWords_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MinedNotes",
                schema: "user",
                columns: table => new
                {
                    MinedNoteId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MinedWordId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnkiNoteId = table.Column<long>(type: "bigint", nullable: false),
                    ModelName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeckName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinedNotes", x => x.MinedNoteId);
                    table.ForeignKey(
                        name: "FK_MinedNotes_MinedWords_MinedWordId",
                        column: x => x.MinedWordId,
                        principalSchema: "user",
                        principalTable: "MinedWords",
                        principalColumn: "MinedWordId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MinedSentences",
                schema: "user",
                columns: table => new
                {
                    MinedSentenceId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MinedWordId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    English = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    EnglishOrigin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Origin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExampleSentenceId = table.Column<long>(type: "bigint", nullable: true),
                    SourceTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PreviousLine = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    MinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AudioPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    AudioContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PicturePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PictureContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AnkiAudioFilename = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AnkiPictureFilename = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SourceAnkiNoteId = table.Column<long>(type: "bigint", nullable: true),
                    IsMain = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    FlaggedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinedSentences", x => x.MinedSentenceId);
                    table.ForeignKey(
                        name: "FK_MinedSentences_MinedWords_MinedWordId",
                        column: x => x.MinedWordId,
                        principalSchema: "user",
                        principalTable: "MinedWords",
                        principalColumn: "MinedWordId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MinedNotes_MinedWordId",
                schema: "user",
                table: "MinedNotes",
                column: "MinedWordId");

            migrationBuilder.CreateIndex(
                name: "IX_MinedNotes_UserId_AnkiNoteId",
                schema: "user",
                table: "MinedNotes",
                columns: new[] { "UserId", "AnkiNoteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MinedSentences_MinedWordId_SortOrder",
                schema: "user",
                table: "MinedSentences",
                columns: new[] { "MinedWordId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_MinedSentences_UserId_SourceAnkiNoteId",
                schema: "user",
                table: "MinedSentences",
                columns: new[] { "UserId", "SourceAnkiNoteId" });

            migrationBuilder.CreateIndex(
                name: "IX_MinedWords_UserId_Expression_Reading",
                schema: "user",
                table: "MinedWords",
                columns: new[] { "UserId", "Expression", "Reading" });

            migrationBuilder.CreateIndex(
                name: "IX_MinedWords_UserId_WordId_ReadingIndex",
                schema: "user",
                table: "MinedWords",
                columns: new[] { "UserId", "WordId", "ReadingIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MinedNotes",
                schema: "user");

            migrationBuilder.DropTable(
                name: "MinedSentences",
                schema: "user");

            migrationBuilder.DropTable(
                name: "MinedWords",
                schema: "user");
        }
    }
}
