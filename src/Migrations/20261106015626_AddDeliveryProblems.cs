using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryProblems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SetById",
                table: "LogMonitoring",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SetByName",
                table: "LogMonitoring",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeliveryProblems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: false),
                    Feature = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    Problem = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DiscordCode = table.Column<int>(type: "integer", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Failures = table.Column<int>(type: "integer", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NotifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NotifiedUserId = table.Column<long>(type: "bigint", nullable: true),
                    NotifiedRole = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptRole = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastAttemptResult = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryProblems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryProblems_LastSeenAt",
                table: "DeliveryProblems",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryProblems_ServerId_Feature_ChannelId",
                table: "DeliveryProblems",
                columns: new[] { "ServerId", "Feature", "ChannelId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeliveryProblems");

            migrationBuilder.DropColumn(
                name: "SetById",
                table: "LogMonitoring");

            migrationBuilder.DropColumn(
                name: "SetByName",
                table: "LogMonitoring");
        }
    }
}
