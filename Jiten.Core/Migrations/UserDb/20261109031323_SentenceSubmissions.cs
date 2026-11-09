using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class SentenceSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedFixMode",
                schema: "user",
                table: "UserSettings",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedFixAt",
                schema: "user",
                table: "SentenceFixes",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedFixMode",
                schema: "user",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ApprovedFixAt",
                schema: "user",
                table: "SentenceFixes");
        }
    }
}
