using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralSentences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CentralSentenceId",
                schema: "jiten",
                table: "ExampleSentences",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RemovedFromDeck",
                schema: "jiten",
                table: "ExampleSentences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Sentences",
                schema: "jiten",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TextHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sentences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentences_CentralSentenceId",
                schema: "jiten",
                table: "ExampleSentences",
                column: "CentralSentenceId");

            migrationBuilder.CreateIndex(
                name: "IX_Sentences_TextHash",
                schema: "jiten",
                table: "Sentences",
                column: "TextHash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleSentences_Sentences_CentralSentenceId",
                schema: "jiten",
                table: "ExampleSentences",
                column: "CentralSentenceId",
                principalSchema: "jiten",
                principalTable: "Sentences",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // What makes two lines the same text. HTML-like tags only, since game lines can contain a literal <.
            migrationBuilder.Sql(@"
                CREATE FUNCTION jiten.normalize_sentence(value text) RETURNS text
                LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
                    SELECT regexp_replace(
                        normalize(regexp_replace(value, '</?[a-zA-Z][^>]*>', '', 'g'), NFC),
                        '^[\s\u3000]+|[\s\u3000]+$', '', 'g')
                $$;");

            migrationBuilder.Sql(@"
                INSERT INTO jiten.""Sentences"" (""TextHash"", ""Text"")
                SELECT DISTINCT sha256(convert_to(t, 'UTF8')), t
                FROM (SELECT jiten.normalize_sentence(""Text"") AS t FROM jiten.""ExampleSentences"") n;

                UPDATE jiten.""ExampleSentences"" es
                SET ""CentralSentenceId"" = s.""Id""
                FROM jiten.""Sentences"" s
                WHERE s.""TextHash"" = sha256(convert_to(jiten.normalize_sentence(es.""Text""), 'UTF8'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP FUNCTION jiten.normalize_sentence(text);");

            migrationBuilder.DropForeignKey(
                name: "FK_ExampleSentences_Sentences_CentralSentenceId",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropTable(
                name: "Sentences",
                schema: "jiten");

            migrationBuilder.DropIndex(
                name: "IX_ExampleSentences_CentralSentenceId",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "CentralSentenceId",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "RemovedFromDeck",
                schema: "jiten",
                table: "ExampleSentences");
        }
    }
}
