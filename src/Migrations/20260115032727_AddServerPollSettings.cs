using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddServerPollSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServerPollSettings",
                columns: table => new
                {
                    DiscordGuildId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ResultsChannelId = table.Column<long>(type: "bigint", nullable: true),
                    MentionVotersOnClose = table.Column<bool>(type: "boolean", nullable: false),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    SetByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerPollSettings", x => x.DiscordGuildId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServerPollSettings");
        }
    }
}
