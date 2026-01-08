using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddJournalDataToWowMounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncounterName",
                table: "WowMounts",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstanceName",
                table: "WowMounts",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "JournalEncounterId",
                table: "WowMounts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "JournalInstanceId",
                table: "WowMounts",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncounterName",
                table: "WowMounts");

            migrationBuilder.DropColumn(
                name: "InstanceName",
                table: "WowMounts");

            migrationBuilder.DropColumn(
                name: "JournalEncounterId",
                table: "WowMounts");

            migrationBuilder.DropColumn(
                name: "JournalInstanceId",
                table: "WowMounts");
        }
    }
}
