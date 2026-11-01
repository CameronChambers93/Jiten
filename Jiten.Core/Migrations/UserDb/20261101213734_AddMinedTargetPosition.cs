using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class AddMinedTargetPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TargetLength",
                schema: "user",
                table: "MinedSentences",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetPosition",
                schema: "user",
                table: "MinedSentences",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetLength",
                schema: "user",
                table: "MinedSentences");

            migrationBuilder.DropColumn(
                name: "TargetPosition",
                schema: "user",
                table: "MinedSentences");
        }
    }
}
