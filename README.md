# SimpleAutoDrive — one-key waypoint autopilot for GTA V Enhanced

Set a waypoint. Get in the driver's seat. **Tap F6** to drive. **Hold F6 for 2 seconds** to change how aggressively.

Built for GTA V Enhanced (PC) on ScriptHookVDotNet 3 — after the existing autopilot mods misbehaved on our Enhanced setups (stale routes, ignored config), this one was written to do one job correctly.

## Controls

| Input | Action |
|---|---|
| Tap F6 | autopilot on / off |
| Hold F6 (2 s) | cycle aggression tier — applies immediately, even mid-drive |

## Aggression tiers

| Tier | Speed | Style |
|---|---|---|
| Cruise | ~61 km/h | civil — stops at lights, yields |
| Hurried (default) | ~94 km/h | rushed — lights are suggestions |
| Insane | ~130 km/h | rushed — good luck |

Every speed and style is editable in the ini.

## How it stays on route

The drive command is reissued **every 2 seconds from the car's current position**. There is no cached route to go stale and no wrong way to drive — the task is always "get from here to the waypoint." Arrival (default 15 m) stops the car dead and returns control.

## Behavior

| Situation | Result |
|---|---|
| F6 tapped, waypoint set, you're driving | autopilot on, notification shows tier and speed |
| F6 tapped again | autopilot off |
| F6 held 2 s | next tier (Cruise → Hurried → Insane → Cruise) |
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
DefaultTier=1       # 0 Cruise / 1 Hurried / 2 Insane - tier at startup
SpeedCruise=17.0    # meters per second (km/h = value x 3.6)
SpeedHurried=26.0
SpeedInsane=36.0
StyleCruise=786603  # civil (stops at lights) / rushed (does not)
StyleHurried=2883621
StyleInsane=2883621
StopRange=15.0      # meters to destination to count as arrived
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

## Notes

- **Story Mode only.** Never run mods in GTA Online.
- The notification API used (`Notification.Show`) is marked obsolete in recent SHVDN nightlies but works fine; swap to `Notification.PostTicker` if your build warns louder than mine.
- Conflict check before filing issues: another mod bound to F6 will fight for the key. Rename yours in the ini.

## License

MIT — see [LICENSE](LICENSE).
