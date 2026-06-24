# Auto Pin Signs

Auto Pin Signs creates map pins from the text of signs without requiring the player to open the map. It is intended primarily for no-map playthroughs used together with a compass or another navigation mod.

The mod has two pin discovery modes:

1. **Local sign discovery** — the original behavior. A client creates or removes pins when nearby signs are loaded.
2. **Server-authoritative sign pins** — an optional server-controlled mode. The server parses signs throughout the world and keeps one authoritative pin list updated for all connected clients in real time.

Configuration is synchronized from the server when the mod is installed there.

## Features

- Creates pins from sign text using the five standard user pin icons: Fire, Base, Hammer, Dot and Portal.
- Supports configurable full-text lists, prefixes and suffixes.
- Prefixes and suffixes are hidden from the visible sign text and pin name while the original text remains available when editing the sign.
- Supports rich-text signs and optional rich-text tag stripping.
- Updates or removes local pins when a loaded sign changes or is destroyed.
- Can remove nearby saved user pins that no longer have a matching loaded sign.
- In server-authoritative mode, pins from distant signs appear for every connected client as soon as the server processes the change, without opening the map or using a cartography table.
- Provides `autopinsigns clear [range]`, `autopinsigns status` and `autopinsigns resync` console commands.

## Server-authoritative pins

Enable `Server Authoritative Pins / Enabled` on the server to use the server's complete sign-derived list instead of local discovery.

The server detects signs throughout the world, updates the shared list when their parsed pin data changes and sends the current list when a client connects or requests resynchronization. Updates are live: if another player places, edits or removes a pinned sign far away, the corresponding pin is updated for connected clients without them opening the map, touching a cartography table, visiting the sign or loading its area. This is the main usability advantage over local sign discovery.

> **Warning**
>
> Enabling server-authoritative pins permanently removes every client pin using the five standard Fire, Base, Hammer, Dot and Portal types. The mod does not back up or restore those pins when the option is later disabled.
>
> Map pings, events, players, locations, deaths and every other pin type are not removed or synchronized.

While authoritative mode is active, the five standard user pin types always reflect the server list. Creating local pins of those types is disabled.

## Matching priority

Matching is case-insensitive and uses this priority:

1. Exact full-text matches from the configured Fire, Base, Hammer, Dot and Portal lists.
2. Explicit prefixes.
3. Explicit suffixes.
4. Partial matches from the configured lists when `General -> Less strict string comparison` is enabled.
5. The first configured `anyPin` prefix fallback.
6. The first configured `anyPin` suffix fallback.

When several explicit prefixes, suffixes or partial list values match, the longest matching value wins. This lets a specific rule override a shorter overlapping rule.

## Prefixes and suffixes

Default prefixes:

- `Fire:`
- `Base:`
- `Hammer:`
- `Pin:`
- `Portal:`

`Pin: Boat` creates a Dot pin named `Boat`. The visible sign text is also `Boat`, while opening the sign for editing returns the original `Pin: Boat` text.

A prefix or suffix does not need to contain a word or include surrounding spaces. For example, configuring `:` as a prefix makes the sign text `: Boat` create a pin named `Boat`. The matched part is removed and whitespace around the remaining name is trimmed.

Suffixes provide the same behavior where helper words or phrases naturally come after the pin name. For example, Russian can use the prefix `Тут`: `Тут Лодка` becomes `Лодка`. In English, `Here Boat` sounds awkward, so the suffix `here` can be used instead: `Boat here` becomes `Boat`. The matched suffix and surrounding whitespace are removed from the final pin name.

If you want to use HTML tags as prefixes, disable `General -> Strip HTML tags from text` mod config. This is also useful for signs that use emoji, symbols or other values encoded through rich-text tags.

### The `anyPin` keyword

A prefix or suffix list may contain the special value `anyPin`. It is not removed from sign text because it is not a real prefix or suffix; it is a final fallback that accepts any non-empty sign text when no exact, explicit prefix/suffix or partial-list rule matched.

If several pin types contain `anyPin`, the first type in Fire, Base, Hammer, Dot and Portal order is used. Prefix `anyPin` has priority over suffix `anyPin`.

## Console commands

- `autopinsigns clear [range]` — removes saved standard user pins near the player. Default range: 5 meters. Disabled in authoritative mode.
- `autopinsigns status` — prints the active mode, revision and pin counts.
- `autopinsigns resync` — rebuilds the server list or requests the current server snapshot.

## Configurating
The best way to handle configs is [Configuration Manager](https://thunderstore.io/c/valheim/p/shudnal/ConfigurationManager/).

Or [Official BepInEx Configuration Manager](https://valheim.thunderstore.io/package/Azumatt/Official_BepInEx_ConfigurationManager/).

## Mirrors
[Nexus](https://www.nexusmods.com/valheim/mods/2433) — legacy page. Current releases are published on Thunderstore. Questions can still be asked there, but Discord is the preferred support channel.

## Donation
[Buy Me a Coffee](https://buymeacoffee.com/shudnal)

## Discord
[Join server](https://discord.gg/e3UtQB8GFK)
