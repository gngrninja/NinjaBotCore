# Debugging Guide

> **Reference this doc when:** debugging interaction errors, unexpected behavior, or "it works in logs but fails for user" scenarios

## Development Workflow

**CRITICAL: The user (developer) runs the bot for testing - Claude Code NEVER starts the bot.**

1. User runs the bot via `./run-bot-dev.sh` or manual launch
2. User tests interactions in Discord
3. User provides logs, screenshots, or error descriptions
4. Claude Code analyzes, implements fixes, and updates code
5. User restarts bot to test changes

**Never use commands like `dotnet run` unless explicitly instructed by the user.**

## Check for Zombie Processes

**Always check before launching the bot:**

```bash
# Check if bot is already running
ps aux | grep -i "ninjabot\|dotnet.*NinjaBotCore" | grep -v grep

# Kill zombie processes if found
kill <PID>
```

### Symptoms of Multiple Bot Instances
- Duplicate Discord responses (one error, one success)
- Logs show perfect execution but user sees errors
- "Unknown command" errors despite early return logic working
- Different response timing (messages offset by milliseconds)

### Why This Happens
- Bot process started without TTY attachment (e.g., `nohup`, background `&`, detached terminal)
- Terminal closed but process continues running
- `ps` shows `??` in TTY column for detached processes

## Case Study: Poll System "Unknown Command" Errors

During poll system development, user reported persistent "Unknown command." errors when voting despite:
- Logs showing perfect execution with zero errors
- Early return logic working correctly in `InteractionHandler`
- Event handlers processing votes successfully
- No [CONSOLE] debug messages appearing (proving error handlers never executed)

### The Real Culprit

A zombie bot process (PID 17671) was running from Sunday at 9 PM:

```
┌─────────────────────────────────┐     ┌─────────────────────────────────┐
│      OLD INSTANCE (zombie)      │     │      NEW INSTANCE (current)     │
│    Started before fixes         │     │    Has all fixes                │
├─────────────────────────────────┤     ├─────────────────────────────────┤
│ 1. Receives poll vote           │     │ 1. Receives poll vote           │
│ 2. No early return (old code)   │     │ 2. Early return (skip)          │
│ 3. Calls ExecuteCommandAsync    │     │ 3. Event handler processes      │
│ 4. No handler found             │     │ 4. Vote recorded successfully   │
│ 5. Sends "Unknown command."     │     │ 5. Sends "Vote recorded!"       │
└─────────────────────────────────┘     └─────────────────────────────────┘
                │                                       │
                └───────────────┬───────────────────────┘
                                │
                                ▼
                    ┌───────────────────────┐
                    │   USER SEES BOTH:     │
                    │   "Unknown command."  │
                    │   "Vote recorded!"    │
                    └───────────────────────┘
```

### Lessons Learned

1. **Always check for zombie processes before testing** (`ps aux | grep`)
2. **Look for TTY=`??`** in process list (indicates detached process)
3. **When logs show perfect execution but user reports errors**, suspect multiple instances
4. **Console.WriteLine debugging** can bypass logging infrastructure for definitive proof
5. **Different message timestamps** (offset by milliseconds) indicate race condition between instances

## Interaction Error Quick Reference

| Error | Code | Meaning | Common Cause |
|-------|------|---------|--------------|
| Unknown interaction | 10062 | Token expired or already responded | Took >3 seconds to defer, or duplicate handling |
| Already acknowledged | 40060 | Multiple handlers responding | Missing duplicate prevention, multiple events firing |
| Unknown command | N/A | No handler found for interaction | Zombie process with old code, or missing handler registration |

## Debugging Interaction Flow

The codebase has logging prefixes to trace interaction flow:

1. **`[EVENT HANDLER]`** - UserInteractions received the interaction
   - Shows CustomId, timestamp, HasResponded status
   - Logs duplicate handling attempts
   - Logs defer attempts

2. **`[INTERACTION HANDLER]`** - InteractionHandler received the interaction
   - Shows CustomId, timestamp, HasResponded status
   - Logs skip decisions for modals/components

### What to Check

- **Race conditions**: Are both handlers trying to respond?
- **Timing**: Is defer happening within 3 seconds?
- **Duplicates**: Is ConcurrentDictionary preventing double-handling?
- **Routing**: Is the correct handler being called?

## Testing API Endpoints

With the bot running and API enabled:

```bash
# Health check (no auth)
curl http://localhost:5100/api/commands/health

# Get commands (requires API key)
curl -H "X-Api-Key: your-key" http://localhost:5100/api/commands

# Refresh guild roster
curl -X POST -H "X-Api-Key: your-key" -H "Content-Type: application/json" \
  -d '{"DiscordGuildId":"123456789"}' \
  http://localhost:5100/api/guilds/refresh-roster

# Add character
curl -X POST -H "X-Api-Key: your-key" -H "Content-Type: application/json" \
  -d '{"DiscordUserId":"987654321","CharacterName":"Charactername","Realm":"Realm Name","Region":"us"}' \
  http://localhost:5100/api/characters/add
```

## Running Tests

```bash
# Run all tests
dotnet test

# Run specific test class
dotnet test --filter "FullyQualifiedName~PostgresIntegrationTests"

# Run a single test
dotnet test --filter "FullyQualifiedName=NinjaBotCore.Tests.PostgresIntegrationTests.Database_ShouldConnect_WithoutErrors"
```

## Test Best Practices

- Test database operations with both success and failure scenarios
- Validate permission checks and error handling
- Test caching behavior and TTL expiration
- When adding new features, plan tests to ensure parity
