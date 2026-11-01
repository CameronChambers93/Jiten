using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class KeepMinedLinesAndDropCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Runs before the cascade, so a mined line outlives its deck like one a reparse dropped.
            migrationBuilder.Sql(@"
                CREATE FUNCTION jiten.keep_mined_lines() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    UPDATE jiten.""ExampleSentences"" es
                    SET ""DeckId"" = NULL, ""RemovedFromDeck"" = TRUE, ""WordKeys"" = '{}'
                    WHERE es.""DeckId"" = OLD.""DeckId""
                      AND EXISTS (SELECT 1 FROM ""user"".""MinedSentences"" m WHERE m.""ExampleSentenceId"" = es.""SentenceId"");
                    RETURN OLD;
                END $$;

                CREATE TRIGGER keep_mined_lines BEFORE DELETE ON jiten.""Decks""
                FOR EACH ROW EXECUTE FUNCTION jiten.keep_mined_lines();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER keep_mined_lines ON jiten.""Decks"";
                DROP FUNCTION jiten.keep_mined_lines();");
        }
    }
}
