# Privacy

**DDO Discord Presence** (the "plugin") and the Discord application it uses (the "app").
Last updated: 2026-11-11 (version 0.2.0).

## The short version

The plugin talks to **one thing only: the Discord program running on your own computer**. It has no
server of its own, no account system, no analytics and no telemetry. The author receives nothing.

## What it reads

From the game, through Dungeon Helper, on your computer: the quest or area you are in, the quest's
difficulty where the game reports it, your character's level and class, how many people are in your party,
and your character's name.

## What it sends, and to whom

It sends a status ("Rich Presence") to your local Discord client, which then shows it on your Discord
profile to the people who can see your activity there. Depending on your settings the status can contain:

| Item | Default |
|---|---|
| Quest name | on |
| Town, public area or wilderness name | on |
| Quest difficulty | on |
| Character level and class | on |
| Party size | on |
| Time spent in the current quest or area | on |
| **Character name** | **off** |

Nothing is sent until you turn sharing on: the first time you open the plugin it asks, and sharing stays off
unless you choose "Turn on". Every item can be switched off. Places can be hidden by name, in which case the
status is cleared while you are there. "Pause sharing" clears your status at once. Logging out of your
character, or closing Dungeon Helper, clears it.

If you play several characters at once (several game clients) and turn the character name on, the status lists
the names of all of them that are sharing, e.g. "Thalindra, Borrik, Vessa".

The status is published through a Discord application registered by the plugin's author. That only lets
Discord show the application's name ("DDO") and its pictures next to your status; it gives the author no access
to your Discord account, your status or any other data, and the author receives nothing.

Once the status reaches Discord it is handled under Discord's own privacy policy and your Discord privacy
settings (for example *Settings > Activity Privacy*). The plugin has no control over that.

## What it stores

One settings file beside the plugin on your computer (`DdoDiscordPresence.json`): your switches and your list
of hidden places. It contains no passwords and no Discord token. Dungeon Helper's log may contain the plugin's
status lines.

While a character is logged in, the plugin also keeps one small file per game client in your Windows temporary
folder (`%TEMP%\DdoDiscordPresence`), so that several game clients on the same PC show one combined status. It
holds only what that client would show (the place text, and the character name only if you turned the name on)
and is deleted when you log out or close Dungeon Helper; a leftover from a crash is removed after a minute.

## What it does not do

- It does not read your Discord messages, friends, servers or token. The only thing it learns from Discord
  is the account name Discord reports when the connection opens, shown to you in the plugin's status line.
- It does not contact any website or server. No network connection is made by the plugin.
- It does not collect, sell or share data with the author or anyone else.

## Children

The plugin is a hobby add-on for an online game and is not directed at children. Use of the game and of
Discord is subject to those services' own age rules.

## Changes

This note may change. The date at the top shows the latest version.

## Contact

Open an issue where you obtained the plugin, or contact the author under the handle **longfreename**.
