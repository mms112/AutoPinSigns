# Auto Pin Signs

Auto Pin Signs creates map pins from the text of signs without requiring the player to open the map. It is intended primarily for no-map playthroughs used together with a compass or another navigation mod.

The mod has two pin discovery modes:

1. **Local sign discovery** — the original behavior. A client creates or removes pins when nearby signs are loaded.
2. **Server-authoritative sign pins** — an optional server-controlled mode. The server parses every sign ZDO in the world and sends one authoritative pin list to all clients.

Configuration is synchronized from the server when the mod is installed there.

## Features

- Creates pins from sign text using the five standard user pin icons: Fire, Base, Hammer, Dot and Portal.
- Supports configurable full-text lists, prefixes and suffixes.
- Prefixes and suffixes are hidden from the visible sign text and pin name while the original text remains available when editing the sign.
- Supports rich-text signs and optional rich-text tag stripping.
- Updates or removes local pins when a loaded sign changes or is destroyed.
- Can remove nearby saved user pins that no longer have a matching loaded sign.
- Provides `autopinsigns clear [range]`, `autopinsigns status` and `autopinsigns resync` console commands.

## Server-authoritative pins

Enable `Server Authoritative Pins / Enabled` on the server to use the server's sign-derived list instead of local discovery.

The server:

- discovers all registered prefabs containing a `Sign` component;
- indexes their world ZDOs, including signs outside loaded zones;
- parses sign text with the same matching rules used by clients;
- rebuilds after sign creation, destruction, text changes or matching configuration changes;
- sorts and compares the rebuilt list with the current snapshot;
- increments the revision and broadcasts only when the resulting pin list actually changed;
- sends the current revision and snapshot when a client connects or explicitly requests resynchronization.

The received server snapshot is stored separately from `Minimap.m_pins`. The client map collection is only a runtime projection: Auto Pin Signs removes every foreign pin using one of the five standard user icon types and recreates the projection from its private snapshot with `save: false`. The projection is reconciled after snapshots, map loading, shared-map imports and normal pin add/remove calls. A lightweight count check runs continuously, with a full audit once per second to recover even if another mod edits `m_pins` directly.

A client does not replace pins merely because its local config still contains the authoritative setting from a previous server. It probes the current server independently of Conditional Config Sync timing, and reconciliation starts only after that server confirms the mode through the Auto Pin Signs RPC. The server also broadcasts explicit mode transitions even when the pin revision itself did not change.

> **Warning**
>
> Enabling server-authoritative pins permanently removes every client pin using the five standard Fire, Base, Hammer, Dot and Portal types. The mod does not back up or restore those pins when the option is later disabled.
>
> Map pings, events, players, locations, deaths and every other pin type are not removed or synchronized.

While authoritative mode is active, the normal pin-name dialog is blocked. Pins inserted through `Minimap.AddPin`, map imports or direct `m_pins` edits are removed by reconciliation if they use one of the five server-controlled user icon types.

## Matching priority

Matching is case-insensitive and uses this priority:

1. Exact full-text matches from the configured Fire, Base, Hammer, Dot and Portal lists.
2. Explicit prefixes.
3. Explicit suffixes.
4. Partial matches from the configured lists when `General -> Less strict string comparison` is enabled.
5. The first configured `anyPin` prefix fallback.
6. The first configured `anyPin` suffix fallback.

When several explicit prefixes, suffixes or partial list values match, the longest matching value wins. Equal-length matches retain Fire, Base, Hammer, Dot and Portal priority.

## Prefixes and suffixes

Default prefixes:

- `Fire:`
- `Base:`
- `Hammer:`
- `Pin:`
- `Portal:`

`Pin: Boat` creates a Dot pin named `Boat`. The sign displays `Boat`, but opening it for editing returns the original `Pin: Boat` text.

Prefix and suffix values do not need to contain words or surrounding spaces. With `:` configured as a prefix, `: Boat` becomes `Boat`; after removing the matched value, surrounding whitespace is trimmed.

Suffixes support the opposite word order. With `here` configured as a suffix, `Boat here` becomes a pin named `Boat`, so it can be used instead of the `Pin: Boat` prefix form.

If you want to use HTML tags as prefixes, disable `General -> Strip HTML tags from text` mod config. This can also be useful for emoji, symbols or other values encoded through rich-text tags.

## Console commands

- `autopinsigns clear [range]` — removes saved standard user pins near the player. Default range: 5 meters. Disabled in authoritative mode.
- `autopinsigns status` — prints the active mode, revision and pin counts.
- `autopinsigns resync` — rebuilds the server list or requests the current server snapshot.

## Suggested multiplayer validation

1. Start a dedicated server with authoritative mode disabled and verify that local sign discovery still behaves as before.
2. Enable authoritative mode, connect a client with several saved Fire/Base/Hammer/Dot/Portal pins and verify the explicit removal warning.
3. Verify that pings, death/event/player/location pins and every non-user icon type remain untouched.
4. Add, rename, retype and destroy signs while two clients are connected. Both clients should converge on the same revision and pin list.
5. Damage or otherwise update a sign without changing its parsed pin data. The server may rebuild its candidate list, but the revision and RPC snapshot must not change.
6. Reconnect a client and use `autopinsigns status` to verify that it receives the current revision without requiring a sign change.
7. Read and write a cartography table and run a map-rendering mod that only reads `m_pins`. The authoritative projection must remain complete without per-pin log spam.
8. Add or remove one of the five user pin types through another mod, including a direct `m_pins` edit. The projection must repair itself immediately or during the one-second audit.
9. Connect with a stale local authoritative config to a server where the mode is disabled or the mod is absent. Existing saved pins must not be deleted.
