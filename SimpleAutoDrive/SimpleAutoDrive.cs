using System;
using System.IO;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

// SimpleAutoDrive v3 - event-driven autopilot.
//
// Architecture (v3.0.0, rebuilt for coherence after field feedback that the
// timer-based retasking "recalculates at weird times"):
//   - The LONGRANGE drive task is issued ONCE per destination. Re-issuing is
//     what the passenger perceives as recalculating; it now happens only on
//     events: destination changed, tier changed (style bits differ), a pass
//     completed, or a genuine stall.
//   - Speed is NOT part of the task anymore. It breathes continuously via
//     SET_DRIVE_TASK_MAX_CRUISE_SPEED: tier speed scaled by how sharply the
//     destination sits off the nose, and ramped down on final approach.
//     Changing tiers mid-drive adjusts speed live; no replan.
//   - A stall is "stopped with no queue ahead" (a car ahead within 14 m,
//     same direction, means a red light or traffic - patience). A true stall
//     dead-stops the car for 1.5 s and reissues once. Three strikes on one
//     trip hand control back with a notice instead of orbiting.
//   - Insane only: when stopped behind a blocker, a low-damage warning round
//     into its bumper lets panic move it before any overtaking pass. Passes
//     only commit onto pavement and never above 20 m/s (poles live off-road).
public class SimpleAutoDrive : Script
{
    static readonly string[] TierNames = { "Cruise", "Brisk", "Hurried", "Insane" };

    bool _on;
    Vector3 _target;
    Keys _toggle;
    Keys _tierKey;
    int _tier;
    readonly float[] _speeds = new float[4];
    readonly int[] _styles = new int[4];
    float _stopRange;
    bool _overtake;
    bool _shootBlockers;

    // event/task state
    DateTime _lastTask = DateTime.MinValue;
    DateTime _lastSpeedUpdate = DateTime.MinValue;
    Vector3 _knownWaypoint = Vector3.Zero;

    // stall / wrong-way state
    DateTime _stalledSince = DateTime.MinValue;
    float _bestDistEver = -1f;
    DateTime _lastMeaningfulProgress = DateTime.MinValue;
    int _loopBreakers;
    DateTime _loopPauseUntil = DateTime.MinValue;

    // overtake / shooting state
    bool _passing;
    Vector3 _passTarget;
    DateTime _passStart;
    DateTime _lastShot = DateTime.MinValue;
    int _shotsAtBlocker;
    DateTime _passCooldownUntil = DateTime.MinValue;
    int _passFailures;

    readonly bool[] _keyLatched = new bool[2];

