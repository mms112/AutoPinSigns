# Auto Pin Signs
Create map pins based on sign's text you placed. Useful for nomap + Compass playthrough. Kinda useless otherwise. Don't break immersion while saving pin functionality for nomap playthroughs.

The mod was not intended as a fully functional replacement for pins in nomap walkthrough. Just to pin most important POI like main base and remote camps or to be some beacon for sailing home.

Mod configs is synchronized from server if installed there.

## Features
* Creates a pin on the minimap when you set text on sign that fits one of the lists
* Configurable strings filter for 5 map pins (fire,base,hammer,dot,portal)
* Updates/Deletes a pin on text change or sign destroying
* Automatically adds new pin on close proximity with the sign (when it is loaded)
* Support of html flavored signs like "<color="red">pin"
* Console command "autopinsigns clear 5" will erase all pins from map in that radius around player
* if "Remove nearby map pins without related signs" config enabled any nearby pin that does not have related sign will be removed

## Sign Prefixes

This mod supports **text prefixes on signs** to control automatic map pin creation without cluttering the sign or pin name.

There are default prefixes
- `Fire:`
- `Base:`
- `Hammer:`
- `Pin:`
- `Portal:`

For example, setting sign text as `Pin: Boat` will result in mod adding map pin named `Boat` and the sign text will be seen as `Boat` while in hovering text you will see full text `Pin: Boat`.

This way you can set up any pin with any text.

## Known issues
* if someone destroyed pinned sign when you're not there your pin will stay until you get near it with "Remove nearby map pins without related signs" config enabled.