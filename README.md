# Auto Pin Signs

Auto Pin Signs creates map pins from the text of signs without requiring the player to open the map. It is intended primarily for no-map playthroughs used together with a compass or another navigation mod.

The mod has two pin discovery modes:

1. **Local sign discovery** — the original behavior. A client creates or removes pins when nearby signs are loaded.
2. **Server-authoritative sign pins** — an optional server-controlled mode. The server parses every sign ZDO in the world and sends one live authoritative pin list to all clients.

Configuration is synchronized from the server when the mod is installed there.

## Features

- Creates pins from sign text using the five standard user pin icons: Fire, Base, Hammer, Dot and Portal.
- Supports configurable full-text lists, prefixes and suffixes.
- Prefixes and suffixes are hidden from the visible sign text and pin name while the original text remains available when editing the sign.
- Supports rich-text signs and optional rich-text tag stripping.
- Updates or removes local pins when a loaded sign changes or is destroyed.
- Can remove nearby saved user pins that no longer have a matching loaded sign.
- Can mark recognized sign pins as checked with alternate use.
- Provides `autopinsigns clear [range]`, `autopinsigns status` and `autopinsigns resync` console commands.

## Server-authoritative pins

Enable `Server Authoritative Pins / Enabled` on the server to use a server-maintained sign-derived list for selected standard user pin types.

The server:

- discovers all registered prefabs containing a `Sign` component;
- indexes their world ZDOs, including signs outside loaded zones;
- parses sign text with the same matching rules used by clients;
- rebuilds after sign creation, destruction, text changes, checked-state changes or matching configuration changes;
- sorts and compares the rebuilt list with the current snapshot;
- increments the revision and broadcasts only when the resulting pin list actually changed;
- sends the current revision and snapshot when a client connects or explicitly requests resynchronization.

The received server snapshot is stored separately from `Minimap.m_pins`. The client map collection is only a runtime projection. For every controlled user pin type, Auto Pin Signs temporarily hides saved client pins and recreates the visible projection from its private server snapshot with `save: false`. Hidden client `PinData` objects are retained unchanged. When `Minimap.GetMapData` serializes the player profile, the mod atomically removes the server projection, reinserts the hidden client pins for the duration of serialization and then restores the server projection. Non-controlled user pin types remain client-owned. The projection is reconciled after snapshots, map loading, shared-map imports and normal pin add/remove calls. A lightweight count check runs continuously, with a full audit once per second to recover even if another mod edits `m_pins` directly.

A client does not replace pins merely because its local config still contains the authoritative setting from a previous server. It probes the current server independently of configuration synchronization timing, and reconciliation starts only after that server confirms the mode through the Auto Pin Signs RPC. The server also broadcasts explicit mode transitions even when the pin revision itself did not change.

### Controlled pin types

`Server Authoritative Pins / Controlled pin types` is a flags setting containing Fire, Base, Hammer, Dot and Portal. The default is `All`.

Only selected types are replaced by the server snapshot and blocked in the normal pin-name UI. Types not selected continue to use local sign discovery and may still contain ordinary client-created pins.

> **Warning**
>
> Enabling server-authoritative pins temporarily hides existing client pins using the selected controlled types. Those pins remain unchanged in the player profile and return when authoritative mode or the corresponding controlled type is disabled. Server pins are a runtime overlay and are not written to the player profile.
>
> Map pings, events, players, locations, deaths and every other pin type are not removed or synchronized.

### Administrator-only publishing

When `Server Authoritative Pins / Only administrator signs` is enabled, the server publishes only matching signs whose stored author resolves to a member of the Valheim server administrator list. Host-authored signs are accepted. Signs with a missing, unknown or non-administrator author are ignored by the authoritative snapshot.

This restriction affects publication only. It does not prevent players from placing or editing signs through normal game permissions.

### Duplicate merging

`Server Authoritative Pins / Merge identical pins within distance` controls optional deduplication. A value of `0` disables it.

Pins are considered duplicates only when:

- their parsed pin types are identical;
- their final parsed names are identical;
- their horizontal XZ distance is less than or equal to the configured value.

Candidates are sorted by their ZDOID string, and the first candidate is retained. Its position, author, creator and checked state become the projected pin values. Every later matching candidate within the configured distance of a retained candidate is omitted. The algorithm is deterministic but intentionally representative-based rather than transitive clustering.

## Checked pin signs

Enable `General / Allow checked pin status` to allow alternate use on recognized pinned signs.

- Keyboard input is `Shift + E`; the hover text uses the game's alternate-use binding and displays `$hud_crossoffpin`.
- The command is added only when the current sign text resolves to a pin.
- Normal `E` interaction still opens the sign editor.
- Standard private-area access is required before the state can be changed.
- A checked sign displays `(x) ` before its parsed visible name.
- The original text stored by the sign is not modified and is still returned when editing it.
- The checked state is stored separately in the sign ZDO.
- In local mode, the loaded sign's local map pin receives the vanilla `m_checked` state.
- In server-authoritative mode, the server includes the checked state in the next live snapshot revision and every client receives the same crossed-off pin state.
- Disabling the config hides and ignores checked status without deleting the stored ZDO value; enabling it again restores the previous state.

Checked status does not participate in pin identity. Duplicate merging still uses only final type, final name and distance. If several signs are merged, only the retained lowest-ordered ZDOID representative determines the projected checked state.

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

- `autopinsigns clear [range]` — removes saved standard user pins near the player. Default range: 5 meters. Server-controlled types are skipped.
- `autopinsigns status` — prints the active mode, revision, controlled types and pin counts.
- `autopinsigns resync` — rebuilds the server list or requests the current server snapshot.

## Dependencies

- [BepInExPack Valheim 5.4.2350](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
- [ConditionalConfigSync 1.0.5](https://thunderstore.io/c/valheim/p/shudnal/ConditionalConfigSync/)

Install ConditionalConfigSync as a separate dependency; do not copy its DLLs into this mod's package.

## Donation
[Buy Me a Coffee](https://buymeacoffee.com/shudnal)

## Discord
[Join server](https://discord.gg/e3UtQB8GFK)