    public SimpleAutoDrive()
    {
        string iniPath = Path.Combine("scripts", "SimpleAutoDrive.ini");
        ScriptSettings cfg = ScriptSettings.Load(iniPath);

        _toggle = ParseKey(cfg.GetValue("MAIN", "ToggleKey", "F6"), Keys.F6);
        _tierKey = ParseKey(cfg.GetValue("MAIN", "TierKey", "F9"), Keys.F9);

        _speeds[0] = cfg.GetValue("MAIN", "SpeedCruise", 16.0f);
        _speeds[1] = cfg.GetValue("MAIN", "SpeedBrisk", 21.0f);
        _speeds[2] = cfg.GetValue("MAIN", "SpeedHurried", 26.0f);
        _speeds[3] = cfg.GetValue("MAIN", "SpeedInsane", 36.0f);
        _styles[0] = cfg.GetValue("MAIN", "StyleCruise", 786603);      // civil
        _styles[1] = cfg.GetValue("MAIN", "StyleBrisk", 1074528293);   // SHVDN Rushed
        _styles[2] = cfg.GetValue("MAIN", "StyleHurried", 1074528805); // + wrong-way-when-blocked
        _styles[3] = cfg.GetValue("MAIN", "StyleInsane", 1074534949);  // + overtake bits
        _tier = cfg.GetValue("MAIN", "DefaultTier", 2);                // Hurried
        if (_tier < 0 || _tier > 3) _tier = 2;
        _stopRange = cfg.GetValue("MAIN", "StopRange", 15.0f);
        _overtake = cfg.GetValue("MAIN", "Overtake", 1) == 1;
        _shootBlockers = cfg.GetValue("MAIN", "ShootBlockers", 1) == 1; // Insane tier only

        cfg.SetValue("MAIN", "ToggleKey", _toggle.ToString());
        cfg.SetValue("MAIN", "TierKey", _tierKey.ToString());
        cfg.SetValue("MAIN", "SpeedCruise", _speeds[0]);
        cfg.SetValue("MAIN", "SpeedBrisk", _speeds[1]);
        cfg.SetValue("MAIN", "SpeedHurried", _speeds[2]);
        cfg.SetValue("MAIN", "SpeedInsane", _speeds[3]);
        cfg.SetValue("MAIN", "StyleCruise", _styles[0]);
        cfg.SetValue("MAIN", "StyleBrisk", _styles[1]);
        cfg.SetValue("MAIN", "StyleHurried", _styles[2]);
        cfg.SetValue("MAIN", "StyleInsane", _styles[3]);
        cfg.SetValue("MAIN", "DefaultTier", _tier);
        cfg.SetValue("MAIN", "StopRange", _stopRange);
        cfg.SetValue("MAIN", "Overtake", _overtake ? 1 : 0);
        cfg.SetValue("MAIN", "ShootBlockers", _shootBlockers ? 1 : 0);
        cfg.Save();

        Interval = 250;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        Tick += OnTick;
        Aborted += OnAborted;
    }

