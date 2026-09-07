using System;
using System.IO;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;

// SimpleAutoDrive - one-key waypoint autopilot for GTA V Enhanced (SHVDN3).
// Tap F6 to start/stop, tap F9 to cycle the aggression tier
// (Cruise / Hurried / Insane; applies immediately, even mid-drive).
// Tier is a tap key, not a hold key: vanilla GTA binds hold-F6 to the
// character switch wheel, so holds on F6 fight the game.
// The drive task is reissued every 2s from the car's current position
// so it can never follow a stale route.
public class SimpleAutoDrive : Script
{
    static readonly string[] TierNames = { "Cruise", "Hurried", "Insane" };

    bool _on;
    Vector3 _target;
    Keys _toggle;
    Keys _tierKey;
    int _tier;
    readonly float[] _speeds = new float[3];
    readonly int[] _styles = new int[3];
    float _stopRange;
    int _taskIntervalMs;
    DateTime _lastTask = DateTime.MinValue;
    float _bestDist = -1f;
    DateTime _lastProgressAt = DateTime.MinValue;
    DateTime _stuckSince = DateTime.MinValue;
    Vector3 _knownWaypoint = Vector3.Zero;
    int _noProgressRetasks;
    float _distAtLastRetask = -1f;

    public SimpleAutoDrive()
    {
        string iniPath = Path.Combine("scripts", "SimpleAutoDrive.ini");
        ScriptSettings cfg = ScriptSettings.Load(iniPath);

        _toggle = ParseKey(cfg.GetValue("MAIN", "ToggleKey", "F6"), Keys.F6);
        _tierKey = ParseKey(cfg.GetValue("MAIN", "TierKey", "F9"), Keys.F9);

        _speeds[0] = cfg.GetValue("MAIN", "SpeedCruise", 17.0f);
        _speeds[1] = cfg.GetValue("MAIN", "SpeedHurried", 26.0f);
        _speeds[2] = cfg.GetValue("MAIN", "SpeedInsane", 36.0f);
        _styles[0] = cfg.GetValue("MAIN", "StyleCruise", 786603);   // civil
        _styles[1] = cfg.GetValue("MAIN", "StyleHurried", 1074528805);   // Rushed + wrong-way-when-blocked
        _styles[2] = cfg.GetValue("MAIN", "StyleInsane", 1074534949);   // + wrong-way + overtake-left/right bits (2048|4096)
        _tier = cfg.GetValue("MAIN", "DefaultTier", 1);             // Hurried
        if (_tier < 0 || _tier > 2) _tier = 1;
        _stopRange = cfg.GetValue("MAIN", "StopRange", 15.0f);
        _taskIntervalMs = cfg.GetValue("MAIN", "TaskIntervalMs", 0); // 0 = off; any heartbeat is a periodic mid-maneuver reset

        cfg.SetValue("MAIN", "ToggleKey", _toggle.ToString());
        cfg.SetValue("MAIN", "TierKey", _tierKey.ToString());
        cfg.SetValue("MAIN", "SpeedCruise", _speeds[0]);
        cfg.SetValue("MAIN", "SpeedHurried", _speeds[1]);
        cfg.SetValue("MAIN", "SpeedInsane", _speeds[2]);
        cfg.SetValue("MAIN", "StyleCruise", _styles[0]);
        cfg.SetValue("MAIN", "StyleHurried", _styles[1]);
        cfg.SetValue("MAIN", "StyleInsane", _styles[2]);
        cfg.SetValue("MAIN", "DefaultTier", _tier);
        cfg.SetValue("MAIN", "StopRange", _stopRange);
        cfg.SetValue("MAIN", "TaskIntervalMs", _taskIntervalMs);
        cfg.Save();

        Interval = 100;
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

    readonly bool[] _keyLatched = new bool[2]; // [0]=toggle, [1]=tier; one action per physical press

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

        Ped p = Game.Player.Character;
        Vehicle v = p != null ? p.CurrentVehicle : null;
        if (v == null || !v.Exists() || p.SeatIndex != VehicleSeat.Driver)
        {
            Stop();
            Notification.Show("AutoDrive OFF");
            return;
        }

        if (p.Position.DistanceTo(_target) < _stopRange)
        {
            Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, v, 0.0f);
            Stop();
            Notification.Show("~g~Arrived");
            return;
        }

