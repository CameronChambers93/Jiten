using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMinedOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "DeckId",
                schema: "jiten",
                table: "ExampleSentences",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "AudioContentType",
                schema: "jiten",
                table: "ExampleSentences",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioPath",
                schema: "jiten",
                table: "ExampleSentences",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                schema: "jiten",
                table: "ExampleSentences",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PictureContentType",
                schema: "jiten",
                table: "ExampleSentences",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PicturePath",
                schema: "jiten",
                table: "ExampleSentences",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTitle",
                schema: "jiten",
                table: "ExampleSentences",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                schema: "jiten",
                table: "ExampleSentences",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Speaker",
                schema: "jiten",
                table: "ExampleSentences",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentences_OwnerId",
                schema: "jiten",
                table: "ExampleSentences",
                column: "OwnerId");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION jiten.normalize_sentence(value text) RETURNS text
                LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
                    SELECT regexp_replace(
                        normalize(regexp_replace(regexp_replace(value, '<br\s*/?>', ' ', 'gi'), '</?[a-zA-Z][^>]*>', '', 'g'), NFC),
                        '^[\s\u3000]+|[\s\u3000]+$', '', 'g')
                $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExampleSentences_OwnerId",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "AudioContentType",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "AudioPath",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "PictureContentType",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "PicturePath",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "SourceTitle",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "Speaker",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.AlterColumn<int>(
                name: "DeckId",
                schema: "jiten",
                table: "ExampleSentences",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
