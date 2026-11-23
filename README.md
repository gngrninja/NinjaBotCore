[![NinjaBot](https://static1.squarespace.com/static/5644323de4b07810c0b6db7b/5939edfbf7e0abe61afd8b9c/5940bca7e58c6299ddc2119a/1497420130867/botdiscord.png?format=300w)](https://gngr.ninja/bot)

# NinjaBot
[![Build status](https://ci.appveyor.com/api/projects/status/9r20viaa3r2i9ksf?svg=true)](https://ci.appveyor.com/project/gngrninja/ninjabotcore) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

NinjaBot is a Discord bot written in C#. 

Its primary focus is to offer helpful features for World of Warcraft guilds, and even if you don't play wow offer helpful server management features.

## Website
Check out [the website](https://bot.gnja.io) to best learn how to use NinjaBot and add it to your Discord server!

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

### WoW Forever: characters, roster, crafting and camps

Forever has no realms, and a character's full name (first name + surname) is unique per region. NinjaBot reads everything about a character from Blizzard's armory: class, level, ruleset, faction, gear, professions and the guild roster. A character appears there at level 10, after it has logged out once. Members only say which characters are theirs; nothing proves it, and every reply except the camp card is only visible to you.

Start with `/forever help`. It lists the three setup steps (server guild, a character, the roster), ticks off the ones this server and you have done, and points at the next one you can do yourself:
```
/forever help
```

Officers (Kick Members) set the server's guild. The armory fills in the spelling, ruleset and faction:
```
/forever guild set name:"Synthetic Guild" region:US
```

Blizzard has no search for Forever guilds or characters, so the suggestions come from what can be known. While you type a guild name, NinjaBot asks the armory about that exact name and shows the region, ruleset, faction and member count when it exists. It also offers the guilds your own saved characters are in.

Members save their own characters. The region defaults to the server's guild, and `character` suggests guildmates from Blizzard's roster, so you can pick yours instead of spelling it. A guildmate below level 10 is saved from the guild's roster. The first character you save in a server becomes your main there:
```
/forever char add character:Asha Brightvale
/forever char list
```

`/forever char list` is where you manage them: pick a character to make it your main, show it on or hide it from this server's roster, open its armory card or remove it.

Look up anyone's armory card, with level, guild, talents, professions, deaths, quests, a Gear tab and a Professions tab. Each profession has a skill bar drawn against 300: solid for skill earned, medium for what is left in the trained rank and light for ranks not trained yet. The Professions tab adds the rank, a menu that lists a profession's known recipes, and links to Wowhead's Forever site. The Gear tab lists each item on one line under Armor, Jewelry or Weapons, with its slot, item level and quality. It shows enchants under the item, set progress and the slots still empty, and item and set names link to Wowhead too:
```
/forever char view character:Asha Brightvale region:EU
```

The roster is Blizzard's member list (level, class and rank). It counts the guild's classes and level 60s, sorts by rank, level, name or class, and has a menu that opens any listed character's armory card. Your Discord name shows next to a character only after you put it on the roster:
```
/forever roster
```

See what a recipe takes, what the materials cost against buying the item, and who in the guild can make it. The first time someone asks, NinjaBot reads the guild's professions from the armory (up to 600 characters, highest level first) and keeps them for six hours:
```
/forever craft recipe:Handstitched Leather Boots
/forever price item:Light Leather
```

Prices are [TradeSkillMaster](https://tradeskillmaster.com)'s regional averages: across every auction house of every ruleset and faction in the region, not your own house, and usually about a day old.

Post a camp card for everyone to join. It works out who brings which camp object (one per player), who can light the campfire, and which objects add nothing because a class at the camp already casts the buff they replace:
```
/forever camp note:Fishing night at Menethil
```

Blizzard hasn't documented the Forever API, so every lookup can fail: a card says so and nothing is saved from a guess.

Leaving a server removes your main and your roster entries there. Removing a character from `/forever char list` deletes it everywhere.

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

NinjaBot can greet people joining the server, and notify the server when someone leaves. The messages the bot uses are customizable.

### Greetings:

In the channel you'd like to have the greetings in (this will either enable or disable, as a toggle): 
```
/toggle-greetings
```

If greetings are enabled, and you'd like to set a different channel to handle leaving/parting messages, use (in the channel you want it to be):

```
/set-parting-channel
```

When setting greetings, you can use emojis the bot has access to. For example, you can use :joy:, and it will translate to 😂.

If you'd like to link to a specific channel, you'd use:
```
Please check out this channel for information: <#channel-id-here>
```
(to get the ID, just right click a text channel and click "**Copy Channel ID**")

To change the greeting message, use:

```
/set-join-message
```

To change the leaving/parting message, use:
```
/set-part-message 
```

### Server wide note

Server members with kick permissions and above can set a server wide note via:

```
/set-note
```

Anyone in the server can retrieve the note via:

```
/get-note
```
Visit the [NinjaBot website](https://bot.gnja.io/commands) for more information.

## Help!

If you're having trouble using any of the WoW commands, the first thing to try is re-associating your WoW guild with your Discord server.
If that doesn't help, check out the following resources below:

[Discord Chat](https://discord.gg/MgvJuaV)

Feel free to open an issue here for any bugs or problems you come across!

Enjoy.
