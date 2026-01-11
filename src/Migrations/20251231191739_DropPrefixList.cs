using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class DropPrefixList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use IF EXISTS to handle both production (table exists) and fresh installs (table doesn't exist)
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""PrefixList"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrefixList",
                columns: table => new
                {
                    ServerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Prefix = table.Column<char>(type: "character(1)", nullable: false),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    SetById = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefixList", x => x.ServerId);
                });
        }
    }
}
