using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class ModerationWatcher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModerationWatcher",
                columns: table => new
                {
                    DiscordGuildId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelId = table.Column<long>(type: "bigint", nullable: true),
                    ChannelName = table.Column<string>(type: "text", nullable: true),
                    WatchVoice = table.Column<bool>(type: "boolean", nullable: true),
                    WatchMessages = table.Column<bool>(type: "boolean", nullable: true),
                    WatchRoles = table.Column<bool>(type: "boolean", nullable: true),
                    WatchBans = table.Column<bool>(type: "boolean", nullable: true),
                    WatchNicknames = table.Column<bool>(type: "boolean", nullable: true),
                    WatchProfiles = table.Column<bool>(type: "boolean", nullable: true),
                    WatchAudit = table.Column<bool>(type: "boolean", nullable: true),
                    WatchServer = table.Column<bool>(type: "boolean", nullable: true),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    SetByName = table.Column<string>(type: "text", nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationWatcher", x => x.DiscordGuildId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModerationWatcher");
        }
    }
}
