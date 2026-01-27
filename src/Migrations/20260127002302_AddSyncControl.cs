using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StaticDataSyncRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SyncType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    RequestSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ItemsProcessed = table.Column<int>(type: "integer", nullable: true),
                    ItemsSkipped = table.Column<int>(type: "integer", nullable: true),
                    ItemsFailed = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaticDataSyncRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StaticDataSyncStatus",
                columns: table => new
                {
                    SyncType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LastSyncStarted = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncCompleted = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncItemCount = table.Column<int>(type: "integer", nullable: true),
                    TotalItemsInDatabase = table.Column<int>(type: "integer", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    NextScheduledSync = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaticDataSyncStatus", x => x.SyncType);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaticDataSyncRequests");

            migrationBuilder.DropTable(
                name: "StaticDataSyncStatus");
        }
    }
}