        // Retask on evidence, not on a blind timer: a timer that fires mid-maneuver
        // aborts overtakes and re-issues turns the car is already taking.
        // Heartbeat, waypoint change, stuck-in-place, or losing ground.
        Vector3 wp = WaypointPos();
        if (wp != Vector3.Zero && _knownWaypoint != Vector3.Zero &&
            wp.DistanceTo(_knownWaypoint) > 20.0f)
        {
            Retask(); // picks up the new waypoint
        }

        float dist = p.Position.DistanceTo(_target);

        if (v.Speed < 2.0f)
        {
            if (_stuckSince == DateTime.MinValue) _stuckSince = DateTime.UtcNow;
            else if ((DateTime.UtcNow - _stuckSince).TotalSeconds > 12.0)
            {
                Retask();
                _stuckSince = DateTime.UtcNow;
            }
        }
        else _stuckSince = DateTime.MinValue;

        if (_bestDist < 0f || dist < _bestDist - 5.0f)
        {
            _bestDist = dist;
            _lastProgressAt = DateTime.UtcNow;
            if (_distAtLastRetask - dist > 40.0f) _noProgressRetasks = 0;
        }
        if ((DateTime.UtcNow - _lastProgressAt).TotalSeconds > 25.0)
        {
            // 25s without getting 5m closer. City routes legitimately wander sideways
            // for a while; 25s/5m only trips on genuinely losing ground or looping.
            _noProgressRetasks++;
            bool looped = _noProgressRetasks >= 3;
            Retask(looped);
            if (looped) _noProgressRetasks = 0;
            _bestDist = -1f;
        }

        if (_taskIntervalMs > 0 &&
            (DateTime.UtcNow - _lastTask).TotalMilliseconds > _taskIntervalMs)
            Retask();
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
                Notification.Show("~y~Set a waypoint or mission objective first");
                return;
            }
            if (v == null || !v.Exists() || p.SeatIndex != VehicleSeat.Driver)
            {
                Notification.Show("~y~Be in the driver seat first");
                return;
            }
            _on = true;
            Notification.Show("~g~AutoDrive ON~w~ - " + TierLabel());
            Retask();
        }
        else
        {
            Stop();
            Notification.Show("AutoDrive OFF");
        }
    }

    void CycleTier()
    {
        _tier = (_tier + 1) % 3;
        if (_on)
        {
            Retask();
            Notification.Show("AutoDrive - " + TierLabel());
        }
        else
        {
            Notification.Show("AutoDrive tier - " + TierLabel());
        }
    }

    string TierLabel()
    {
        return TierNames[_tier] + " (" + (int)(_speeds[_tier] * 3.6) + " km/h)";
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
        _bestDist = -1f;
        _lastProgressAt = DateTime.UtcNow;

        Ped p = Game.Player.Character;
        Vehicle v = p.CurrentVehicle;
        if (v == null || !v.Exists()) { Stop(); return; }

        _lastTask = DateTime.UtcNow;
        _distAtLastRetask = p.Position.DistanceTo(_target);

        // Loop breaker: three no-progress re-issues in a row means the route or the
        // aggressive style itself is circling. Drop to civil style for one task -
        // the wrong-way-when-blocked bit is the usual junction-circling culprit.
        int style = loopBreaker ? 786603 : _styles[_tier];
        if (loopBreaker)
            Notification.Show("~y~AutoDrive: rerouting (loop detected)");

        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, p, v,
            _target.X, _target.Y, _target.Z, _speeds[_tier], style, _stopRange);
    }

    void Stop()
    {
        if (!_on) return;
        _on = false;
        Ped p = Game.Player.Character;
        if (p != null && p.Exists())
            Function.Call(Hash.CLEAR_PED_TASKS, p);
    }
}
