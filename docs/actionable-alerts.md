# Actionable alerts

When Discord refuses a post a server set up, the bot keeps a record so the bot owner can review it on the website's admin page (`/admin/alerts`) and decide whether to tell the server. Nothing here acts on its own: no DM is sent and no feature is turned off until the owner clicks.

## What is recorded

| Feature | Key | Recorded by | Needs in the channel |
| --- | --- | --- | --- |
| New log posts (`/watchlogs`) | `new_log` | Helpers | View Channel, Send Messages, Embed Links |
| Welcome messages | `welcome` | Bot | View Channel, Send Messages, Embed Links |
| Leave messages | `leave` | Bot | View Channel, Send Messages, Embed Links |
| Moderation log (`/watch`) | `moderation_log` | Bot | View Channel, Send Messages, Embed Links |
| Realm status alerts | `realm_alert` | Helpers | View Channel, Send Messages, Embed Links |
| Word filter | `word_filter` | Bot | View Channel, Manage Messages |

**Problems recorded:**

| Problem | Discord response |
| --- | --- |
| `channel_missing` | 10003, or the channel is gone from the server |
| `no_access` | 50001 |
| `missing_permissions` | 50013 |

**Table:** `DeliveryProblems` holds one row per server, feature and channel.

**Throttling:** each process writes a failing channel at most once an hour and counts the failures in between.

**Resolving:** the next post that works resolves the problem (`delivered`). Breaking again later opens it afresh, clearing any earlier dismissal or DM record.

## The owner's actions

All POSTs need the owner's Discord ID as `user_id`, checked against the bot owners.

- **List:** `GET /api/admin/delivery-problems` returns unresolved problems plus problems resolved in the last 14 days, up to 500. Each one includes:
  - the live view: bot still in the server, channel still there, missing permissions, `fixed_now`;
  - who set the feature up (New log falls back to whoever ran `/setguild`);
  - the server owner;
  - a preview of the DM.
- **DM:** `POST /{id}/dm` with `to` = `setter` or `owner`.
  - It never DMs about a server the bot has left, or a person who left the server.
  - Only a delivered DM marks the problem as told. Closed DMs are recorded as the last attempt.
- **Re-check:** `POST /{id}/recheck` checks the bot's permissions again and resolves the problem (`fixed`) when it can post.
- **Turn off:** `POST /turn-off` with `ids` (up to 200) does what each feature's own off command does:
  - New log: `MonitorLogs` off.
  - Welcome/leave: `GreetUsers`/`PartUsers` off.
  - Moderation log: every watcher type off.
  - Realm alerts: the subscriptions posting to that channel are removed.
  - The word filter can't be turned off from here.
- **Dismiss:** `POST /dismiss` with `ids` hides a problem until it resolves and breaks again.

**Retention:** problems are purged with the rest of a server's data 30 days after the bot leaves.
