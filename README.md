# SimpleAutoDrive — one-key waypoint autopilot for GTA V Enhanced

Set a waypoint. Get in a car. **Tap F6** — an invisible chauffeur takes the wheel and drives you there while you sit in the passenger seat, free to shoot, use your phone, or watch the world go by. **Tap F9** to change how aggressively. Tap F6 again and you're back in the driver's seat.

Chauffeur mode is the default (`ChauffeurMode=0` in the ini gives the classic player-drive autopilot). The chauffeur is a hidden NPC with maximum driver ability — the player ped's low default skill is what caused the routing loops and intersection overshoots in earlier versions.

Built for GTA V Enhanced (PC) on ScriptHookVDotNet 3 — after the existing autopilot mods misbehaved on our Enhanced setups (stale routes, ignored config), this one was written to do one job correctly.

## Controls

| Input | Action |
|---|---|
| Tap F6 | autopilot on / off (chauffeur in, drive, chauffeur out, you back in the driver's seat) |
| Tap F9 | cycle aggression tier (Cruise, Brisk, Hurried) — re-tasked immediately, even mid-drive |

## Aggression tiers

| Tier | Speed | Style |
|---|---|---|
| Cruise | ~58 km/h | civil — stops at lights, yields |
| Brisk | ~76 km/h | SHVDN Rushed — passes when convenient |
| Hurried (default) | ~94 km/h | + wrong-way-when-blocked — passes aggressively |

Notifications show the tier name only; speeds live in the ini.

Every speed and style is editable in the ini.

## Destinations

The player waypoint (purple marker) takes priority. Without one, the mod follows the nearest **mission objective blip farther than 50 m** - so it works during missions, and it re-targets automatically as the objective moves. Contact markers beside you in free roam are ignored via the 50 m rule.

## The driving model (v7 - the proven recipe)

One `TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE` to the destination with a speed and a driving style - the same model the long-running community autopilots have shipped for years. The engine's pathfinder follows the road graph end to end; it never beelines through geometry.

- **The task is issued once per destination** and re-issued only when the destination or tier changes. Nothing else touches the driving - no speed interference, no periodic re-issues, no route surgery. (A driving task restarted every tick never commits to a turn, and short-range "direct steer" tasks cut straight lines through walls - both lessons are paid for.)
- **Arrival is a distance check** (default 35 m), so a waypoint inside a building still ends the trip cleanly at the nearest road instead of orbiting.
- **Mission objectives work without a waypoint** - the nearest objective blip over 50 m becomes the destination.

## Arrival

Waypoints can sit inside buildings or in parking lots, so the long-range task aims at the nearest road node to the marker. Arrival (default 35 m, `StopRange` in the ini) stops the car and returns the driver's seat to you.

## Behavior

| Situation | Result |
|---|---|
| F6 tapped, waypoint set, you're driving | autopilot on, notification shows tier and speed |
| F6 tapped again | autopilot off |
| F9 tapped | next tier (Cruise → Brisk → Hurried → Cruise) |
| Car reaches the waypoint | hard stop, "Arrived", control returned |
| You leave the driver's seat | autopilot ends cleanly |
| Scripts reloaded (Insert) | task cleared, never left running under you |
| No waypoint / not driving when tapping F6 | tells you what's missing instead of failing silently |

## Requirements

- GTA V Enhanced (PC), Story Mode
- [ScriptHookV](http://www.dev-c.com/gtav/scripthookv/)
- ScriptHookVDotNet **nightly** (built and tested against 3.9.0; the last stable release predates Enhanced)

## Install

1. Grab the latest `SimpleAutoDrive-vX.Y.Z.zip` from [Releases](../../releases).
2. Drop `SimpleAutoDrive.dll` into your game's `scripts\` folder.
3. Launch. `scripts\SimpleAutoDrive.ini` writes itself on first load.

## Configuration

`scripts\SimpleAutoDrive.ini`:

```ini
[MAIN]
ToggleKey=F6        # any System.Windows.Forms.Keys name
TierKey=F9          # tap to cycle aggression tier
DefaultTier=2       # 0 Cruise / 1 Brisk / 2 Hurried - tier at startup
ChauffeurMode=1     # 1 = NPC drives, you ride shotgun; 0 = you drive
SpeedCruise=16.0    # meters per second (km/h = value x 3.6)
SpeedBrisk=21.0
SpeedHurried=26.0
StyleCruise=786603       # civil (stops at lights)
StyleBrisk=1074528293    # SHVDN Rushed - passes when convenient
StyleHurried=1074528805  # + wrong-way-when-blocked
StopRange=35.0      # meters to destination to count as arrived
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
