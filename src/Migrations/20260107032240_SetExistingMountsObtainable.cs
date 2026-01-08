using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class SetExistingMountsObtainable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Set all existing mounts to be obtainable by default
            migrationBuilder.Sql("UPDATE \"WowMounts\" SET \"IsObtainable\" = true WHERE \"IsObtainable\" = false OR \"IsObtainable\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback: set all mounts back to not obtainable (this is destructive, but reversible)
            migrationBuilder.Sql("UPDATE \"WowMounts\" SET \"IsObtainable\" = false");
        }
    }
}
