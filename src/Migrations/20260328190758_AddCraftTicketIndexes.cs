using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddCraftTicketIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CraftTickets_GuildId_RequesterId_Status",
                table: "CraftTickets",
                columns: new[] { "GuildId", "RequesterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CraftTickets_GuildId_Status",
                table: "CraftTickets",
                columns: new[] { "GuildId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CraftTickets_Status_ExpiresAt",
                table: "CraftTickets",
                columns: new[] { "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CraftTickets_GuildId_RequesterId_Status",
                table: "CraftTickets");

            migrationBuilder.DropIndex(
                name: "IX_CraftTickets_GuildId_Status",
                table: "CraftTickets");

            migrationBuilder.DropIndex(
                name: "IX_CraftTickets_Status_ExpiresAt",
                table: "CraftTickets");
        }
    }
}
