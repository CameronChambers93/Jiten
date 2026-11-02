using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class AddSentenceFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SentenceFlags",
                schema: "user",
                columns: table => new
                {
                    SentenceFlagId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExampleSentenceId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentenceFlags", x => x.SentenceFlagId);
                    table.ForeignKey(
                        name: "FK_SentenceFlags_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentenceFlags_ExampleSentenceId",
                schema: "user",
                table: "SentenceFlags",
                column: "ExampleSentenceId");

            migrationBuilder.CreateIndex(
                name: "IX_SentenceFlags_UserId_ExampleSentenceId",
                schema: "user",
                table: "SentenceFlags",
                columns: new[] { "UserId", "ExampleSentenceId" },
                unique: true,
                filter: "\"ResolvedAt\" IS NULL");

            // Flags from before reasons existed stay open, as needing one.
            migrationBuilder.Sql(@"
                INSERT INTO ""user"".""SentenceFlags"" (""UserId"", ""ExampleSentenceId"", ""CreatedAt"")
                SELECT ""UserId"", ""ExampleSentenceId"", MIN(""FlaggedAt"")
                FROM ""user"".""MinedSentences""
                WHERE ""FlaggedAt"" IS NOT NULL AND ""ExampleSentenceId"" IS NOT NULL
                GROUP BY ""UserId"", ""ExampleSentenceId"";");

            migrationBuilder.DropColumn(
                name: "FlaggedAt",
                schema: "user",
                table: "MinedSentences");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FlaggedAt",
                schema: "user",
                table: "MinedSentences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(@"
                UPDATE ""user"".""MinedSentences"" m SET ""FlaggedAt"" = f.""CreatedAt""
                FROM ""user"".""SentenceFlags"" f
                WHERE f.""UserId"" = m.""UserId"" AND f.""ExampleSentenceId"" = m.""ExampleSentenceId"" AND f.""ResolvedAt"" IS NULL;");

            migrationBuilder.DropTable(
                name: "SentenceFlags",
                schema: "user");
        }
    }
}
