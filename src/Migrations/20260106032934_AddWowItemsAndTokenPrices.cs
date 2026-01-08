using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddWowItemsAndTokenPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WowItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Quality = table.Column<int>(type: "integer", nullable: false),
                    QualityName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ItemLevel = table.Column<int>(type: "integer", nullable: false),
                    InventoryType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ItemClass = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ItemSubclass = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MediaUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsEquippable = table.Column<bool>(type: "boolean", nullable: false),
                    RequiredLevel = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourceDetail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WowTokenPrices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Region = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Price = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowTokenPrices", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WowItems");

            migrationBuilder.DropTable(
                name: "WowTokenPrices");
        }
    }
}
