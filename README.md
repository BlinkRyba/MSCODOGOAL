[Click Here To Download MSCODOGOAL (Official Releases)](https://github.com/BlinkRyba/MSCODOGOAL/releases)
----------------------------------------------------------------------------------------------
***

MSC Odo Goal (Bridges Edition)
------------------------------------------------------------------------------------------------

MSC Odo Goal is a My Summer Car mod that adds a Satsuma odometer HUD with a configurable distance goal system and optional live integration bridges for Streamlabs (Twitch) and TikTok.

This repository contains the source code for:

-The base mod

-The Streamlabs (Twitch) bridge

-The TikTok bridge (Alpha)

Precompiled builds are available under the Releases section.


Available Releases:
------------------------------------------------------------------------------------------------

*There are currently three separate releases. Make sure you download the one you intend to use:*

MSCODOGOAL Release 1.1 (Base Mod):
------------------------------------------------------------------------------------------------

The core HUD mod.
Includes:

-Odometer

-Goal tracking

-Distance left

-Progress bar

-Timer

-Bridge connection support

This is required for any bridge integration.


MSCODOGOAL Streamlabs Release (ver 1.1):
------------------------------------------------------------------------------------------------

Bridge for Twitch / Streamlabs integration.
Converts Streamlabs events into goal distance increases.

Stable and tested.


MSCODOGOAL TikTok Release (Alpha 0.1):
------------------------------------------------------------------------------------------------

TikTok Live bridge integration.

This release is currently in Alpha.

It is untested due to not having access to a TikTok Live account at this time.

Testers are needed.

Expect:

Possible connection instability

Unexpected behavior

Incomplete edge case handling


Important Installation Notes:
------------------------------------------------------------------------------------------------

The bridge files must be placed inside:

steamapps\common\My Summer Car\Mods\Assets\MSCODOGOALTWITCH

If placed elsewhere, the mod will not connect properly.

The mod .dll itself goes in:

steamapps\common\My Summer Car\Mods


Bridge Behavior:
------------------------------------------------------------------------------------------------

Bridges must be launched manually.

They do NOT auto-start.

If a bridge is not running, the HUD still works normally.

The mod attempts to connect up to 3 times.

After 3 failed attempts, it stops trying until you press:
"Try Connect to Bridges Now"

This prevents console spam and infinite reconnect loops.

You can start the bridge while the game is running and go to the settings and click
"Try Connect To Bridges Now"


Alpha Notice (TikTok):
------------------------------------------------------------------------------------------------

The TikTok bridge is in early alpha state.

Because it has not yet been tested with a live TikTok account, community testing is highly appreciated.

If you test it, please report:

Connection success/failure

Event reliability

Delay issues

Console errors

Crash behavior


Repository Contents:
------------------------------------------------------------------------------------------------

/bridges
Source code for Streamlabs and TikTok bridges.
