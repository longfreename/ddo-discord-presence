# DDO Discord Presence

> **This plugin is for Dungeon Helper 4.**

A Dungeon Helper plugin by **longfreename** that shows what you are doing in Dungeons & Dragons Online as your
Discord status:

```
Playing DDO
The Sharn Syndicate
Level 15 Paladin · Elite · Party of 4
00:07 elapsed
```

Inside a quest the first line is the quest's name; in town, a public area or the wilderness it is the area's name
("The Harbor"). The second line is built only from things the plugin can read reliably, and every part of it can be
switched off. Nothing is shared until you say so.

## Install

Download `DdoDiscordPresence_0.2.0.zip` from this repository's **Releases** page (a copy is also in `dist/`).

1. In Dungeon Helper, use **Install plugin** and pick `DdoDiscordPresence_<version>.zip`.
   Or by hand: close Dungeon Helper and copy the zip's `plugins\DdoDiscordPresence` folder into
   `%APPDATA%\Dungeon Helper\plugins\`, so that you have
   `%APPDATA%\Dungeon Helper\plugins\DdoDiscordPresence\win-x64\DdoDiscordPresence.dll`.
2. Start Dungeon Helper and enable **DDO Discord Presence** in its plugin list.
3. Click the plugin's icon on the Dungeon Helper toolbar: a steel speech bubble holding a gold die, with a green dot.

You also need:

- **Discord desktop** running on the same PC (the browser version cannot be reached by other programs).
- In Discord, **User Settings → Activity Privacy → Share your detected activities with others** turned on.

## First run

The first time you open the panel it asks one question: **"Show what you're playing in DDO on your Discord
profile?"**, with a list of what would be shown. Choose **Turn on** or **Not now**. Your answer is saved and you are
not asked again; you can change it at any time with *Share my DDO activity on Discord*.

## What you can switch

All settings save the moment you change them, to `DdoDiscordPresence.json` in the plugin's folder. Opening the panel
never changes them.

| setting | default | effect |
|---|---|---|
| Share my DDO activity on Discord | off until you turn it on | the master switch |
| Pause sharing | off | clears your status immediately and keeps it clear |
| Quest names | on | off: "In a quest" |
| Town, public area and wilderness names | on | off: "In town" / "In a public area" |
| Quest difficulty | on | "Casual", "Normal", "Hard", "Elite", "Reaper 3" (in quests, when the game reports it) |
| Difficulty badge on the picture | on | a small difficulty badge on the corner of the picture |
| Character level and class | on | "Level 15 Paladin" |
| Show my character's name | **off** | your character's name; when multi-boxing, every sharing character's name |
| Party size | on | "Party of 4" (only when 2 or more) |
| Time in the current quest or area | on | Discord's "elapsed" timer |
| Hide while in | empty | one entry per line; while the quest or area name contains an entry, nothing is shared |

Whenever there is nothing to show (logged out, paused, a hidden place, a name that cannot be read) the plugin
**clears** your Discord status rather than leaving old text up.

**About the character name.** With the name on, anyone who can see your Discord activity can tell which character
you are playing, and so find you in game. That is why it is off by default. Your Discord privacy settings decide who
sees your activity.

## Several characters at once (multi-boxing)

If you run several game clients, each with its own character, the plugin shows **one** status for all of them
instead of switching between them:

```
The Sharn Syndicate · +1 elsewhere
Thalindra, Borrik, Vessa · Elite · Party of 4
```

- **The names** of every character that is sharing (with the name switch on) are listed, separated by commas, in the
  order their game clients started, so the list does not reorder when you switch windows. A list too long for
  Discord ends with "+3 more", and the full list appears when someone hovers over the picture.
- **The place, difficulty, party size and timer** are those of the *primary* character: the one whose game window is
  in front, or else the client that started first. If the others are in the same quest or area it is said once;
  otherwise "+1 elsewhere" is added when it fits.
- Level and class are left out when several names are shown (they would be ambiguous).
- A client with sharing paused, or the name switch off, adds no name. If the primary character is in a hidden place
  the whole status is cleared; a hidden place of another character just leaves that character out.
- The settings are shared by every game client in a Dungeon Helper: a change in one panel shows in the others.
- One of the clients talks to Discord for all of them. If it logs out or closes, another takes over within a few
  seconds. This also works across several Dungeon Helpers on the same PC and Windows account.

When the status line is too long for Discord (128 characters), the least important parts go first: party size, then
class, then difficulty.

## The panel

- **Status line:** *Not sharing yet* (first run) / *Off* / *Paused* / *Discord not running* / *Connected as name* /
  *Shown by another game client* (multi-boxing) / *Error: …* with Discord's own message.
- **Preview:** exactly what is (or would be) shown on your profile.
- **Send a test status (20 s):** shows a sample status for 20 seconds and reports what Discord answered.
- **What the game reports** (collapsed): the raw values the plugin reads, and every game client taking part.

## Troubleshooting

- **"Discord not running"** while Discord is open: Discord and Dungeon Helper must run as the same Windows user,
  and not one as administrator while the other is not. The plugin retries after 5 s, 15 s, then every minute.
- **Connected, but nothing on my profile:** check *Share your detected activities with others* in Discord's Activity
  Privacy settings, and that you are looking at the account named in the status line.
- **No difficulty shown:** the plugin only shows the difficulty when the game reports it; otherwise it is left out.
- The Dungeon Helper log has the plugin's lines under `DdoDiscordPresence` (each message at most once a minute).

## How the quest and area are detected

Read through Dungeon Helper every 5 seconds while a character is logged in, and 1.5 s after each map change.

| what | how | confidence |
|---|---|---|
| inside a quest | not in a peaceful zone (`InTown()` false) and the instance names a quest (`GetInstanceQuestDid()`) | medium; the client keeps the last quest after you leave, so the town check decides |
| quest name | the instance quest's name from the game's own data | high |
| area name | the current map area's name from the game's own data | medium |
| difficulty, reaper skulls | the instance's properties | **unverified**; left out when not readable |
| level | the character's total level | high (checked at level 15) |
| class | the Who panel's class list | medium; may be empty, then only the level is shown |
| party size | the party list plus you | medium |

The elapsed timer restarts when you enter a different quest (or area outside quests); moving between areas inside
one quest keeps it. Names that cannot be read cleanly are never sent.

## Not verified yet

- **Multi-boxing in a real session.** The coordination was tested with several plugin instances in one process and
  with two real processes, but not with several real game clients in Dungeon Helper.
- **Which window counts as "in front"** (the game window handle against the foreground window) in real play.
- **Difficulty and reaper skulls**, class names, party counts with hirelings, levels above 20.
- **The first-run panel inside Dungeon Helper** (it was rendered in a test harness).

## Files in the zip

`win-x64\DdoDiscordPresence.dll` and `.deps.json` (the plugin), `README.md`, `INSTALL.txt`, `TERMS.md`,
`PRIVACY.md`. Dungeon Helper provides everything else the plugin needs.

## License

MIT. See [LICENSE](LICENSE). Use is also subject to [TERMS.md](TERMS.md).
