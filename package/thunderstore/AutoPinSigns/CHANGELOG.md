# 2.0.0
* new feature: Sign Suffixes. The same as Sign Prefixes but useful for languages with different word orders.
* new mode: Server Authoritative Pins. The server maintains one shared pin list from matching signs throughout the world and updates connected clients in real time. Pins appear when another player places or edits a pinned sign, without opening the map, using a cartography table, visiting the sign or loading its area.
* added configurable authoritative pin types, optional administrator-only sign publishing and deterministic merging of identical nearby pins.
* server-authoritative pins preserve existing client pins in the player profile. Client pins are temporarily hidden while controlled and return when server authority or the corresponding controlled type is inactive.
* added optional checked sign pins. Use Shift + E on a recognized pinned sign to cross it off; checked state is synchronized in server-authoritative mode.
* migrated config synchronization to ConditionalConfigSync.

# 1.2.0
* configs will now be synced from server to clients if mod is installed on a server
* new feature: Sign Prefixes. If enabled, the mod checks whether sign text starts with a configured prefix (e.g. "Pin: "). Prefix text is NOT included in the pin name or visible sign text, but is preserved when editing the sign.

# 1.1.0
* complete rework
* more stable sign recognition and state update
* pin list configs now have custom config drawer for configuration manager
* after pin list config update sign states will be updated
* new config for removing nearby pins with no related signs

# 1.0.8
* patch 0.220.3

# 1.0.7
* Ashlands

# 1.0.6
* patch 0.217.46

# 1.0.5
* option to less strict strings comparison

# 1.0.4
* patch 0.217.22

# 1.0.3
* improved stability
* support of signs with html tags
* console command to clear nearest pins

# 1.0.2
* Added some protection from potential issues

# 1.0.1
* Added config update on sign interaction

# 1.0.0
* Initial release
