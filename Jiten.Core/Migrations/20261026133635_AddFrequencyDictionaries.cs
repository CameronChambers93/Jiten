using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFrequencyDictionaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FrequencyDictionaries",
                schema: "jiten",
                columns: table => new
                {
                    FrequencyDictionaryId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Revision = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    RankedForms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FrequencyDictionaries", x => x.FrequencyDictionaryId);
                });

            migrationBuilder.CreateTable(
                name: "FrequencyDictionaryRanks",
                schema: "jiten",
                columns: table => new
                {
                    FrequencyDictionaryId = table.Column<int>(type: "integer", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    ReadingIndex = table.Column<short>(type: "smallint", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FrequencyDictionaryRanks", x => new { x.FrequencyDictionaryId, x.WordId, x.ReadingIndex });
                    table.ForeignKey(
                        name: "FK_FrequencyDictionaryRanks_FrequencyDictionaries_FrequencyDic~",
                        column: x => x.FrequencyDictionaryId,
                        principalSchema: "jiten",
                        principalTable: "FrequencyDictionaries",
                        principalColumn: "FrequencyDictionaryId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FrequencyDictionaryRanks",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "FrequencyDictionaries",
                schema: "jiten");
        }
    }
}
