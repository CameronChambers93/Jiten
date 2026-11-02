using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class AnkiNoteTypeSetups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnkiCardTargetJson",
                schema: "user",
                table: "UserSettings",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<DateTime>(
                name: "AnkiKnownWordsSyncedAt",
                schema: "user",
                table: "UserSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FieldsHash",
                schema: "user",
                table: "MinedNotes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FieldsSentenceId",
                schema: "user",
                table: "MinedNotes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FoldingInto",
                schema: "user",
                table: "MinedNotes",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AnkiNoteTypeSetups",
                schema: "user",
                columns: table => new
                {
                    AnkiNoteTypeSetupId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Filter = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FieldRolesJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    Preset = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    KnownWords = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ImportSentences = table.Column<bool>(type: "boolean", nullable: false),
                    TakeFlags = table.Column<bool>(type: "boolean", nullable: false),
                    FoldDuplicates = table.Column<bool>(type: "boolean", nullable: false),
                    UpdateMainFields = table.Column<bool>(type: "boolean", nullable: false),
                    SentenceList = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScriptVersion = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnkiNoteTypeSetups", x => x.AnkiNoteTypeSetupId);
                    table.ForeignKey(
                        name: "FK_AnkiNoteTypeSetups_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnkiNoteTypeSetups_UserId_ModelName",
                schema: "user",
                table: "AnkiNoteTypeSetups",
                columns: new[] { "UserId", "ModelName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnkiNoteTypeSetups",
                schema: "user");

            migrationBuilder.DropColumn(
                name: "AnkiCardTargetJson",
                schema: "user",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "AnkiKnownWordsSyncedAt",
                schema: "user",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "FieldsHash",
                schema: "user",
                table: "MinedNotes");

            migrationBuilder.DropColumn(
                name: "FieldsSentenceId",
                schema: "user",
                table: "MinedNotes");

            migrationBuilder.DropColumn(
                name: "FoldingInto",
                schema: "user",
                table: "MinedNotes");
        }
    }
}
