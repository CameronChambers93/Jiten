using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class AddTargetWordTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetWordTemplate",
                schema: "user",
                table: "UserSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // The store marks the target word with <b>; cards get the user's template when written out.
            migrationBuilder.Sql(@"
                UPDATE ""user"".""MinedSentences""
                SET ""Text"" = regexp_replace(""Text"", '<span class=""cloze_body"">(.*?)</span>', '<b>\1</b>', 'g'),
                    ""Furigana"" = regexp_replace(""Furigana"", '<span class=""cloze_body"">(.*?)</span>', '<b>\1</b>', 'g')
                WHERE ""Text"" LIKE '%cloze_body%' OR ""Furigana"" LIKE '%cloze_body%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetWordTemplate",
                schema: "user",
                table: "UserSettings");
        }
    }
}
