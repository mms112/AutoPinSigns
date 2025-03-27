# Auto Pin Signs
Create map pins based on sign's text you placed. Useful for nomap + Compass playthrough. Kinda useless otherwise. Don't break immersion while saving pin functionality for nomap playthroughs.

The mod was not intended as a fully functional replacement for pins in nomap walkthrough. Just to pin most important POI like main base and remote camps or to be some beacon for sailing home.

## Features
* Creates a pin on the minimap when you set text on sign that fits one of the lists
* Configurable strings filter for 5 map pins (fire,base,hammer,dot,portal)
* Updates/Deletes a pin on text change or sign destroying
* Automatically adds new pin on close proximity with the sign (when it is loaded)
* Support of html flavored signs like "<color="red">pin"
* Console command "autopinsigns clear 5" will erase all pins from map in that radius around player
* if "Remove nearby map pins without related signs" config enabled any nearby pin that does not have related sign will be removed

## Known issues
* if someone destroyed pinned sign when you're not there your pin will stay until you get near it with "Remove nearby map pins without related signs" config enabled.

## Installation (manual)
extract AutoPinSigns.dll file to your BepInEx\Plugins\ folder

## Configurating
The best way to handle configs is [Configuration Manager](https://thunderstore.io/c/valheim/p/shudnal/ConfigurationManager/).

Or [Official BepInEx Configuration Manager](https://valheim.thunderstore.io/package/Azumatt/Official_BepInEx_ConfigurationManager/).

## Mirrors
[Nexus](https://www.nexusmods.com/valheim/mods/2433)

## Donation
[Buy Me a Coffee](https://buymeacoffee.com/shudnal)