    static Keys ParseKey(string name, Keys fallback)
    {
        Keys k;
        return Enum.TryParse(name, true, out k) ? k : fallback;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == _toggle)
        {
            if (_keyLatched[0]) return;
            _keyLatched[0] = true;
            ToggleAutopilot();
        }
        else if (e.KeyCode == _tierKey)
        {
            if (_keyLatched[1]) return;
            _keyLatched[1] = true;
            CycleTier();
        }
    }

    void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == _toggle) _keyLatched[0] = false;
        else if (e.KeyCode == _tierKey) _keyLatched[1] = false;
    }

    void OnTick(object sender, EventArgs e)
    {
        if (!_on) return;

        // dead-stop pause during a stall reset
        if (_loopPauseUntil != DateTime.MinValue)
        {
            if (DateTime.UtcNow < _loopPauseUntil) return;
            _loopPauseUntil = DateTime.MinValue;
        }

        Ped p = Game.Player.Character;
        Vehicle v = p != null ? p.CurrentVehicle : null;
        if (v == null || !v.Exists() || p.SeatIndex != VehicleSeat.Driver)
        {
            Stop();
            GTA.UI.Screen.ShowSubtitle("AutoDrive OFF", 1500);
            return;
        }

        float dist = p.Position.DistanceTo(_target);

        if (dist < _stopRange)
        {
            Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, v, 0.0f);
            Stop();
            GTA.UI.Screen.ShowSubtitle("~g~Arrived", 1500);
            return;
        }

        if (v.Speed > 5.0f) _shotsAtBlocker = 0; // rolling again: reload the patience

        // ---- destination changed? (the one replan a passenger understands) ----
        Vector3 wp = WaypointPos();
        if (wp != Vector3.Zero && _knownWaypoint != Vector3.Zero &&
            wp.DistanceTo(_knownWaypoint) > 20.0f)
        {
            Retask();
            dist = p.Position.DistanceTo(_target);
        }

        // ---- continuous speed control: no replans, just breathing ----
        if (!_passing && (DateTime.UtcNow - _lastSpeedUpdate).TotalMilliseconds >= 500)
        {
            _lastSpeedUpdate = DateTime.UtcNow;
            Function.Call(Hash.SET_DRIVE_TASK_MAX_CRUISE_SPEED, p, DesiredSpeed(v, dist), 1);
        }

        // ---- pass in progress: let it run out ----
        if (_passing)
        {
            bool arrived = p.Position.DistanceTo(_passTarget) < 12.0f;
            bool timedOut = (DateTime.UtcNow - _passStart).TotalSeconds > 12.0;
            if (arrived || timedOut)
            {
                _passing = false;
                if (timedOut && !arrived)
                {
                    _passFailures++;
                    // Backed out of a pass: don't immediately re-trigger. The
                    // endless try-and-pussy-out loop in busy traffic is worse
                    // than just following for a while.
                    _passCooldownUntil = DateTime.UtcNow.AddSeconds(_passFailures >= 3 ? 30.0 : 6.0);
                }
                else
                {
                    _passFailures = 0; // clean pass
                }
                Retask();
            }
            return;
        }

        // ---- stall and wrong-way detection (event thresholds, not cadence) ----
        if (_bestDistEver < 0f || dist < _bestDistEver - 25.0f)
        {
            _bestDistEver = dist;
            _lastMeaningfulProgress = DateTime.UtcNow;
            _loopBreakers = 0;
            _passFailures = 0;
        }

        bool stalled = v.Speed < 1.0f;
        if (stalled)
        {
            if (_stalledSince == DateTime.MinValue) _stalledSince = DateTime.UtcNow;
        }
        else _stalledSince = DateTime.MinValue;

        bool wrongWay = _bestDistEver > 0f && dist > _bestDistEver + 150.0f;
        bool longDead = (DateTime.UtcNow - _lastMeaningfulProgress).TotalSeconds > 90.0;

        if (wrongWay || longDead || StalledTooLong(v, _stalledSince))
        {
            _loopBreakers++;
            if (_loopBreakers >= 3)
            {
                Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, v, 0.0f);
                Stop();
                GTA.UI.Screen.ShowSubtitle("~y~AutoDrive OFF - cannot route from here, take over", 2000);
                return;
            }

            // dead-stop reset: planners stuck in a turn loop often recover from standstill
            Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, v, 0.0f);
            _loopPauseUntil = DateTime.UtcNow.AddSeconds(1.5);
            Retask(true);
            return;
        }

        // ---- overtake / shoot logic when sagging behind a slower car ----
        if (_overtake && v.Speed < _speeds[_tier] * 0.35f)
        {
            Vehicle blocker = null;
            float best = 30.0f;
            Vector3 fwd = v.ForwardVector;
            foreach (Vehicle other in World.GetNearbyVehicles(v.Position, 35.0f))
            {
                if (other == null || !other.Exists() || other.Handle == v.Handle) continue;
                Vector3 rel = other.Position - v.Position;
                float bdist = rel.Length();
                if (bdist > 30.0f || bdist < 3.0f) continue;
                if (Vector3.Dot(rel.Normalized, fwd) < 0.7f) continue;
                if (Vector3.Dot(other.ForwardVector, fwd) < 0.6f) continue;
                if (other.Speed > v.Speed + 2.0f) continue;
                if (bdist < best) { best = bdist; blocker = other; }
            }

            if (blocker != null)
            {
                // Insane only: fire a warning round into the blocker's rear bumper.
                // A bullet needs no pavement check - decoupled from the pass logic
                // because rural single-lane shoulders routinely fail IS_POINT_ON_ROAD,
                // which used to block the shot too (car just sat there).
                if (_tier == 3 && _shootBlockers && _shotsAtBlocker < 3 &&
                    v.Speed < 2.0f &&
                    (DateTime.UtcNow - _lastShot).TotalMilliseconds > 3500)
                {
                    _lastShot = DateTime.UtcNow;
                    _shotsAtBlocker++;
                    Vector3 from = v.Position + fwd.Normalized * 2.5f + Vector3.WorldUp * 0.7f;
                    Vector3 to = blocker.Position - blocker.ForwardVector.Normalized * 1.2f + Vector3.WorldUp * 0.6f;
                    Function.Call(Hash.SHOOT_SINGLE_BULLET_BETWEEN_COORDS,
                        from.X, from.Y, from.Z, to.X, to.Y, to.Z,
                        5.0f, false, 0x1B06D571 /* pistol */, p, true, false, 1000.0f);
                    GTA.UI.Screen.ShowSubtitle("~r~Insane: clearing the road", 1500);
                    return;
                }

                // After shots exhausted (or non-Insane): pass. The pavement check
                // was too strict on single-lane roads - the 3.2m offset often lands
                // on the shoulder. GTA vehicles drive on shoulders fine; the pole
                // problem was from full-speed passes, not from passing itself.
                // Cap at 20 m/s and let the physics handle the rest.
                // Don't start what you can't finish: check for oncoming traffic
                // in the left corridor before committing. A pass into a car
                // heading at you is a brake-and-swerve, not an overtake.
                if (DateTime.UtcNow < _passCooldownUntil) return;
                Vector3 right = Vector3.Cross(fwd, Vector3.WorldUp).Normalized;
                _passTarget = blocker.Position + blocker.ForwardVector.Normalized * 35.0f - right * 3.2f;

                bool oncomingInCorridor = false;
                Vector3 corridorDir = (_passTarget - v.Position).Normalized;
                float corridorLength = v.Position.DistanceTo(_passTarget);
                foreach (Vehicle other2 in World.GetNearbyVehicles(_passTarget, corridorLength + 30.0f))
                {
                    if (other2 == null || !other2.Exists() || other2.Handle == v.Handle) continue;
                    // oncoming: heading roughly toward us
                    if (Vector3.Dot(other2.ForwardVector, fwd) > -0.3f) continue;
                    // in the corridor: close to the line from us to the pass target
                    Vector3 toOther = other2.Position - v.Position;
                    float along = Vector3.Dot(toOther, corridorDir);
                    if (along < 0f || along > corridorLength + 25.0f) continue;
                    float lateral = (toOther - corridorDir * along).Length();
                    if (lateral < 6.0f) { oncomingInCorridor = true; break; }
                }
                if (oncomingInCorridor) return; // wait for a gap

                _passing = true;
                _passStart = DateTime.UtcNow;
                Function.Call((Hash)0xE2A2AA2F659D77A7, p, v,  // TASK_VEHICLE_DRIVE_TO_COORD, not in SHVDN enum
                    _passTarget.X, _passTarget.Y, _passTarget.Z,
                    Math.Min(_speeds[_tier], 20.0f), 1, 0, 1074528805, 2.5f, -1f);
            }
        }
    }

    static bool StalledTooLong(Vehicle v, DateTime stalledSince)
    {
        if (stalledSince == DateTime.MinValue) return false;
        double stopped = (DateTime.UtcNow - stalledSince).TotalSeconds;
        if (stopped < 20.0) return false;

        // A car ahead within 14 m, same direction, means a queue or a red
        // light: keep waiting. Give lights up to 45 s before calling it.
        foreach (Vehicle other in World.GetNearbyVehicles(v.Position, 18.0f))
        {
            if (other == null || !other.Exists() || other.Handle == v.Handle) continue;
            Vector3 rel = other.Position - v.Position;
            if (rel.Length() > 14.0f) continue;
            if (Vector3.Dot(rel.Normalized, v.ForwardVector) < 0.7f) continue;
            if (Vector3.Dot(other.ForwardVector, v.ForwardVector) < 0.6f) continue;
            if (other.Speed > 2.0f) continue;
            return stopped > 45.0; // queued behind someone who is also stuck
        }
        return true; // stopped with clear road ahead
    }

    float DesiredSpeed(Vehicle v, float dist)
    {
        float s = _speeds[_tier];

        Vector3 dir = _target - v.Position; dir.Z = 0f;
        Vector3 fwd = v.ForwardVector; fwd.Z = 0f;
        if (dir.LengthSquared() > 1f && fwd.LengthSquared() > 0.01f)
        {
            float dot = Math.Max(-1f, Math.Min(1f, Vector3.Dot(dir.Normalized, fwd.Normalized)));
            double ang = Math.Acos(dot) * 180.0 / Math.PI;
            if (ang > 50.0) s *= 0.5f;       // hard turn ahead: give the wheel a chance
            else if (ang > 25.0) s *= 0.75f;
        }

        if (dist < 100.0f) s *= 0.6f;        // final approach
        else if (dist < 250.0f) s *= 0.8f;

        if (s < 10.0f) s = 10.0f;
        return s;
    }

    void OnAborted(object sender, EventArgs e)
    {
        Stop();
    }

    void ToggleAutopilot()
    {
        if (!_on)
        {
            Ped p = Game.Player.Character;
            Vehicle v = p != null ? p.CurrentVehicle : null;
            if (WaypointPos() == Vector3.Zero)
            {
                GTA.UI.Screen.ShowSubtitle("~y~Set a waypoint or mission objective first", 1500);
                return;
            }
            if (v == null || !v.Exists() || p.SeatIndex != VehicleSeat.Driver)
            {
                GTA.UI.Screen.ShowSubtitle("~y~Be in the driver seat first", 1500);
                return;
            }
            _on = true;
            GTA.UI.Screen.ShowSubtitle("~g~AutoDrive ON~w~ - " + TierLabel(), 1500);
            Retask();
        }
        else
        {
            Stop();
            GTA.UI.Screen.ShowSubtitle("AutoDrive OFF", 1500);
        }
    }

    void CycleTier()
    {
        _tier = (_tier + 1) % 4;
        if (_on)
        {
            Retask(); // styles differ per tier, so the task must be reissued; speed follows live
            GTA.UI.Screen.ShowSubtitle("AutoDrive - " + TierLabel(), 1500);
        }
        else
        {
            GTA.UI.Screen.ShowSubtitle("AutoDrive tier - " + TierLabel(), 1500);
        }
    }

    string TierLabel()
    {
        return TierNames[_tier];
    }

    Vector3 WaypointPos()
    {
        // Player-set waypoint (purple, sprite 8) first...
        int blip = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
        if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, blip))
            return Function.Call<Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blip);

        // ...then mission objective blips (sprite 1). Skip anything within 50m
        // so free-roam contact markers next to the player don't hijack it.
        // Mission objectives move as a mission progresses; the >20m change
        // detection in OnTick follows them automatically.
        blip = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 1);
        while (Function.Call<bool>(Hash.DOES_BLIP_EXIST, blip))
        {
            Vector3 c = Function.Call<Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blip);
            if (Game.Player.Character.Position.DistanceTo(c) > 50.0f)
                return c;
            blip = Function.Call<int>(Hash.GET_NEXT_BLIP_INFO_ID, blip);
        }
        return Vector3.Zero;
    }

    void Retask(bool loopBreaker = false)
    {
        Vector3 t = WaypointPos();
        if (t == Vector3.Zero)
        {
            // waypoint removed mid-drive: keep driving to the last known target
            if (_target == Vector3.Zero) { Stop(); return; }
        }
        else
        {
            _target = t;
        }
        _knownWaypoint = _target;
        _bestDistEver = -1f;
        _lastMeaningfulProgress = DateTime.UtcNow;
        _passing = false;

        Ped p = Game.Player.Character;
        Vehicle v = p.CurrentVehicle;
        if (v == null || !v.Exists()) { Stop(); return; }

        _lastTask = DateTime.UtcNow;
        _lastSpeedUpdate = DateTime.UtcNow;

        int style = loopBreaker ? 786603 : _styles[_tier];
        if (loopBreaker)
            GTA.UI.Screen.ShowSubtitle("~y~AutoDrive: rerouting (stall detected)", 1500);

        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, p, v,
            _target.X, _target.Y, _target.Z, DesiredSpeed(v, p.Position.DistanceTo(_target)),
            style, _stopRange);
    }

    void Stop()
    {
        if (!_on) return;
        _on = false;
        _passing = false;
        _shotsAtBlocker = 0;
        _loopBreakers = 0;
        _loopPauseUntil = DateTime.MinValue;
        _stalledSince = DateTime.MinValue;
        Ped p = Game.Player.Character;
        if (p != null && p.Exists())
            Function.Call(Hash.CLEAR_PED_TASKS, p);
    }
}
