using System;
using System.IO;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

// SimpleAutoDrive v6.0.1 — GPS line follower.
//
// The v1-v5 chauffeur handed the destination to the engine's drive task and
// accepted whatever route the task's own pathfinder invented — which is not
// always the route the purple GPS line on the player's minimap shows. When
// they disagreed, the player watched the car lap a block the line never
// suggested. v6 removes the disagreement at the source: the game's GPS route
// IS readable (GET_POS_ALONG_GPS_TYPE_ROUTE), so the chauffeur samples the
// line ahead of the car and drives segments along it. What you see on the
// map is what the car does.
//
// Segment routing: the target is the route point at a speed-scaled lookahead
// (80-200 m). A task is issued once per segment and refreshed only on events
// (target reached/passed, waypoint change, tier change, 4 s heartbeat) — the
// task-churn lesson from v5.1 stands. When the line itself loops back — the
// game router DOES plan turnarounds, as a U-turn on two-lane roads — the
// segment target sits behind the nose, and that segment is issued as a
// short-range direct-steer task so the car physically turns instead of
// pathfinding around the block. If route sampling fails (no route computed
// for this blip type), the same task issuance falls back to the v5 behavior:
// long-range task to the road-node-snapped destination.
//
// Chauffeur mode (default): F6 spawns an invisible NPC driver with max
// driver ability and moves you to the passenger seat — shoot, phone, watch
// the world go by. ChauffeurMode=0 in the ini keeps player-drive mode.
public class SimpleAutoDrive : Script
{
    static readonly string[] TierNames = { "Cruise", "Brisk", "Hurried" };

    // GET_POS_ALONG_GPS_TYPE_ROUTE — verified against the game's native defs:
    // (Vector3* result, BOOL bStartAtPlayerPos, float distanceAlongRoute,
    //  int slotType) -> BOOL. Slot 0 is the player waypoint route, slot 1
    // the mission blip route (usage per Lafa2K/l2k_gps3d).
    const ulong POS_ALONG_ROUTE = 0xF3162836C28F9DA5;
    // GET_GPS_BLIP_ROUTE_LENGTH -> float
    const ulong ROUTE_LENGTH = 0xBBB45C3CF5C8AA85;
    // TASK_VEHICLE_DRIVE_TO_COORD — verified: (ped, veh, x, y, z, speed, p6,
    // vehicleModel, drivingMode, stopRange, straightLineDistance).
    const ulong DRIVE_TO_COORD = 0xE2A2AA2F659D77A7;

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

    // segment state (the driven target is a route sample, not the destination)
    Vector3 _segTarget;
    bool _segDirect;               // current segment is a direct-steer turnaround
    DateTime _lastSegTask = DateTime.MinValue;
    int _slot;                     // 0 = waypoint route, 1 = mission blip route

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

        Vehicle v = GetActiveVehicle();

        if (v == null || !v.Exists())
        {
            Stop();
            GTA.UI.Screen.ShowSubtitle("AutoDrive OFF", 1500);
            return;
        }

