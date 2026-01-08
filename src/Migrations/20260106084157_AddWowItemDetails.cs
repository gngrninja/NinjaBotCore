using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddWowItemDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WowItemDetails",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    SetId = table.Column<long>(type: "bigint", nullable: true),
                    SetName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SetEffects = table.Column<string>(type: "text", nullable: true),
                    BaseStats = table.Column<string>(type: "text", nullable: true),
                    SpellEffects = table.Column<string>(type: "text", nullable: true),
                    SocketCount = table.Column<int>(type: "integer", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowItemDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WowItemDetails_WowItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "WowItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WowItemDetails_ItemId",
                table: "WowItemDetails",
                column: "ItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WowItemDetails");
        }
    }
}
