[![NinjaBot](https://static1.squarespace.com/static/5644323de4b07810c0b6db7b/5939edfbf7e0abe61afd8b9c/5940bca7e58c6299ddc2119a/1497420130867/botdiscord.png?format=300w)](https://gngr.ninja/bot)

# NinjaBot
[![Build status](https://ci.appveyor.com/api/projects/status/9r20viaa3r2i9ksf?svg=true)](https://ci.appveyor.com/project/gngrninja/ninjabotcore) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

NinjaBot is a Discord bot written in C#. 

Its primary focus was to help out guilds in World of Warcraft.
I will be shifting to more administrative / Discord tasks for the time being as I am no longer actively playing World of Warcraft.

This project has been an awesome way for me to learn C#, feel free to toss in a pull request if there's a better way to do something!

**Updated for 10.1, good luck in *Aberrus, the Shadowed Crucible*.**

## Getting Started
The first thing you'll need to do is [invite the bot to your server](https://discord.com/api/oauth2/authorize?client_id=238495040446398467&permissions=377960909888&scope=bot%20applications.commands). 
It will need permissions to read and post messages at the very minimum. 
If you wish to use NinjaBot to assist with admin tasks (kicking/banning users, message management, etc), [please use this link](https://discord.com/api/oauth2/authorize?client_id=238495040446398467&permissions=1494652415174&scope=bot%20applications.commands).

### Slash commands
This bot is now using [slash commands](https://discord.com/blog/slash-commands-permissions-discord-apps-bots), be sure to use the updated invites above to invite it to your discord server. 
If it is already on your server, you may need to ensure users can use the commands:
```
Go to Server Settings → Integrations, and then 'Manage' (next to NinjaBot).
```

There are a limited number of classic WoW commands now available. You can associate your guild, and watch/get logs from Warcraft logs. 
Currently there is no way to get classic armory or guild information via the API, and I'll be watching to see when/if things get added!

More information on the bot and getting started [here](https://www.gngrninja.com/bot).

### Associating your guild (Retail WoW)
Associating a WoW guild with your Discord server allows you to use the Warcraft Logs watching command, as well as some autocomplete features for guild member names when using various WoW commands.

To associate your guild with NinjaBot, use the following command:
```
/setguild realmName, guildName, region
```
Here are some examples of using the command:
### US (also the default if no region is specified)
```
/setguild Blackwater Raiders, Raiders of the Lost Ark, us
```
### EU
``` 
/setguild Silvermoon, Rome in a Day, eu
```
### RU
```
/setguild Ревущий фьорд, Порейдим месяц, ru
```

### Associating your guild (Classic WoW)

To associate your classic WoW guild with NinjaBot, use the following command:
```
/setclassicguild "guild name" "realm" "region"
```

Valid regions:
US, EU, KR, TW, and CN

NinjaBot will associate what you enter as the guild attached to your server. That data will then be used to watch / retrieve logs from Warcraft Logs.

Example:
### US (also the default if no region is specified)
```
/setclassicguild "Disorder" "Rattlegore"
```
## WoW Commands

### [Warcraft Logs](https://www.warcraftlogs.com) Auto Log Poster (Retail and Classic)

To use the auto log poster, use this command in the channel you want them automatically posted to:
```
/watchlogs
```

You can use the same exact command to disable the auto log posting, and then use it again to enable it (in the channel you want them posted to).

### [Warcraft Logs](https://www.warcraftlogs.com) Last Three Logs

To get the last three of your guild's logs, use:

```
/logs
```

### [Warcraft Logs](https://www.warcraftlogs.com) Last Three Logs (Classic WoW)

To get the last three of your guild's logs, use:

```
/logsclassic
```

### World of Warcraft Commands

### [Raider.IO](https://www.raider.io) Player Information Lookup

Command 
Help:

```
/rio
```

Try to find character (first in guild, then best guess)
``` 
/rio characterName
```

Long form version to try to find someone not in the same region
```
/rio characterName realmName region(us or eu)
```

### [Raider.IO](https://www.raider.io) Guild Information

```
/ginfo
```

## Server Enhancement Commands

NinjaBot can greet people leaving the server, and notify the server when someone leaves. The messages the bot uses are customizable.

Visit the [NinjaBot website](https://www.gngrninja.com/ninjabot-command-reference/2017/6/13/admin-commands) for more information.

## Help!

If you're having trouble using any of the WoW commands, the first thing to try is re-associating your WoW guild with your Discord server.
If that doesn't help, check out the following resources below:

[Discord Chat](https://discord.gg/MgvJuaV)

[NinjaBot Website](https://www.gngrninja.com/bot)

Feel free to open an issue here for any bugs or problems you come across!

Enjoy.