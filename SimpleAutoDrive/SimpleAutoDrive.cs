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
    DateTime _lastTask = DateTime.MinValue;

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
        _styles[1] = cfg.GetValue("MAIN", "StyleHurried", 2883621); // rushed
        _styles[2] = cfg.GetValue("MAIN", "StyleInsane", 2883621);  // rushed
        _tier = cfg.GetValue("MAIN", "DefaultTier", 1);             // Hurried
        if (_tier < 0 || _tier > 2) _tier = 1;
        _stopRange = cfg.GetValue("MAIN", "StopRange", 15.0f);

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
        cfg.Save();

        Interval = 250;
        KeyDown += OnKeyDown;
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
            ToggleAutopilot();
        else if (e.KeyCode == _tierKey)
            CycleTier();
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

        if ((DateTime.UtcNow - _lastTask).TotalMilliseconds > 2000)
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
            if (!Function.Call<bool>(Hash.IS_WAYPOINT_ACTIVE))
            {
                Notification.Show("~y~Set a waypoint first");
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
        return TierNames[_tier] + " (~" + (int)(_speeds[_tier] * 3.6) + " km/h)";
    }

    Vector3 WaypointPos()
    {
        int blip = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
        if (!Function.Call<bool>(Hash.DOES_BLIP_EXIST, blip))
            return Vector3.Zero;
        return Function.Call<Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blip);
    }

    void Retask()
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

        Ped p = Game.Player.Character;
        Vehicle v = p.CurrentVehicle;
        if (v == null || !v.Exists()) { Stop(); return; }

        _lastTask = DateTime.UtcNow;
        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, p, v,
            _target.X, _target.Y, _target.Z, _speeds[_tier], _styles[_tier], _stopRange);
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
