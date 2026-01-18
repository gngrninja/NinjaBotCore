# Code Patterns & Conventions

> **Reference this doc when:** working with repositories, dependency injection, or module organization

## Repository Pattern

Generic repository in `src/Repositories/Repository.cs` supports two construction patterns:

### Pattern #1: Standalone with IServiceScopeFactory (for singletons)

```csharp
public Repository(IServiceScopeFactory serviceScopeFactory)
```
- Internally manages DbContext lifecycle via lazy initialization
- Thread-safe with locking mechanism
- Used by long-lived services (WordFilterService, DiscordServerTrackingService)

### Pattern #2: DI-Resolved with NinjaBotEntities (for scoped contexts)

```csharp
[ActivatorUtilitiesConstructor]
public Repository(NinjaBotEntities context)
```
- Externally-managed context from DI container
- Preferred for interaction modules and scoped services
- Marked with `[ActivatorUtilitiesConstructor]` for DI resolution

## IQueryable Support

Repository exposes `IQueryable<TEntity> Query` property for advanced LINQ operations:
- Enables database-side filtering with `Contains()`, `Where()`, `OrderBy()`, etc.
- Use for complex queries that benefit from SQL translation
- Example: `.Where(c => c.UserId.HasValue && userIdList.Contains(c.UserId.Value))`

## UnitOfWork Pattern

`IUnitOfWork` provides scoped database access with multiple repositories:

```csharp
public interface IUnitOfWork : IAsyncDisposable, IDisposable
{
    IRepository<TEntity> Repository<TEntity>() where TEntity : class;
    Task<int> SaveChangesAsync();
    NinjaBotEntities Context { get; }
}
```

**IMPORTANT:** The method is `Repository<T>()`, **NOT** `GetRepository<T>()`

### Usage Example

```csharp
// CORRECT:
var result = await WithScopedUnitOfWorkAsync(async uow =>
{
    var pollRepo = uow.Repository<Poll>();
    var voteRepo = uow.Repository<PollVote>();

    // Perform operations
    await pollRepo.AddAsync(entity);
    await uow.SaveChangesAsync();

    return result;
});

// WRONG:
var pollRepo = uow.GetRepository<Poll>();  // Method does not exist!
```

## WoW Command Module Organization

Large interaction modules should be split into separate classes by feature domain, NOT partial classes.

### Current Structure (`src/Modules/Interactions/Wow/`)
- `WowInteract.cs`: Main WoW commands (character lookups, armory, logs, RIO, guild commands)
- `MountCommands.cs`: Mount collection commands with pagination/filtering (~5 dependencies)
- `HousingCommands.cs`: Housing feature commands (~2 dependencies)

### Pattern for Extracting Commands

```csharp
// GOOD: Separate class with minimal dependencies
public class MountCommands : NinjaBotBaseModule
{
    private readonly ILogger<MountCommands> _logger;
    private readonly WowApi _wowApi;
    private readonly WowCacheService _wowCache;
    // Only inject what you need

    public MountCommands(
        IServiceScopeFactory scopeFactory,  // Required by base class
        ILogger<MountCommands> logger,
        WowApi wowApi,
        WowCacheService wowCache)
        : base(scopeFactory)
    {
        _logger = logger;
        _wowApi = wowApi;
        _wowCache = wowCache;
    }

    [SlashCommand("mounts-needed", "Check which mounts you're missing")]
    public async Task MountsNeeded(...) { }

    // Component handlers for this feature's buttons/dropdowns
    [ComponentInteraction("mount_filter~*")]
    public async Task HandleMountFilter(...) { }
}
```

### Why NOT Partial Classes
- Partial classes share ALL dependencies (defeats single responsibility)
- No reduction in coupling or complexity
- Makes testing harder (must mock all dependencies)
- Proper separation allows independent testing and maintenance

## WoW Realm Name vs Slug Convention

**CRITICAL:** The Blizzard WoW API requires realm **slugs** (e.g., "sisters-of-elune") in URLs, not display names (e.g., "Sisters of Elune").

### Database Fields in `WowGuildAssociations`
- `WowRealm`: Display name for showing to users (e.g., "Sisters of Elune")
- `LocalRealmSlug`: URL-safe slug for API calls (e.g., "sisters-of-elune")

### In `GuildObject`
- `realmName`: Display name (from `WowRealm`)
- `realmSlug`: API slug (from `LocalRealmSlug`)

### When Making WoW API Calls

```csharp
// CORRECT - use realmSlug for API calls
var effectiveRealmSlug = !string.IsNullOrEmpty(guildObject.realmSlug)
    ? guildObject.realmSlug
    : guildObject.realmName?.ToLower().Replace(" ", "-").Replace("'", "");

var guildie = await _wowApi.GetCharFromGuildAsync(
    charName,
    effectiveRealmSlug,  // Use slug!
    guildObject.guildName,
    guildObject.regionName);

// WRONG - will fail for multi-word realms
var guildie = await _wowApi.GetCharFromGuildAsync(
    charName,
    guildObject.realmName,  // "Sisters of Elune" causes 404!
    ...);
```

## Caching Strategy

- `IMemoryCache` with size limits configured in DI (1000 items max)
- 15-minute TTL for WoW API responses (realms, characters, guilds)
- Word filter cache per server (15-minute TTL)
- Help content cached to disk (`help-commands.json`) + in-memory

## Extension Methods

`src/Services/NinjaExtensions.cs` provides Unix timestamp conversion:
- `UnixTimeStampToDateTime(long)`: Milliseconds to UTC DateTime
- `UnixTimeStampToDateTimeSeconds(long)`: Seconds to UTC DateTime
- Legacy `uint` variants for backwards compatibility
