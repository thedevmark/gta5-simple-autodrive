# SimpleAutoDrive — one-key waypoint autopilot for GTA V Enhanced

Set a waypoint. Get in the driver's seat. **Tap F6** to drive. **Hold F6 for 2 seconds** to change how aggressively.

Built for GTA V Enhanced (PC) on ScriptHookVDotNet 3 — after the existing autopilot mods misbehaved on our Enhanced setups (stale routes, ignored config), this one was written to do one job correctly.

## Controls

| Input | Action |
|---|---|
| Tap F6 | autopilot on / off |
| Tap F9 | cycle aggression tier — applies immediately, even mid-drive |

## Aggression tiers

| Tier | Speed | Style |
|---|---|---|
| Cruise | ~61 km/h | civil — stops at lights, yields |
| Hurried (default) | ~94 km/h | Rushed + wrong-way-when-blocked — passes slow traffic |
| Insane | ~130 km/h | Rushed + wrong-way-when-blocked — overtakes through the oncoming lane |

Every speed and style is editable in the ini.

## How it stays on route

The drive command is reissued **every 2 seconds from the car's current position**. There is no cached route to go stale and no wrong way to drive — the task is always "get from here to the waypoint." Arrival (default 15 m) stops the car dead and returns control.

## Behavior

| Situation | Result |
|---|---|
| F6 tapped, waypoint set, you're driving | autopilot on, notification shows tier and speed |
| F6 tapped again | autopilot off |
| F9 tapped | next tier (Cruise → Hurried → Insane → Cruise) |
| Car reaches the waypoint | hard stop, "Arrived", control returned |
| You leave the driver's seat | autopilot ends cleanly |
| Scripts reloaded (Insert) | task cleared, never left running under you |
| No waypoint / not driving when tapping F6 | tells you what's missing instead of failing silently |

## Requirements

- GTA V Enhanced (PC), Story Mode
- [ScriptHookV](http://www.dev-c.com/gtav/scripthookv/)
- ScriptHookVDotNet **nightly** (built and tested against 3.9.0; the last stable release predates Enhanced)

## Install

1. Grab `SimpleAutoDrive-v1.0.0.zip` from [Releases](../../releases).
2. Drop `SimpleAutoDrive.dll` into your game's `scripts\` folder.
3. Launch. `scripts\SimpleAutoDrive.ini` writes itself on first load.

## Configuration

`scripts\SimpleAutoDrive.ini`:

```ini
[MAIN]
ToggleKey=F6        # any System.Windows.Forms.Keys name
TierKey=F9          # tap to cycle aggression tier
DefaultTier=1       # 0 Cruise / 1 Hurried / 2 Insane - tier at startup
SpeedCruise=17.0    # meters per second (km/h = value x 3.6)
SpeedHurried=26.0
SpeedInsane=36.0
StyleCruise=786603  # civil (stops at lights) / rushed (does not)
StyleHurried=1074528293
StyleInsane=1074528805
StopRange=15.0      # meters to destination to count as arrived
TaskIntervalMs=0     # 0 = off; a periodic heartbeat reissue resets maneuvers mid-flight
```

Edits apply on the next script reload (Insert by default in ScriptHookVDotNet).

## Build from source

One file, no packaging:

```
csc -target:library -platform:x64 -optimize+ -out:SimpleAutoDrive.dll
    -r:"<game folder>\ScriptHookVDotNet3.dll" -r:System.dll -r:System.Windows.Forms.dll
    SimpleAutoDrive.cs
```

Reference the ScriptHookVDotNet3.dll from the game you'll run it on. Any Roslyn compiler works (Visual Studio 2022's `csc.exe`, or `dotnet build` after wrapping the source in a project); the ancient .NET Framework compiler that ships with Windows also handles this source.

## Driving style values

The defaults are presets from the community's [driving-styles reference](https://gtaforums.com/topic/822314-guide-driving-styles/) — `786603` civil, `1074528293` the real SHVDN "Rushed" (brakes near moving cars but passes them), `1074528805` adds bit 512, which allows using the oncoming lane when your lane is blocked. The old `2883621` you'll see in other mods ignores lights but mostly *queues* behind traffic — that's the value this mod replaced after its author watched it politely follow a van for three miles. Compose your own with the [driving style calculator](https://vespura.com/fivem/drivingstyle/).

## Notes

- **Story Mode only.** Never run mods in GTA Online.
- The notification API used (`Notification.Show`) is marked obsolete in recent SHVDN nightlies but works fine; swap to `Notification.PostTicker` if your build warns louder than mine.
- Why tiers are a second tap key instead of a hold key: vanilla GTA binds **hold-F6 to the character switch wheel**, so any hold on F6 fights the game. Learned the hard way; kept the note so you don't retry it.
- GTA's notification parser treats `~` as a formatting escape character; two stray tildes in one string pair up and swallow the text between them. Tier labels therefore contain no tildes. Learned from "Cruise ( Insane (".
- Conflict check before filing issues: another mod bound to F6 will fight for the key. Rename yours in the ini.

## License

MIT — see [LICENSE](LICENSE).
