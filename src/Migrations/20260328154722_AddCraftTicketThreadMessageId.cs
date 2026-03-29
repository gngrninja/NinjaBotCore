using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddCraftTicketThreadMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ThreadMessageId",
                table: "CraftTickets",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThreadMessageId",
                table: "CraftTickets");
        }
    }
}
