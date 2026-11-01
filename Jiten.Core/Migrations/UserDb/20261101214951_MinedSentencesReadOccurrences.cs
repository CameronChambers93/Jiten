using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class MinedSentencesReadOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These columns go, so nothing may depend on them: every sentence has to have its occurrence by now.
            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM ""user"".""MinedSentences"" m
                               WHERE NOT EXISTS (SELECT 1 FROM jiten.""ExampleSentences"" e WHERE e.""SentenceId"" = m.""ExampleSentenceId"")) THEN
                        RAISE EXCEPTION 'Mined sentences without an occurrence: run --migrate-mined-sentences from commit 4decc54d first';
                    END IF;
                END $$;

                UPDATE jiten.""ExampleSentences"" e SET ""SourceTitle"" = m.""SourceTitle""
                FROM ""user"".""MinedSentences"" m
                WHERE m.""ExampleSentenceId"" = e.""SentenceId"" AND e.""DeckId"" IS NOT NULL AND e.""SourceTitle"" IS NULL;");

            migrationBuilder.DropColumn(
                name: "AudioContentType",
                schema: "user",
                table: "MinedSentences");

            migrationBuilder.DropColumn(
                name: "AudioPath",
                schema: "user",
                table: "MinedSentences");

            migrationBuilder.DropColumn(
                name: "PictureContentType",
                schema: "user",
                table: "MinedSentences");

            migrationBuilder.DropColumn(
                name: "PicturePath",
                schema: "user",
                table: "MinedSentences");

            migrationBuilder.DropColumn(
                name: "SourceTitle",
                schema: "user",
                table: "MinedSentences");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                schema: "user",
                table: "MinedSentences");

            migrationBuilder.AlterColumn<string>(
                name: "Text",
                schema: "user",
                table: "MinedSentences",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000);

            // Text stays only where the occurrence can't rebuild it exactly; those rows are the user's overrides.
            migrationBuilder.Sql(@"
                UPDATE ""user"".""MinedSentences"" m SET ""Text"" = NULL
                FROM jiten.""ExampleSentences"" e
                WHERE e.""SentenceId"" = m.""ExampleSentenceId""
                  AND m.""Text"" = COALESCE(e.""Speaker"", '') || CASE WHEN m.""TargetPosition"" IS NULL THEN e.""Text"" ELSE
                      left(e.""Text"", m.""TargetPosition"") || '<b>' || substr(e.""Text"", m.""TargetPosition"" + 1, m.""TargetLength"") || '</b>'
                      || substr(e.""Text"", m.""TargetPosition"" + m.""TargetLength"" + 1) END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""user"".""MinedSentences"" m
                SET ""Text"" = COALESCE(e.""Speaker"", '') || CASE WHEN m.""TargetPosition"" IS NULL THEN e.""Text"" ELSE
                    left(e.""Text"", m.""TargetPosition"") || '<b>' || substr(e.""Text"", m.""TargetPosition"" + 1, m.""TargetLength"") || '</b>'
                    || substr(e.""Text"", m.""TargetPosition"" + m.""TargetLength"" + 1) END
                FROM jiten.""ExampleSentences"" e
                WHERE e.""SentenceId"" = m.""ExampleSentenceId"" AND m.""Text"" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "Text",
                schema: "user",
                table: "MinedSentences",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioContentType",
                schema: "user",
                table: "MinedSentences",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioPath",
                schema: "user",
                table: "MinedSentences",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PictureContentType",
                schema: "user",
                table: "MinedSentences",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PicturePath",
                schema: "user",
                table: "MinedSentences",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTitle",
                schema: "user",
                table: "MinedSentences",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                schema: "user",
                table: "MinedSentences",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }
    }
}
