using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddPartUsersToggle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PartUsers",
                table: "ServerGreetings",
                type: "boolean",
                nullable: true);

            // Migrate existing data: if GreetUsers was set, copy to PartUsers
            // This preserves current behavior where GreetUsers controlled both
            migrationBuilder.Sql(
                @"UPDATE ""ServerGreetings"" SET ""PartUsers"" = ""GreetUsers"" WHERE ""GreetUsers"" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PartUsers",
                table: "ServerGreetings");
        }
    }
}
