namespace NinjaBotCore.Database
{
    using Microsoft.EntityFrameworkCore;
    
    public partial class NinjaBotEntities : DbContext
    {
        public NinjaBotEntities() {
            
        }
        public NinjaBotEntities(DbContextOptions<NinjaBotEntities> options) : base(options)
        {            
        }        
        public virtual DbSet<RlStat> RlStats { get; set; }
        public virtual DbSet<RlUserStat> RlUserStats { get; set; }
        public virtual DbSet<TriviaQuestion> TriviaQuestion { get; set; }
        public virtual DbSet<TriviaQuestionChoice> TriviaQuestionChoices { get; set; }
        public virtual DbSet<Note> Notes { get; set; }
        public virtual DbSet<QuestionAnswer> QuestionAnswers { get; set; }
        public virtual DbSet<ChannelOutput> ChannelOutputs { get; set; }
        public virtual DbSet<WowGuildAssociations> WowGuildAssociations { get; set; }
        public virtual DbSet<Giphy> Giphy { get; set; }
        public virtual DbSet<ServerSetting> ServerSettings { get; set; }
        public virtual DbSet<TriviaCategory> TriviaCategories { get; set; }
        public virtual DbSet<AwaySystem> AwaySystem { get; set; }
        public virtual DbSet<C8Ball> C8Ball { get; set; }
        public virtual DbSet<WowAuctions> WowAuctions { get; set; }
        public virtual DbSet<AuctionItemMapping> AuctionItemMappings { get; set; }
        public virtual DbSet<WowAuctionPrice> WowAuctionPrices { get; set; }
        public virtual DbSet<Blacklist> Blacklist { get; set; }
        public virtual DbSet<ServerGreeting> ServerGreetings { get; set; }
        public virtual DbSet<AchCategory> AchCategories { get; set; }
        public virtual DbSet<FindWowCheeve> FindWowCheeves { get; set; }
        public virtual DbSet<DiscordServer> DiscordServers { get; set; }
        public virtual DbSet<Request> Requests { get; set; }
        public virtual DbSet<WowResources> WowResources { get; set; }
        public virtual DbSet<LogMonitoring> LogMonitoring { get; set; }
        public virtual DbSet<Warnings> Warnings { get; set; }
        public virtual DbSet<CharStats> CharStats { get; set; }
        public virtual DbSet<CurrentRaidTier> CurrentRaidTier { get; set; }
        public virtual DbSet<WowMChar> WowMChar { get; set; }
        public virtual DbSet<WordList> WordList { get; set; }
        public virtual DbSet<WowClassicGuild> WowClassicGuild { get; set; }
        public virtual DbSet<WowVanillaGuild> WowVanillaGuild { get; set; }
        public virtual DbSet<WclPosted> WclPosted { get; set; }
        public virtual DbSet<WowCharAssociation> WowCharAssociation { get; set; }
        public virtual DbSet<WowGuildRosterMember> WowGuildRosterMembers { get; set; }
        public virtual DbSet<RioSearchHistory> RioSearchHistory { get; set; }
        public virtual DbSet<VoiceWatcher> VoiceWatcher { get; set; }
        public virtual DbSet<ModerationWatcher> ModerationWatcher { get; set; }
        public virtual DbSet<WowItems> WowItems { get; set; }
        public virtual DbSet<WowItemDetails> WowItemDetails { get; set; }
        public virtual DbSet<WowTokenPrices> WowTokenPrices { get; set; }
        public virtual DbSet<WowMounts> WowMounts { get; set; }
        public virtual DbSet<HousingDecor> HousingDecor { get; set; }
        public virtual DbSet<WowRealms> WowRealms { get; set; }
        public virtual DbSet<WowPlayableClass> WowPlayableClasses { get; set; }
        public virtual DbSet<WowRaces> WowRaces { get; set; }
        public virtual DbSet<WowAchievements> WowAchievements { get; set; }
        public virtual DbSet<WowAchievementCriteria> WowAchievementCriteria { get; set; }
        public virtual DbSet<WowPets> WowPets { get; set; }
        public virtual DbSet<Poll> Polls { get; set; }
        public virtual DbSet<PollOption> PollOptions { get; set; }
        public virtual DbSet<PollVote> PollVotes { get; set; }
        public virtual DbSet<ServerPollSettings> ServerPollSettings { get; set; }
        public virtual DbSet<RealmWatchSubscription> RealmWatchSubscriptions { get; set; }
        public virtual DbSet<RealmStatusCache> RealmStatusCache { get; set; }
        public virtual DbSet<ApiUsageLog> ApiUsageLogs { get; set; }
        public virtual DbSet<ItemMediaCache> ItemMediaCache { get; set; }
        public virtual DbSet<StaticDataSyncRequest> StaticDataSyncRequests { get; set; }
        public virtual DbSet<StaticDataSyncStatus> StaticDataSyncStatus { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                DatabaseConfigurator.Apply(optionsBuilder);
            }
        }
    }
}
