using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class SentenceFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SentenceFixes",
                schema: "user",
                columns: table => new
                {
                    SentenceFixId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExampleSentenceId = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Tokens = table.Column<byte[]>(type: "bytea", nullable: true),
                    English = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartMs = table.Column<int>(type: "integer", nullable: true),
                    EndMs = table.Column<int>(type: "integer", nullable: true),
                    PictureMs = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentenceFixes", x => x.SentenceFixId);
                    table.ForeignKey(
                        name: "FK_SentenceFixes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentenceFixes_ExampleSentenceId",
                schema: "user",
                table: "SentenceFixes",
                column: "ExampleSentenceId");

            migrationBuilder.CreateIndex(
                name: "IX_SentenceFixes_UserId_ExampleSentenceId",
                schema: "user",
                table: "SentenceFixes",
                columns: new[] { "UserId", "ExampleSentenceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentenceFixes",
                schema: "user");
        }
    }
}
