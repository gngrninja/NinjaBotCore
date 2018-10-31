[![NinjaBot](https://static1.squarespace.com/static/5644323de4b07810c0b6db7b/5939edfbf7e0abe61afd8b9c/5940bca7e58c6299ddc2119a/1497420130867/botdiscord.png?format=300w)](https://gngr.ninja/bot)

# NinjaBot
[![Build status](https://ci.appveyor.com/api/projects/status/9r20viaa3r2i9ksf?svg=true)](https://ci.appveyor.com/project/gngrninja/ninjabotcore) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

NinjaBot is a Discord bot written in C#. 

It's primary focus is to help out guilds in World of Warcraft.

This project has been an awesome way for me to learn C#, feel free to toss in a pull request if there's a better way to do something!

## Getting Started

The first thing you'll need to do is [invite the bot to your server](https://discordapp.com/oauth2/authorize?client_id=238495040446398467&scope=bot&permissions=19520). 
It will need permissions to read and post messages at the very minimum. 

More information on the bot and getting started [here](https://www.gngrninja.com/bot).

### Associating your guild
Associating a WoW guild with your Discord server allows you to use the Warcraftlogs watching command, as well as some autocomplete features for guild member names when using various WoW commands.

To associate your guild with NinjaBot use, the following command:
```
!set-guild realmName, guildname, region
```

NinjaBot will then attempt to find all the information it needs to store for future requests from your Discord server.

Here are some examples of using the command:
### US (also the default if no region is specified)
```
!set-guild Destromath, NinjaBread Men, us
```
### EU
``` 
!set-guild Silvermoon, Rome in a Day, eu
```
### RU
```
!set-guild Ревущий фьорд, Порейдим месяц, ru
```

![example](https://raw.githubusercontent.com/gngrninja/NinjaBotCore/Dev/media/set-guild.PNG)

## WoW Commands

### [Warcraftlogs](https://www.warcraftlogs.com) Auto Log Poster

To use the auto log poster, use this command in the channel you want them automatically posted to:
```
!watch-logs
```

![example](https://raw.githubusercontent.com/gngrninja/NinjaBotCore/Dev/media/watch-en.PNG)

You can use the same exact command to disable the auto log posting, and then use it again to enable it (in the channel you want them posted to).

![example](https://raw.githubusercontent.com/gngrninja/NinjaBotCore/Dev/media/watch-dis.PNG)

### [Warcraftlogs](https://www.warcraftlogs.com) Last Three Logs

To get the last three of your guild's logs, use:

```
!logs
```

### World of Warcraft Commands

### [Raider.IO](https://www.raider.io) Player Information Lookup

Command 
Help:

```
!rpi
```

Try to find character (first in guild, then best guess)
``` 
!rpi characterName
```

![example](https://raw.githubusercontent.com/gngrninja/NinjaBotCore/Dev/media/rpi.PNG)

Long form version to try to find someone not in the same region
```
!rpi characterName realmName region(us or eu)
```

### [Raider.IO](https://www.raider.io) Guild Information

```
!guildstats
```

![example](https://raw.githubusercontent.com/gngrninja/NinjaBotCore/Dev/media/guildstats.PNG)

### Armory lookup (soon to be rolled into rpi where it counts)

```
!armory characterName
```

### Gearlist

```
!gearlist characterName
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