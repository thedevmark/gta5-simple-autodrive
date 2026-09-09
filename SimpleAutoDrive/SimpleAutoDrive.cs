using System;
using System.IO;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

// SimpleAutoDrive v7 — the proven recipe.
//
// v6's line-following architecture (route sampling, segment streaming,
// direct-steer turnaround branches) caused wall crashes: a direct-steer
// task with a large straightLineDistance steers STRAIGHT at its target
// regardless of geometry, and re-issued short segments kept disturbing
// the task's own route commitment. All of it is removed.
//
// v7 is the same model as the long-running community autopilots: ONE
// TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE to the destination with a speed
// and a driving style, re-issued ONLY when the destination or tier
// changes. The engine's pathfinder follows the road graph end to end —
// it never beelines through geometry. Arrival is a distance check, so
// unreachable waypoint interiors still end the trip cleanly at the
// nearest road. The chauffeur layer (invisible max-skill NPC drives,
// you ride as passenger) is kept — field-log verified working.
public class SimpleAutoDrive : Script
{
    static readonly string[] TierNames = { "Cruise", "Brisk", "Hurried" };

    bool _on;
    bool _chauffeurMode;
    Vector3 _target;
    Keys _toggle;
    Keys _tierKey;
    int _tier;
    readonly float[] _speeds = new float[3];
    readonly int[] _styles = new int[3];
    float _stopRange;
    Vector3 _knownWaypoint = Vector3.Zero;
    DateTime _startedAt = DateTime.MinValue;
    int _slot;

    Ped _driver;
    readonly bool[] _keyLatched = new bool[2];

    public SimpleAutoDrive()
    {
        string iniPath = Path.Combine("scripts", "SimpleAutoDrive.ini");
        ScriptSettings cfg = ScriptSettings.Load(iniPath);

        _toggle = ParseKey(cfg.GetValue("MAIN", "ToggleKey", "F6"), Keys.F6);
        _tierKey = ParseKey(cfg.GetValue("MAIN", "TierKey", "F9"), Keys.F9);
        _chauffeurMode = cfg.GetValue("MAIN", "ChauffeurMode", 1) == 1;

        _speeds[0] = cfg.GetValue("MAIN", "SpeedCruise", 16.0f);
        _speeds[1] = cfg.GetValue("MAIN", "SpeedBrisk", 21.0f);
        _speeds[2] = cfg.GetValue("MAIN", "SpeedHurried", 26.0f);
        _styles[0] = cfg.GetValue("MAIN", "StyleCruise", 786603);
        _styles[1] = cfg.GetValue("MAIN", "StyleBrisk", 1074528293);
        _styles[2] = cfg.GetValue("MAIN", "StyleHurried", 1074528805);
        _tier = cfg.GetValue("MAIN", "DefaultTier", 2);
        if (_tier < 0 || _tier > 2) _tier = 2;
        _stopRange = cfg.GetValue("MAIN", "StopRange", 35.0f);

        cfg.SetValue("MAIN", "ToggleKey", _toggle.ToString());
        cfg.SetValue("MAIN", "TierKey", _tierKey.ToString());
        cfg.SetValue("MAIN", "ChauffeurMode", _chauffeurMode ? 1 : 0);
        cfg.SetValue("MAIN", "SpeedCruise", _speeds[0]);
        cfg.SetValue("MAIN", "SpeedBrisk", _speeds[1]);
        cfg.SetValue("MAIN", "SpeedHurried", _speeds[2]);
        cfg.SetValue("MAIN", "StyleCruise", _styles[0]);
        cfg.SetValue("MAIN", "StyleBrisk", _styles[1]);
        cfg.SetValue("MAIN", "StyleHurried", _styles[2]);
        cfg.SetValue("MAIN", "DefaultTier", _tier);
        cfg.SetValue("MAIN", "StopRange", _stopRange);
        cfg.Save();

        Interval = 500;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        Tick += OnTick;
        Aborted += OnAborted;
    }

