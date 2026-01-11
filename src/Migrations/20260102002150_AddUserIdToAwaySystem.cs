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
            // Use TRUNCATE for fresh installs (empty table) and DELETE for existing data
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'AwaySystem') THEN
                        DELETE FROM ""AwaySystem"";
                    END IF;
                END $$;
            ");

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
