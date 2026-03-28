using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddCraftLinkTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CraftTickets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BlizzardItemId = table.Column<long>(type: "bigint", nullable: true),
                    ItemIconUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CraftedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RequesterId = table.Column<long>(type: "bigint", nullable: false),
                    RequesterName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CrafterId = table.Column<long>(type: "bigint", nullable: true),
                    CrafterName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    ThreadId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CraftTickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerCraftSettings",
                columns: table => new
                {
                    DiscordGuildId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CraftChannelId = table.Column<long>(type: "bigint", nullable: true),
                    MaxOpenTicketsPerUser = table.Column<int>(type: "integer", nullable: false),
                    TicketExpirationHours = table.Column<int>(type: "integer", nullable: false),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    SetByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerCraftSettings", x => x.DiscordGuildId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CraftTickets");

            migrationBuilder.DropTable(
                name: "ServerCraftSettings");
        }
    }
}
