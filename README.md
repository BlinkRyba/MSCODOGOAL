[Click Here To Download MSCODOGOAL (Official Releases)](https://github.com/BlinkRyba/MSCODOGOAL/releases)
----------------------------------------------------------------------------------------------

[Click Here To Visit The Official WIKI](https://github.com/BlinkRyba/MSCODOGOAL/wiki) (outdated right now, will update soon)
-----------------------------------------------------------------------------------------------

***

MSC Odo Goal 2.0
-----------------------------------------------------------------------------------------------

MSC Odo Goal is a My Summer Car mod that adds a Satsuma odometer HUD with a configurable distance goal system and optional live integration bridges for Streamlabs (Twitch) and TikTok.

Precompiled builds are available under the Releases section.

Available Releases:
-----------------------------------------------------------------------------------------------

There are currently four separate releases. Make sure you download the one you intend to use.

MSCODOGOAL Release 2.0 (Base Mod):
-----------------------------------------------------------------------------------------------

The core HUD mod. Includes:

Odometer

Goal tracking

Distance left

Progress bar

Timer

Bridge connection support

The mod works completely on its own without any bridges.

Bridges are only required if you want Twitch or TikTok events to increase the goal distance.

MSCODOGOAL Streamlabs Release (v2.0):
-----------------------------------------------------------------------------------------------

Bridge for Twitch / Streamlabs integration.

Converts Streamlabs events into goal distance increases.

Supported events may include:

Tips / Donations

Bits

Subscriptions

Stable and tested.

MSCODOGOAL TikTok Release (Alpha):
-----------------------------------------------------------------------------------------------

TikTok Live bridge integration.

This bridge allows TikTok Live events to interact with the distance goal system.

Events received from TikTok can increase the goal distance in the mod.

This release has been updated for Version 2.0 of the mod.

MSCODOGOAL Config Editor:
-----------------------------------------------------------------------------------------------

The Config Editor is used to edit the bridge configuration file.

It allows you to configure things such as:

Streamlabs tokens

Feature toggles

Event behavior

Logging settings

The editor will create the config.json automatically if one does not exist.

This allows configuration without manually editing JSON files.

Important Installation Notes:
-----------------------------------------------------------------------------------------------

The mod .dll itself goes in:

steamapps\common\My Summer Car\Mods

Bridge files must be placed inside:

steamapps\common\My Summer Car\Mods\Assets\MSCODOGOAL

If placed elsewhere, the mod will not connect properly.

Bridge Behavior:
-----------------------------------------------------------------------------------------------

Bridges must be launched manually.

They do NOT auto-start.

If a bridge is not running, the HUD still works normally.

The mod attempts to connect up to 3 times.

After 3 failed attempts, it stops trying until you press:

"Try Connect to Bridges Now"

This prevents console spam and infinite reconnect loops.

You can start the bridge while the game is running and go to the settings and click:

"Try Connect To Bridges Now"

Extra notes:
-----------------------------------------------------------------------------------------------

The mod also includes a Debug Info Dump feature that can help generate useful debugging information.