        // player left the vehicle mid-drive (chauffeur keeps driving without
        // them otherwise, and Stop() would summon them back later) — end the
        // drive where they stand; the car stops right there
        Ped p = Game.Player.Character;
        if (p == null || !p.Exists() || p.CurrentVehicle == null ||
            p.CurrentVehicle.Handle != v.Handle)
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
            IssueTask(v);
            return;
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
                IssueTask(v);
            }
            return;
        }

        // segment advance: near the target, past it, or the 4 s heartbeat
        float segDist = _segTarget == Vector3.Zero ? float.MaxValue : v.Position.DistanceTo(_segTarget);
        bool passed = segDist < 80.0f &&
            Vector3.Dot((_segTarget - v.Position).Normalized, v.ForwardVector) < -0.2f;
        bool beat = (DateTime.UtcNow - _lastSegTask).TotalSeconds > 4.0;
        if (segDist < 45.0f || passed || beat)
        {
            IssueTask(v);
            return;
        }

        if (_overtake && !_segDirect && v.Speed < _speeds[_tier] * 0.70f)
        {
            TryOvertake(v);
        }
    }

    Vector3 SampleRoute(float distance)
    {
        OutputArgument pos = new OutputArgument();
        if (Function.Call<bool>((Hash)POS_ALONG_ROUTE, pos, true, distance, _slot))
        {
            Vector3 p = pos.GetResult<Vector3>();
            if (p != Vector3.Zero) return p;
        }
        return Vector3.Zero;
    }

    // One task issuance: prefer the on-line sample at a speed-scaled lookahead
    // (capped by remaining route length so the final sample converges on the
    // destination), fall back to the v5 destination task when no route exists.
    void IssueTask(Vehicle v)
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
        _lastSegTask = DateTime.UtcNow;

        Ped driver = GetActiveDriver();
        if (driver == null || !driver.Exists()) { Stop(); return; }
        Vehicle veh = driver.CurrentVehicle;
        if (veh == null || !veh.Exists()) { Stop(); return; }

        Function.Call(Hash.SET_DRIVER_ABILITY, driver, 1.0f);
        Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, driver, 1.0f);

        float speed = _speeds[_tier];
        float lookahead = Math.Min(200.0f, Math.Max(80.0f, veh.Speed * 6.0f));
        float routeLen = Function.Call<float>((Hash)ROUTE_LENGTH);
        if (routeLen > 0.0f) lookahead = Math.Min(lookahead, routeLen);

        Vector3 seg = SampleRoute(lookahead);

        if (seg == Vector3.Zero)
        {
            // no readable route — v5 fallback: snapped destination, plus the
            // close-and-behind direct steer for turnarounds
            float a = NoseAngle(veh, _target);
            if (a > 100.0f && veh.Position.DistanceTo(_target) < 150.0f)
            {
                _segTarget = _target;
                _segDirect = true;
                Function.Call((Hash)DRIVE_TO_COORD, driver, veh,
                    _target.X, _target.Y, _target.Z, speed, 1, veh.Model.Hash,
                    _styles[_tier], 8.0f, 300.0f);
                return;
            }
            OutputArgument snappedPos = new OutputArgument();
            if (Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE,
                _target.X, _target.Y, _target.Z, snappedPos, 1, 3.0f, 0.0f))
            {
                seg = snappedPos.GetResult<Vector3>();
            }
            else
            {
                seg = _target;
            }
            _segTarget = seg;
            _segDirect = false;
            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, driver, veh,
                seg.X, seg.Y, seg.Z, speed, _styles[_tier], _stopRange);
            return;
        }

        // route is readable — drive the line. A segment target behind the
        // nose means the line itself is looping back: direct-steer it (the
        // physical U-turn) instead of letting the pathfinder lap the block.
        _segTarget = seg;
        float angle = NoseAngle(veh, seg);
        float segDist = veh.Position.DistanceTo(seg);

        if (angle > 100.0f && segDist < 160.0f)
        {
            _segDirect = true;
            Function.Call((Hash)DRIVE_TO_COORD, driver, veh,
                seg.X, seg.Y, seg.Z, speed, 1, veh.Model.Hash,
                _styles[_tier], 8.0f, 300.0f);
        }
        else
        {
            _segDirect = false;
            // small stopRange: the carrot is re-issued before it's reached, so
            // the task must never enter its arrive-and-brake phase mid-route
            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, driver, veh,
                seg.X, seg.Y, seg.Z, speed, _styles[_tier], 20.0f);
        }
    }

    static float NoseAngle(Vehicle v, Vector3 to)
    {
        Vector3 dir = to - v.Position; dir.Z = 0f;
        Vector3 fwd = v.ForwardVector; fwd.Z = 0f;
        if (dir.LengthSquared() < 1f || fwd.LengthSquared() < 0.01f) return 0f;
        float dot = Math.Max(-1f, Math.Min(1f, Vector3.Dot(dir.Normalized, fwd.Normalized)));
        return (float)(Math.Acos(dot) * 180.0 / Math.PI);
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
        Function.Call((Hash)DRIVE_TO_COORD, driver, v,
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
            _segTarget = Vector3.Zero;
            _segDirect = false;

            if (_chauffeurMode)
                StartChauffeur(p, v);

            GTA.UI.Screen.ShowSubtitle("~g~AutoDrive ON~w~ - " + TierLabel(), 1500);
            IssueTask(v);
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
        if (_on)
        {
            Vehicle v = GetActiveVehicle();
            if (v != null && v.Exists()) IssueTask(v);
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
        _passing = false;
        _passFailures = 0;
        _passCooldownUntil = DateTime.MinValue;
        _segTarget = Vector3.Zero;

        Ped p = Game.Player.Character;

        if (_chauffeurMode && _driver != null && _driver.Exists())
        {
            Vehicle v = _driver.CurrentVehicle;
            // delete the NPC first to free the driver seat, then warp player
            // in — ONLY if they're actually in this car; never teleport a
            // player who left the vehicle back into it
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
