using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSentenceTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Tokens",
                schema: "jiten",
                table: "Sentences",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int[]>(
                name: "WordKeys",
                schema: "jiten",
                table: "Sentences",
                type: "integer[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Tokens",
                schema: "jiten",
                table: "Sentences");

            migrationBuilder.DropColumn(
                name: "WordKeys",
                schema: "jiten",
                table: "Sentences");
        }
    }
}