    static void Log(string s)
    {
        try
        {
            File.AppendAllText(Path.Combine("scripts", "SimpleAutoDrive.log"),
                DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine);
        }
        catch { }
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

        Vehicle v = GetActiveVehicle();

        if (v == null || !v.Exists())
        {
            Log("auto-stop: vehicle gone");
            Stop();
            GTA.UI.Screen.ShowSubtitle("AutoDrive OFF", 1500);
            return;
        }

        Ped p = Game.Player.Character;
        if ((p == null || !p.Exists() || p.CurrentVehicle == null ||
            p.CurrentVehicle.Handle != v.Handle) &&
            (DateTime.UtcNow - _startedAt).TotalSeconds > 2.0)
        {
            Log("auto-stop: player not in vehicle");
            Stop();
            GTA.UI.Screen.ShowSubtitle("AutoDrive OFF", 1500);
            return;
        }

        if (v.Position.DistanceTo(_target) < _stopRange)
        {
            Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, v, 0.0f);
            Log("arrived");
            Stop();
            GTA.UI.Screen.ShowSubtitle("~g~Arrived", 2000);
            return;
        }

        // the ONLY re-issuing trigger: the destination itself moved
        Vector3 wp = WaypointPos();
        if (wp != Vector3.Zero && _knownWaypoint != Vector3.Zero &&
            wp.DistanceTo(_knownWaypoint) > 20.0f)
        {
            Log("re-task: destination moved");
            TaskTo();
        }
    }

    // One long-range task to the destination. Nothing else touches the
    // driving — no speed interference, no re-issues, no route surgery.
    void TaskTo()
    {
        Vector3 raw = WaypointPos();
        if (raw == Vector3.Zero)
        {
            if (_target == Vector3.Zero) { Stop(); return; }
        }
        else
        {
            _target = raw;
        }
        _knownWaypoint = _target;

        Ped driver = GetActiveDriver();
        if (driver == null || !driver.Exists()) { Stop(); return; }
        Vehicle v = driver.CurrentVehicle;
        if (v == null || !v.Exists()) { Stop(); return; }

        Function.Call(Hash.SET_DRIVER_ABILITY, driver, 1.0f);
        Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, 1.0f);

        Log("task: longrange " + (int)v.Position.DistanceTo(_target) +
            "m slot " + _slot + " tier " + _tier);

        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, driver, v,
            _target.X, _target.Y, _target.Z, _speeds[_tier], _styles[_tier], _stopRange);
    }

    Vehicle GetActiveVehicle()
    {
        if (_chauffeurMode && _driver != null && _driver.Exists())
            return _driver.CurrentVehicle;
        Ped p = Game.Player.Character;
        return p != null ? p.CurrentVehicle : null;
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
                GTA.UI.Screen.ShowSubtitle("~y~Set a waypoint first", 1500);
                return;
            }
            if (v == null || !v.Exists() || !v.IsDriveable)
            {
                GTA.UI.Screen.ShowSubtitle("~y~Be in a vehicle first", 1500);
                return;
            }

            _on = true;
            _startedAt = DateTime.UtcNow;

            if (_chauffeurMode)
                StartChauffeur(p, v);

            GTA.UI.Screen.ShowSubtitle("~g~AutoDrive ON~w~ - " + TierLabel(), 1500);
            TaskTo();
        }
        else
        {
            Stop();
            GTA.UI.Screen.ShowSubtitle("AutoDrive OFF", 1500);
        }
    }

    void StartChauffeur(Ped player, Vehicle vehicle)
    {
        // seat the player somewhere that isn't the driver seat — some
        // vehicles (garbage/service trucks) have no passenger seat at all
        VehicleSeat seat = VehicleSeat.None;
        if (vehicle.IsSeatFree(VehicleSeat.Passenger)) seat = VehicleSeat.Passenger;
        else if (vehicle.IsSeatFree(VehicleSeat.LeftRear)) seat = VehicleSeat.LeftRear;
        else if (vehicle.IsSeatFree(VehicleSeat.RightRear)) seat = VehicleSeat.RightRear;

        if (seat == VehicleSeat.None)
        {
            _chauffeurMode = false;
            Log("chauffeur: no free seat, player-drive mode");
            GTA.UI.Screen.ShowSubtitle("~y~No free seat - you drive", 1500);
            return;
        }
        Log("chauffeur: seat " + (int)seat);
        Function.Call(Hash.SET_PED_INTO_VEHICLE, player, vehicle, (int)seat);

        // spawn driver
        _driver = vehicle.CreatePedOnSeat(VehicleSeat.Driver, PedHash.Blackops01SMY);
        if (_driver == null || !_driver.Exists())
        {
            Log("chauffeur: CreatePedOnSeat failed, street fallback");
            _driver = World.CreatePed(PedHash.Blackops01SMY, vehicle.Position + vehicle.RightVector * 3f);
            if (_driver != null && _driver.Exists())
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _driver, vehicle, (int)VehicleSeat.Driver);
        }

        if (_driver == null || !_driver.Exists())
        {
            _chauffeurMode = false;
            Log("chauffeur: NPC creation failed entirely, player-drive mode");
            Function.Call(Hash.SET_PED_INTO_VEHICLE, player, vehicle, (int)VehicleSeat.Driver);
            GTA.UI.Screen.ShowSubtitle("~y~Chauffeur failed, player-drive mode", 1500);
            return;
        }

        _driver.IsPersistent = true;
        _driver.BlockPermanentEvents = true;

        // let the player control the radio from the passenger seat
        Function.Call(Hash.SET_VEH_HAS_NORMAL_RADIO, vehicle);
        _driver.CanBeDraggedOutOfVehicle = false;
        _driver.CanBeTargetted = false;
        _driver.IsInvincible = true;
        Function.Call(Hash.SET_ENTITY_VISIBLE, _driver, false);
        Function.Call(Hash.SET_DRIVER_ABILITY, _driver, 1.0f);
        Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, _driver, 1.0f);
        Log("chauffeur: npc up");
    }

    void CycleTier()
    {
        _tier = (_tier + 1) % 3;
        if (_on)
        {
            TaskTo();
        }
        GTA.UI.Screen.ShowSubtitle("AutoDrive - " + TierLabel(), 1500);
    }

    Ped GetActiveDriver()
    {
        if (_chauffeurMode && _driver != null && _driver.Exists())
            return _driver;
        return Game.Player.Character;
    }

    string TierLabel()
    {
        return TierNames[_tier] + " (" + (int)(_speeds[_tier] * 3.6) + " km/h)";
    }

    Vector3 WaypointPos()
    {
        int blip = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 8);
        if (Function.Call<bool>(Hash.DOES_BLIP_EXIST, blip))
        {
            _slot = 0;
            return Function.Call<Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blip);
        }

        blip = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, 1);
        while (Function.Call<bool>(Hash.DOES_BLIP_EXIST, blip))
        {
            Vector3 c = Function.Call<Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blip);
            if (Game.Player.Character.Position.DistanceTo(c) > 50.0f)
            {
                _slot = 1;
                return c;
            }
            blip = Function.Call<int>(Hash.GET_NEXT_BLIP_INFO_ID, blip);
        }
        return Vector3.Zero;
    }

    void Stop()
    {
        if (!_on) return;
        _on = false;

        Ped p = Game.Player.Character;

        if (_chauffeurMode && _driver != null && _driver.Exists())
        {
            Vehicle v = _driver.CurrentVehicle;
            // delete the NPC first to free the driver seat, then warp the
            // player in — ONLY if they're actually in this car
            _driver.Delete();
            _driver = null;
            if (v != null && v.Exists() && p != null && p.Exists() &&
                p.CurrentVehicle != null && p.CurrentVehicle.Handle == v.Handle)
            {
                Function.Call(Hash.SET_PED_INTO_VEHICLE, p, v, (int)VehicleSeat.Driver);
            }
        }
        else if (p != null && p.Exists())
        {
            Function.Call(Hash.CLEAR_PED_TASKS, p);
        }
    }
}
