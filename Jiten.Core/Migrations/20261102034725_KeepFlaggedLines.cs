using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class KeepFlaggedLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION jiten.keep_mined_lines() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    UPDATE jiten.""ExampleSentences"" es
                    SET ""DeckId"" = NULL, ""RemovedFromDeck"" = TRUE, ""WordKeys"" = '{}'
                    WHERE es.""DeckId"" = OLD.""DeckId""
                      AND (EXISTS (SELECT 1 FROM ""user"".""MinedSentences"" m WHERE m.""ExampleSentenceId"" = es.""SentenceId"")
                           OR EXISTS (SELECT 1 FROM ""user"".""SentenceFlags"" f WHERE f.""ExampleSentenceId"" = es.""SentenceId"" AND f.""ResolvedAt"" IS NULL));
                    RETURN OLD;
                END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION jiten.keep_mined_lines() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    UPDATE jiten.""ExampleSentences"" es
                    SET ""DeckId"" = NULL, ""RemovedFromDeck"" = TRUE, ""WordKeys"" = '{}'
                    WHERE es.""DeckId"" = OLD.""DeckId""
                      AND (EXISTS (SELECT 1 FROM ""user"".""MinedSentences"" m WHERE m.""ExampleSentenceId"" = es.""SentenceId""));
                    RETURN OLD;
                END $$;");
        }
    }
}
