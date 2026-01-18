# Modal and Component Interaction Handling

> **Reference this doc when:** implementing new modals, buttons, or debugging interaction errors (10062, 40060)

## Overview

All modal and poll component handlers use the **event-based pattern** via specific event subscriptions. This provides immediate response timing and avoids Discord.Net's InteractionService routing delays that can cause "Unknown interaction" (10062) errors.

## Shared Modal Constants

All modal and component custom IDs are defined in `src/Common/ModalConstants.cs` to keep `InteractionHandler` (which skips them) and `UserInteractions` (which handles them) in sync:

```
                         ┌──────────────────────────────┐
                         │   Common/ModalConstants.cs   │
                         ├──────────────────────────────┤
                         │ LegacyModals = [             │
                         │   "joining_message",         │
                         │   "parting_message",         │
                         │   "discord_server_note"      │
                         │ ]                            │
                         │ PollModals = [               │
                         │   "poll_create_modal"        │
                         │ ]                            │
                         │ PollVotePrefix = "poll_vote~"│
                         │ PollClosePrefix = "poll_close~"│
                         └──────────────┬───────────────┘
                                        │
              ┌─────────────────────────┼─────────────────────────┐
              │                         │                         │
              ▼                         ▼                         ▼
┌─────────────────────┐   ┌─────────────────────┐   ┌─────────────────────┐
│ InteractionHandler  │   │  UserInteractions   │   │   PollCommands +    │
│ SKIPS these IDs     │   │ HANDLES these IDs   │   │ CommandsApiService  │
│ (early return)      │   │ (event handlers)    │   │ BUILDS buttons with │
│                     │   │                     │   │ these prefixes      │
└─────────────────────┘   └─────────────────────┘   └─────────────────────┘
```

**Why constants instead of inline arrays?**
- Single source of truth - add a new modal in ONE place
- Prevents silent bugs from forgetting to update one of the files
- Makes the coordination between InteractionHandler and UserInteractions explicit

## Event-Based Handler Pattern

`UserInteraction` handlers:
- Handle modals in `ModalConstants.LegacyModals` and `ModalConstants.PollModals`
- Handle components prefixed with `ModalConstants.PollVotePrefix` or `ModalConstants.PollClosePrefix`
- Subscribe to **specific events** to avoid conflicts with `InteractionHandler`:
  - `ModalSubmitted` event for modals → `HandleModalSubmitted`
  - `ButtonExecuted` event for poll buttons → `HandleButtonExecuted`
- Defer the interaction **immediately** upon receipt (critical for 3-second timeout)
- Use ConcurrentDictionary for duplicate handling prevention (sharded client safety)

## Why Not Discord.Interactions Framework?

Why we don't use `[ModalInteraction]` / `[ComponentInteraction]` attributes:
- Discord.Net's InteractionService introduces routing delays (10-15ms observed)
- By the time framework handler executes and tries to defer, interaction may expire
- Event-based approach responds faster and more reliably
- **CRITICAL:** Must use specific events (`ModalSubmitted`, `ButtonExecuted`) NOT generic `InteractionCreated`
  - `InteractionCreated` fires for ALL interactions including slash commands
  - Using it in UserInteractions conflicts with `InteractionHandler` causing "Interaction already acknowledged" errors
- Note: `[ComponentInteraction]` doesn't work inside `[Group]` classes anyway

## How to Implement a New Modal

### Step 1: Present the modal from a slash command

```csharp
[SlashCommand("create", "Create a poll")]
public async Task CreateCommand()
{
    // Option A: Using IModal class (cleaner)
    await Context.Interaction.RespondWithModalAsync<PollCreationModal>("poll_create_modal");

    // Option B: Using ModalBuilder (more flexible)
    var mb = new ModalBuilder()
        .WithTitle("Create a Poll")
        .WithCustomId("poll_create_modal")
        .AddTextInput("Question", "poll_question", placeholder: "What would you like to ask?")
        .AddTextInput("Duration", "poll_duration", required: false);
    await Context.Interaction.RespondWithModalAsync(mb.Build());
}
```

### Step 2: Add the modal CustomId to `ModalConstants.cs`

```csharp
// In src/Common/ModalConstants.cs
public static readonly string[] PollModals = new[]
{
    "poll_create_modal",
    "your_new_modal"  // Add here - both InteractionHandler and UserInteractions will see it
};
```

The routing in `UserInteractions.HandleModalSubmitted` automatically uses these constants:
```csharp
// Skip modals that aren't handled here
if (!ModalConstants.LegacyModals.Contains(customId) &&
    !ModalConstants.PollModals.Contains(customId))
{
    return;
}
```

### Step 3: Add handler in UserInteractions

```csharp
switch (customId)
{
    case "poll_create_modal":
        await HandlePollModal(modal, components);
        break;
    case "your_new_modal":
        await HandleYourNewModal(modal, components);
        break;
}

private async Task HandlePollModal(SocketModal modal, List<SocketMessageComponentData> components)
{
    // Already deferred by HandleModal - safe to use FollowupAsync
    var question = components.First(x => x.CustomId == "poll_question").Value?.Trim();
    var duration = components.FirstOrDefault(x => x.CustomId == "poll_duration")?.Value?.Trim();

    // Process modal data and respond
    await modal.FollowupAsync("Poll created!", ephemeral: true);
}
```

## Interaction Routing Flow

1. Discord event fires → both `InteractionHandler` and `UserInteractions` receive it
2. `InteractionHandler` checks `ModalConstants.LegacyModals`, `ModalConstants.PollModals`, and prefixes
   - If match found → early return (skip processing, let UserInteractions handle it)
   - If no match → route to slash command handler
3. `UserInteractions` event handler (`HandleModalSubmitted` or `HandleButtonExecuted`) fires
4. Checks if CustomId is in `ModalConstants` arrays or starts with `ModalConstants.*Prefix`
   - If no match → return (not our concern)
   - If match → continue processing
5. Duplicate prevention check via ConcurrentDictionary (sharded client safety)
6. Defers the interaction immediately (before any processing)
7. Routes to appropriate handler method via switch statement or component type check
8. Handler processes data and uses FollowupAsync to respond
9. Cleanup: removes interaction ID from tracking dictionary after 1 second

## Common Interaction Errors

| Error | Meaning | Fix |
|-------|---------|-----|
| **10062 (Unknown interaction)** | Token expired (>3 seconds) or already responded | Use event-based pattern with immediate defer |
| **40060 (Already acknowledged)** | Multiple handlers responding | ConcurrentDictionary duplicate prevention |
| **Handler not found** | Component inside `[Group]` class | Discord.Net limitation - use event handlers |
| **Intermittent failures** | Race condition between events | Use single specific event, not multiple |

## Debugging

Logging prefixes help trace interaction flow:
- `[EVENT HANDLER]` - UserInteractions received the interaction
- `[INTERACTION HANDLER]` - InteractionHandler received the interaction

Check for:
- Race conditions or duplicate handling from sharded client
- Timing issues (Discord requires response within 3 seconds)
- Double-handling (interaction already responded to)
- Routing failures (handler not being called)
