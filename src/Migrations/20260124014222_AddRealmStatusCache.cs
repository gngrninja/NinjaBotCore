using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddRealmStatusCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RealmStatusCache",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Region = table.Column<string>(type: "text", nullable: true),
                    ConnectedRealmId = table.Column<long>(type: "bigint", nullable: false),
                    RealmName = table.Column<string>(type: "text", nullable: true),
                    IsOnline = table.Column<bool>(type: "boolean", nullable: false),
                    HasQueue = table.Column<bool>(type: "boolean", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealmStatusCache", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RealmStatusCache_Region_ConnectedRealmId",
                table: "RealmStatusCache",
                columns: new[] { "Region", "ConnectedRealmId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RealmStatusCache_Region_ConnectedRealmId",
                table: "RealmStatusCache");

            migrationBuilder.DropTable(
                name: "RealmStatusCache");
        }
    }
}
