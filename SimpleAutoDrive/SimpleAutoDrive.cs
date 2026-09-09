using System;
using System.IO;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

// SimpleAutoDrive v5.1 — NPC chauffeur mode.
//
// The biggest UX change: F6 spawns an invisible NPC driver, moves you to
// the passenger seat, and drives you to the waypoint. You can shoot,
// use your phone, watch the world go by. On arrival or toggle-off, the
// NPC vanishes and you're back in the driver's seat. The NPC has max
// driver ability (willing to make left turns, tight maneuvers) which
// fixes the routing issues from v3-v4 where the player ped's low default
// driver skill caused loops and intersection overshoots.
//
// Player-drive mode (ChauffeurMode=0 in ini) keeps the v4 behavior.
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
    bool _overtake;
    Vector3 _knownWaypoint = Vector3.Zero;
    float _minDist = float.MaxValue;
    DateTime _lastTaskAt = DateTime.MinValue;
    int _forcedRetasks;

    Ped _driver;
    bool _passing;
    Vector3 _passTarget;
    DateTime _passStart;
    DateTime _passCooldownUntil = DateTime.MinValue;
    int _passFailures;

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
        _overtake = cfg.GetValue("MAIN", "Overtake", 1) == 1;

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
        cfg.SetValue("MAIN", "Overtake", _overtake ? 1 : 0);
        cfg.Save();

        Interval = 500;
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

        Ped p = Game.Player.Character;
        Vehicle v = GetActiveVehicle();

        if (v == null || !v.Exists())
        {
            Stop();
            GTA.UI.Screen.ShowSubtitle("AutoDrive OFF", 1500);
            return;
        }

        float dist = v.Position.DistanceTo(_target);

        if (dist < _stopRange)
        {
            Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, v, 0.0f);
            Stop();
            GTA.UI.Screen.ShowSubtitle("~g~Arrived", 2000);
            return;
        }

        Vector3 wp = WaypointPos();
        if (wp != Vector3.Zero && _knownWaypoint != Vector3.Zero &&
            wp.DistanceTo(_knownWaypoint) > 20.0f)
        {
            _forcedRetasks = 0;
            Retask();
            return;
        }

        // Wrong-way watchdog: net distance ballooning while pointed away from
        // the target means a missed turn — force one replan. Budgeted so a
        // legitimate divided-highway turnaround (drive away to reach the next
        // crossover) is never fought to death.
        if (dist < _minDist) _minDist = dist;
        if (dist > _minDist + 70.0f && _forcedRetasks < 3 &&
            (DateTime.UtcNow - _lastTaskAt).TotalSeconds > 8.0)
        {
            Vector3 wdir = _target - v.Position; wdir.Z = 0f;
            Vector3 wfwd = v.ForwardVector; wfwd.Z = 0f;
            if (wdir.LengthSquared() > 1f && wfwd.LengthSquared() > 0.01f &&
                Vector3.Dot(wdir.Normalized, wfwd.Normalized) < -0.25f)
            {
                _forcedRetasks++;
                _minDist = dist;
                Retask();
                return;
            }
        }

        if (_passing)
        {
            bool arrived = v.Position.DistanceTo(_passTarget) < 12.0f;
            bool timedOut = (DateTime.UtcNow - _passStart).TotalSeconds > 12.0;
            if (arrived || timedOut)
            {
                _passing = false;
                if (timedOut && !arrived)
                {
                    _passFailures++;
                    _passCooldownUntil = DateTime.UtcNow.AddSeconds(_passFailures >= 3 ? 30.0 : 6.0);
                }
                else _passFailures = 0;
                Retask();
            }
            return;
        }

        if (_overtake && v.Speed < _speeds[_tier] * 0.70f)
        {
            TryOvertake(v);
        }
    }

    Vehicle GetActiveVehicle()
    {
        if (_chauffeurMode && _driver != null && _driver.Exists())
            return _driver.CurrentVehicle;
        Ped p = Game.Player.Character;
        return p != null ? p.CurrentVehicle : null;
    }

    void TryOvertake(Vehicle v)
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

        if (blocker == null) return;
        if (DateTime.UtcNow < _passCooldownUntil) return;

        Vector3 right = Vector3.Cross(fwd, Vector3.WorldUp).Normalized;
        _passTarget = blocker.Position + blocker.ForwardVector.Normalized * 35.0f - right * 3.2f;
        Vector3 corridorDir = (_passTarget - v.Position).Normalized;
        float corridorLength = v.Position.DistanceTo(_passTarget);

        foreach (Vehicle other2 in World.GetNearbyVehicles(_passTarget, corridorLength + 30.0f))
        {
            if (other2 == null || !other2.Exists() || other2.Handle == v.Handle) continue;
            if (Vector3.Dot(other2.ForwardVector, fwd) > -0.3f) continue;
            Vector3 toOther = other2.Position - v.Position;
            float along = Vector3.Dot(toOther, corridorDir);
            if (along < 0f || along > corridorLength + 25.0f) continue;
            float lateral = (toOther - corridorDir * along).Length();
            if (lateral < 3.5f) return;
        }

        Ped driver = GetActiveDriver();
        if (driver == null || !driver.Exists()) return;

        _passing = true;
        _passStart = DateTime.UtcNow;
        Function.Call((Hash)0xE2A2AA2F659D77A7, driver, v,
            _passTarget.X, _passTarget.Y, _passTarget.Z,
            Math.Min(_speeds[_tier], 20.0f), 1, v.Model.Hash,
            1074528805, 2.5f, 60.0f);
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
            _forcedRetasks = 0;

            if (_chauffeurMode)
                StartChauffeur(p, v);

            GTA.UI.Screen.ShowSubtitle("~g~AutoDrive ON~w~ - " + TierLabel(), 1500);
            Retask();
        }
        else
        {
            Stop();
            GTA.UI.Screen.ShowSubtitle("AutoDrive OFF", 1500);
        }
    }

    void StartChauffeur(Ped player, Vehicle vehicle)
    {
        // move player to passenger
        Function.Call(Hash.SET_PED_INTO_VEHICLE, player, vehicle, (int)VehicleSeat.Passenger);

        // spawn driver
        _driver = vehicle.CreatePedOnSeat(VehicleSeat.Driver, PedHash.Blackops01SMY);
        if (_driver == null || !_driver.Exists())
        {
            _driver = World.CreatePed(PedHash.Blackops01SMY, vehicle.Position + vehicle.RightVector * 3f);
            if (_driver != null && _driver.Exists())
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _driver, vehicle, (int)VehicleSeat.Driver);
        }

        if (_driver == null || !_driver.Exists())
        {
            _chauffeurMode = false;
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
    }

    void CycleTier()
    {
        _tier = (_tier + 1) % 3;
        if (_on && GetActiveDriver() != null)
        {
            Retask();
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
            return Function.Call<Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blip);

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

    void Retask()
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
        _passing = false;
        _minDist = float.MaxValue;
        _lastTaskAt = DateTime.UtcNow;

        Ped driver = GetActiveDriver();
        Vehicle v = driver.CurrentVehicle;
        if (v == null || !v.Exists()) { Stop(); return; }

        Function.Call(Hash.SET_DRIVER_ABILITY, driver, 1.0f);
        Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, 1.0f);

        float speed = _speeds[_tier];

        // U-turn check BEFORE road-node snapping: if the destination is close
        // and behind, the LONGRANGE task routes around the block instead of
        // turning around. Short-range task makes tight maneuvers.
        float distNow = v.Position.DistanceTo(raw);
        if (raw != Vector3.Zero && distNow < 150.0f)
        {
            Vector3 dir = raw - v.Position; dir.Z = 0f;
            Vector3 fwd = v.ForwardVector; fwd.Z = 0f;
            if (dir.LengthSquared() > 1f && fwd.LengthSquared() > 0.01f)
            {
                float dot = Math.Max(-1f, Math.Min(1f, Vector3.Dot(dir.Normalized, fwd.Normalized)));
                double ang = Math.Acos(dot) * 180.0 / Math.PI;
                if (ang > 100.0)
                {
                    // straightLineDistance >= remaining distance = steer
                    // directly at the target the whole way (verified sig:
                    // ..., p6, vehicleModel, drivingMode, stopRange, straightLineDist)
                    Function.Call((Hash)0xE2A2AA2F659D77A7, driver, v,
                        raw.X, raw.Y, raw.Z, speed, 1, v.Model.Hash,
                        _styles[_tier], 8.0f, 300.0f);
                    return;
                }
            }
        }

        // Snap target to nearest road node for LONGRANGE: waypoints can be
        // inside buildings, on sidewalks, in parking lots.
        OutputArgument snappedPos = new OutputArgument();
        if (Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE,
            _target.X, _target.Y, _target.Z, snappedPos, 1, 3.0f, 0.0f))
        {
            _target = snappedPos.GetResult<Vector3>();
        }

        Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, driver, v,
            _target.X, _target.Y, _target.Z, speed, _styles[_tier], _stopRange);
    }

    void Stop()
    {
        if (!_on) return;
        _on = false;
        _passing = false;
        _passFailures = 0;
        _passCooldownUntil = DateTime.MinValue;

        Ped p = Game.Player.Character;

        if (_chauffeurMode && _driver != null && _driver.Exists())
        {
            Vehicle v = _driver.CurrentVehicle;
            // delete the NPC first to free the driver seat, then warp player in
            _driver.Delete();
            _driver = null;
            if (v != null && v.Exists() && p != null && p.Exists())
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
