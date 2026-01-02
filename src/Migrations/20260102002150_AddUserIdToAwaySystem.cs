using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddUserIdToAwaySystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Clear existing away statuses since we can't reliably map UserName to UserId
            // Users will need to re-set their away status after this migration
            migrationBuilder.Sql("DELETE FROM \"AwaySystem\"");

            migrationBuilder.AddColumn<decimal>(
                name: "UserId",
                table: "AwaySystem",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserId",
                table: "AwaySystem");
        }
    }
}
