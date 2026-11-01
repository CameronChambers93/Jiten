using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class TranslationsPerText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Old ids are occurrences and new ones texts. No database has cached translations yet, so there is nothing to map.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM jiten."ExampleSentenceTranslations") THEN
                        RAISE EXCEPTION 'ExampleSentenceTranslations has rows: map SentenceId to the central id before this migration';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ExampleSentenceTranslations_ExampleSentences_SentenceId",
                schema: "jiten",
                table: "ExampleSentenceTranslations");

            migrationBuilder.DropIndex(
                name: "IX_ExampleSentenceTranslations_DeckId",
                schema: "jiten",
                table: "ExampleSentenceTranslations");

            migrationBuilder.DropColumn(
                name: "DeckId",
                schema: "jiten",
                table: "ExampleSentenceTranslations");

            migrationBuilder.RenameColumn(
                name: "SentenceId",
                schema: "jiten",
                table: "ExampleSentenceTranslations",
                newName: "CentralSentenceId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleSentenceTranslations_Sentences_CentralSentenceId",
                schema: "jiten",
                table: "ExampleSentenceTranslations",
                column: "CentralSentenceId",
                principalSchema: "jiten",
                principalTable: "Sentences",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExampleSentenceTranslations_Sentences_CentralSentenceId",
                schema: "jiten",
                table: "ExampleSentenceTranslations");

            migrationBuilder.RenameColumn(
                name: "CentralSentenceId",
                schema: "jiten",
                table: "ExampleSentenceTranslations",
                newName: "SentenceId");

            migrationBuilder.AddColumn<int>(
                name: "DeckId",
                schema: "jiten",
                table: "ExampleSentenceTranslations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentenceTranslations_DeckId",
                schema: "jiten",
                table: "ExampleSentenceTranslations",
                column: "DeckId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleSentenceTranslations_ExampleSentences_SentenceId",
                schema: "jiten",
                table: "ExampleSentenceTranslations",
                column: "SentenceId",
                principalSchema: "jiten",
                principalTable: "ExampleSentences",
                principalColumn: "SentenceId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
