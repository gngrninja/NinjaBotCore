using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AchCategories",
                columns: table => new
                {
                    CatId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CatName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AchCategories", x => x.CatId);
                });

            migrationBuilder.CreateTable(
                name: "AuctionItemMappings",
                columns: table => new
                {
                    MapId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemId = table.Column<long>(type: "bigint", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuctionItemMappings", x => x.MapId);
                });

            migrationBuilder.CreateTable(
                name: "AwaySystem",
                columns: table => new
                {
                    AwayId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserName = table.Column<string>(type: "text", nullable: true),
                    Message = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<bool>(type: "boolean", nullable: true),
                    TimeAway = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AwaySystem", x => x.AwayId);
                });

            migrationBuilder.CreateTable(
                name: "Blacklist",
                columns: table => new
                {
                    BlacklistId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscordUserId = table.Column<long>(type: "bigint", nullable: true),
                    DiscordUserName = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    WhenBlacklisted = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Blacklist", x => x.BlacklistId);
                });

            migrationBuilder.CreateTable(
                name: "C8Ball",
                columns: table => new
                {
                    AnswerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Answer = table.Column<string>(type: "text", nullable: true),
                    Color = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_C8Ball", x => x.AnswerId);
                });

            migrationBuilder.CreateTable(
                name: "ChannelOutputs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    ServerId = table.Column<long>(type: "bigint", nullable: true),
                    ChannelName = table.Column<string>(type: "text", nullable: true),
                    ChannelId = table.Column<long>(type: "bigint", nullable: true),
                    SetByName = table.Column<string>(type: "text", nullable: true),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    SetTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOutputs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CharStats",
                columns: table => new
                {
                    CharStatId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CharName = table.Column<string>(type: "text", nullable: true),
                    GuildName = table.Column<string>(type: "text", nullable: true),
                    RealmName = table.Column<string>(type: "text", nullable: true),
                    LastModified = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ElixerConsumed = table.Column<string>(type: "text", nullable: true),
                    Quantity = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharStats", x => x.CharStatId);
                });

            migrationBuilder.CreateTable(
                name: "CurrentRaidTier",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WclZoneId = table.Column<long>(type: "bigint", nullable: false),
                    RaidName = table.Column<string>(type: "text", nullable: true),
                    Partition = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrentRaidTier", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscordServers",
                columns: table => new
                {
                    ServerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    OwnerId = table.Column<long>(type: "bigint", nullable: true),
                    OwnerName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordServers", x => x.ServerId);
                });

            migrationBuilder.CreateTable(
                name: "Giphy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    ServerId = table.Column<long>(type: "bigint", nullable: true),
                    GiphyEnabled = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Giphy", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogMonitoring",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: false),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    ChannelName = table.Column<string>(type: "text", nullable: true),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    MonitorLogs = table.Column<bool>(type: "boolean", nullable: false),
                    WatchLog = table.Column<bool>(type: "boolean", nullable: false),
                    RetailReportId = table.Column<string>(type: "text", nullable: true),
                    ClassicReportId = table.Column<string>(type: "text", nullable: true),
                    VanillaReportId = table.Column<string>(type: "text", nullable: true),
                    ReportId = table.Column<string>(type: "text", nullable: true),
                    LatestLog = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LatestLogClassic = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LatestLogVanilla = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LatestLogRetail = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogMonitoring", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Note1 = table.Column<string>(type: "text", nullable: true),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    ServerId = table.Column<long>(type: "bigint", nullable: true),
                    SetBy = table.Column<string>(type: "text", nullable: true),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrefixList",
                columns: table => new
                {
                    ServerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    Prefix = table.Column<char>(type: "character(1)", nullable: false),
                    SetById = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefixList", x => x.ServerId);
                });

            migrationBuilder.CreateTable(
                name: "QuestionAnswers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscordUserName = table.Column<string>(type: "text", nullable: true),
                    QuestionId = table.Column<long>(type: "bigint", nullable: true),
                    ChoiceId = table.Column<long>(type: "bigint", nullable: true),
                    IsRight = table.Column<bool>(type: "boolean", nullable: true),
                    AnswerTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    test = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestionAnswers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Requests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: true),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    ChannelName = table.Column<string>(type: "text", nullable: true),
                    Command = table.Column<string>(type: "text", nullable: true),
                    Parameters = table.Column<string>(type: "text", nullable: true),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    ServerID = table.Column<long>(type: "bigint", nullable: false),
                    RequestTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RlStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SteamID = table.Column<long>(type: "bigint", nullable: true),
                    DiscordUserID = table.Column<long>(type: "bigint", nullable: true),
                    DiscordUserName = table.Column<string>(type: "text", nullable: true),
                    RlPlayerName = table.Column<string>(type: "text", nullable: true),
                    Platform = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RlStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RlUserStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SteamID = table.Column<long>(type: "bigint", nullable: true),
                    RankedSolo = table.Column<string>(type: "text", nullable: true),
                    Ranked2v2 = table.Column<string>(type: "text", nullable: true),
                    RankedDuel = table.Column<string>(type: "text", nullable: true),
                    Ranked3v3 = table.Column<string>(type: "text", nullable: true),
                    Unranked = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RlUserStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerGreetings",
                columns: table => new
                {
                    DiscordGuildId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GreetUsers = table.Column<bool>(type: "boolean", nullable: true),
                    Greeting = table.Column<string>(type: "text", nullable: true),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    SetByName = table.Column<string>(type: "text", nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PartingMessage = table.Column<string>(type: "text", nullable: true),
                    GreetingChannelId = table.Column<long>(type: "bigint", nullable: true),
                    PartingChannelId = table.Column<long>(type: "bigint", nullable: true),
                    GreetingChannelName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerGreetings", x => x.DiscordGuildId);
                });

            migrationBuilder.CreateTable(
                name: "ServerSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    ServerId = table.Column<long>(type: "bigint", nullable: true),
                    Announcements = table.Column<bool>(type: "boolean", nullable: true),
                    OutputChannel = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TriviaCategories",
                columns: table => new
                {
                    CategoryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriviaCategories", x => x.CategoryId);
                });

            migrationBuilder.CreateTable(
                name: "Warnings",
                columns: table => new
                {
                    Warnid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: false),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    UserWarnedId = table.Column<long>(type: "bigint", nullable: false),
                    UserWarnedName = table.Column<string>(type: "text", nullable: true),
                    IssuerId = table.Column<long>(type: "bigint", nullable: false),
                    IssuerName = table.Column<string>(type: "text", nullable: true),
                    TimeIssued = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NumWarnings = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warnings", x => x.Warnid);
                });

            migrationBuilder.CreateTable(
                name: "WclPosted",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: false),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    ChannelName = table.Column<string>(type: "text", nullable: true),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    ReportId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WclPosted", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WordList",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: false),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    Word = table.Column<string>(type: "text", nullable: true),
                    SetById = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordList", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WowAuctionPrices",
                columns: table => new
                {
                    AuctionPriceId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AuctionItemId = table.Column<long>(type: "bigint", nullable: true),
                    AuctionRealm = table.Column<string>(type: "text", nullable: true),
                    MinPrice = table.Column<long>(type: "bigint", nullable: true),
                    AvgPrice = table.Column<long>(type: "bigint", nullable: true),
                    MaxPrice = table.Column<long>(type: "bigint", nullable: true),
                    Seen = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowAuctionPrices", x => x.AuctionPriceId);
                });

            migrationBuilder.CreateTable(
                name: "WowAuctions",
                columns: table => new
                {
                    AuctionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RealmName = table.Column<string>(type: "text", nullable: true),
                    RealmSlug = table.Column<string>(type: "text", nullable: true),
                    WowAuctionId = table.Column<long>(type: "bigint", nullable: true),
                    AuctionItemId = table.Column<long>(type: "bigint", nullable: true),
                    AuctionOwner = table.Column<string>(type: "text", nullable: true),
                    AuctionOwnerRealm = table.Column<string>(type: "text", nullable: true),
                    AuctionBid = table.Column<long>(type: "bigint", nullable: true),
                    AuctionBuyout = table.Column<long>(type: "bigint", nullable: true),
                    AuctionQuantity = table.Column<long>(type: "bigint", nullable: true),
                    AuctionTimeLeft = table.Column<string>(type: "text", nullable: true),
                    AuctionRand = table.Column<long>(type: "bigint", nullable: true),
                    AuctionSeed = table.Column<long>(type: "bigint", nullable: true),
                    AuctionContext = table.Column<long>(type: "bigint", nullable: true),
                    DateModified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowAuctions", x => x.AuctionId);
                });

            migrationBuilder.CreateTable(
                name: "WowCharAssociation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    IsMain = table.Column<bool>(type: "boolean", nullable: false),
                    CharName = table.Column<string>(type: "text", nullable: true),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    WowGuild = table.Column<string>(type: "text", nullable: true),
                    WowRealm = table.Column<string>(type: "text", nullable: true),
                    WowRegion = table.Column<string>(type: "text", nullable: true),
                    LocalRealmSlug = table.Column<string>(type: "text", nullable: true),
                    Locale = table.Column<string>(type: "text", nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowCharAssociation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WowClassicGuild",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: true),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    WowGuild = table.Column<string>(type: "text", nullable: true),
                    WowRealm = table.Column<string>(type: "text", nullable: true),
                    WowRegion = table.Column<string>(type: "text", nullable: true),
                    SetBy = table.Column<string>(type: "text", nullable: true),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowClassicGuild", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WowGuildAssociations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: true),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    WowGuild = table.Column<string>(type: "text", nullable: true),
                    WowRealm = table.Column<string>(type: "text", nullable: true),
                    WowRegion = table.Column<string>(type: "text", nullable: true),
                    LocalRealmSlug = table.Column<string>(type: "text", nullable: true),
                    Locale = table.Column<string>(type: "text", nullable: true),
                    SetBy = table.Column<string>(type: "text", nullable: true),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowGuildAssociations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WowMChar",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: false),
                    DiscordUserId = table.Column<long>(type: "bigint", nullable: false),
                    CharName = table.Column<string>(type: "text", nullable: true),
                    ClassName = table.Column<string>(type: "text", nullable: true),
                    ItemLevel = table.Column<long>(type: "bigint", nullable: false),
                    Traits = table.Column<long>(type: "bigint", nullable: false),
                    MainSpec = table.Column<string>(type: "text", nullable: true),
                    OffSpec = table.Column<string>(type: "text", nullable: true),
                    IsMain = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowMChar", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WowResources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: true),
                    ClassName = table.Column<string>(type: "text", nullable: true),
                    Specialization = table.Column<string>(type: "text", nullable: true),
                    Resource = table.Column<string>(type: "text", nullable: true),
                    ResourceDescription = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowResources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WowVanillaGuild",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: true),
                    ServerName = table.Column<string>(type: "text", nullable: true),
                    WowGuild = table.Column<string>(type: "text", nullable: true),
                    WowRealm = table.Column<string>(type: "text", nullable: true),
                    WowRegion = table.Column<string>(type: "text", nullable: true),
                    SetBy = table.Column<string>(type: "text", nullable: true),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowVanillaGuild", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FindWowCheeves",
                columns: table => new
                {
                    AchId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AchCategoryCatId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FindWowCheeves", x => x.AchId);
                    table.ForeignKey(
                        name: "FK_FindWowCheeves_AchCategories_AchCategoryCatId",
                        column: x => x.AchCategoryCatId,
                        principalTable: "AchCategories",
                        principalColumn: "CatId");
                });

            migrationBuilder.CreateTable(
                name: "TriviaQuestion",
                columns: table => new
                {
                    QuestionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Question = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: true),
                    Category = table.Column<long>(type: "bigint", nullable: true),
                    TriviaCategoryCategoryId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriviaQuestion", x => x.QuestionId);
                    table.ForeignKey(
                        name: "FK_TriviaQuestion_TriviaCategories_TriviaCategoryCategoryId",
                        column: x => x.TriviaCategoryCategoryId,
                        principalTable: "TriviaCategories",
                        principalColumn: "CategoryId");
                });

            migrationBuilder.CreateTable(
                name: "TriviaQuestionChoices",
                columns: table => new
                {
                    ChoiceId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QuestionId = table.Column<long>(type: "bigint", nullable: true),
                    IsRightChoice = table.Column<bool>(type: "boolean", nullable: true),
                    Choice = table.Column<string>(type: "text", nullable: true),
                    TriviaQuestionQuestionId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriviaQuestionChoices", x => x.ChoiceId);
                    table.ForeignKey(
                        name: "FK_TriviaQuestionChoices_TriviaQuestion_TriviaQuestionQuestion~",
                        column: x => x.TriviaQuestionQuestionId,
                        principalTable: "TriviaQuestion",
                        principalColumn: "QuestionId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_FindWowCheeves_AchCategoryCatId",
                table: "FindWowCheeves",
                column: "AchCategoryCatId");

            migrationBuilder.CreateIndex(
                name: "IX_TriviaQuestion_TriviaCategoryCategoryId",
                table: "TriviaQuestion",
                column: "TriviaCategoryCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_TriviaQuestionChoices_TriviaQuestionQuestionId",
                table: "TriviaQuestionChoices",
                column: "TriviaQuestionQuestionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuctionItemMappings");

            migrationBuilder.DropTable(
                name: "AwaySystem");

            migrationBuilder.DropTable(
                name: "Blacklist");

            migrationBuilder.DropTable(
                name: "C8Ball");

            migrationBuilder.DropTable(
                name: "ChannelOutputs");

            migrationBuilder.DropTable(
                name: "CharStats");

            migrationBuilder.DropTable(
                name: "CurrentRaidTier");

            migrationBuilder.DropTable(
                name: "DiscordServers");

            migrationBuilder.DropTable(
                name: "FindWowCheeves");

            migrationBuilder.DropTable(
                name: "Giphy");

            migrationBuilder.DropTable(
                name: "LogMonitoring");

            migrationBuilder.DropTable(
                name: "Notes");

            migrationBuilder.DropTable(
                name: "PrefixList");

            migrationBuilder.DropTable(
                name: "QuestionAnswers");

            migrationBuilder.DropTable(
                name: "Requests");

            migrationBuilder.DropTable(
                name: "RlStats");

            migrationBuilder.DropTable(
                name: "RlUserStats");

            migrationBuilder.DropTable(
                name: "ServerGreetings");

            migrationBuilder.DropTable(
                name: "ServerSettings");

            migrationBuilder.DropTable(
                name: "TriviaQuestionChoices");

            migrationBuilder.DropTable(
                name: "Warnings");

            migrationBuilder.DropTable(
                name: "WclPosted");

            migrationBuilder.DropTable(
                name: "WordList");

            migrationBuilder.DropTable(
                name: "WowAuctionPrices");

            migrationBuilder.DropTable(
                name: "WowAuctions");

            migrationBuilder.DropTable(
                name: "WowCharAssociation");

            migrationBuilder.DropTable(
                name: "WowClassicGuild");

            migrationBuilder.DropTable(
                name: "WowGuildAssociations");

            migrationBuilder.DropTable(
                name: "WowMChar");

            migrationBuilder.DropTable(
                name: "WowResources");

            migrationBuilder.DropTable(
                name: "WowVanillaGuild");

            migrationBuilder.DropTable(
                name: "AchCategories");

            migrationBuilder.DropTable(
                name: "TriviaQuestion");

            migrationBuilder.DropTable(
                name: "TriviaCategories");
        }
    }
}
