using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class NoteWordMatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WordMatch",
                schema: "user",
                table: "MinedNotes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // Notes the mine button recorded carry the entry they were mined for; the rest are checked by the next Sync.
            migrationBuilder.Sql("""
                UPDATE "user"."MinedNotes" n SET "WordMatch" = 'mined'
                WHERE EXISTS (SELECT 1 FROM "user"."MinedSentences" s
                              WHERE s."SourceAnkiNoteId" = n."AnkiNoteId" AND s."UserId" = n."UserId" AND s."Origin" = 'jiten');
                """);

            // Known words were matched by word and reading alone: the next Sync sends every card again.
            migrationBuilder.Sql("""UPDATE "user"."UserSettings" SET "AnkiKnownWordsSyncedAt" = NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WordMatch",
                schema: "user",
                table: "MinedNotes");
        }
    }
}
