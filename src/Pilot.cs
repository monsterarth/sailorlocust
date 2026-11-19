using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SailorLocust
{
    public class Stop
    {
        public string Name;
        public int Wait;
    }

    /// <summary>The sailing orders as the player writes them: one stop on each line, "name; seconds".</summary>
    public static class Orders
    {
        public static List<Stop> Parse(string text)
        {
            var stops = new List<Stop>();
            if (string.IsNullOrEmpty(text)) return stops;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string name = line; int wait = -1;
                int cut = line.LastIndexOf(';');
                if (cut >= 0)
                {
                    name = line.Substring(0, cut).Trim();
                    int.TryParse(line.Substring(cut + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out wait);
                    if (line.Substring(cut + 1).Trim().Length == 0) wait = -1;
                }
                if (name.Length == 0) continue;
                stops.Add(new Stop { Name = name, Wait = Math.Min(wait, 36000) });
                if (stops.Count >= 64) break;
            }
            return stops;
        }
    }

    /// <summary>
    /// How one kind of boat is driven. The game's boat and the boats made after it keep two numbers, ForwardSpeed and
    /// AngularVelocity, that the keys of whoever sits at the helm pull towards a target each tick; the boat's own tick
    /// turns them into motion and turning. With a rider at the helm the server leaves the boat's physics to that
    /// player's client, so the locust does not sit there: it sets those two numbers itself, to the targets the keys
    /// would give on that boat, and the server moves the boat.
    /// </summary>
    public class BoatDrive
    {
        static readonly Dictionary<Type, BoatDrive> known = new Dictionary<Type, BoatDrive>();
        System.Reflection.FieldInfo forward, angular, turnSpeed;
        System.Reflection.PropertyInfo multiplier, extra;

        public static BoatDrive For(Entity boat)
        {
            Type type = boat.GetType();
            if (known.TryGetValue(type, out BoatDrive drive)) return drive;
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var made = new BoatDrive
            {
                forward = type.GetField("ForwardSpeed", flags),
                angular = type.GetField("AngularVelocity", flags),
                turnSpeed = type.GetField("TurnSpeed", flags),
                multiplier = type.GetProperty("SpeedMultiplier", flags),
                extra = type.GetProperty("ExtraSpeed", flags)
            };
            bool ok = made.forward != null && made.angular != null && made.forward.FieldType == typeof(double) && made.angular.FieldType == typeof(double);
            known[type] = ok ? made : null;
            return ok ? made : null;
        }

        static double Number(object value, double fallback)
        {
            try { return value == null ? fallback : Convert.ToDouble(value); } catch { return fallback; }
        }

        /// <summary>throttle: 1 is a player paddling forward; turn: 1 the left key, -1 the right key.</summary>
        public void Set(Entity boat, double throttle, int turn)
        {
            double frame = Vintagestory.API.Config.GlobalConstants.PhysicsFrameTime;
            double mult = Number(multiplier?.GetValue(boat), 1);
            double more = Number(extra?.GetValue(boat), 1);
            double turning = turnSpeed != null ? Number(turnSpeed.GetValue(boat), mult) : mult;
            forward.SetValue(boat, throttle * frame * mult * more);
            angular.SetValue(boat, turn * 0.5 * frame * turning);
        }
    }

    /// <summary>
    /// Sails the boat the locust sits in, on the server: which stop is next, the long route to it, the way through the
    /// water in sight, and the keys a player would press at the helm (forward, back, left, right; the sail on boats
    /// that have one). The boat's own physics does the moving, so each boat keeps its speed and its turning.
    /// </summary>
    public class Pilot
    {
        readonly EntitySailorLocust locust;
        readonly SailorLocustSystem system;
        readonly ICoreServerAPI sapi;
        LocalNav nav;

        public List<Stop> Stops = new List<Stop>();
        public List<Vec2d> Route;
        int routeIdx;
        string routeFor;
        public string RouteHow = "";
        List<Vec2d> path;
        int pathIdx;
        double replanIn;
        bool pathReached;

        Vec3d progressFrom;
        double progressTimer;
        double backoffLeft;
        int backoffSide = 1;
        public int StuckCount;

        // which key turns which way and which way the bow points are measured on the boat itself, not taken for granted
        bool leftTurnsPositive;
        double leftEvidence, rightEvidence;
        double prevFx, prevFz; bool havePrev;
        int headingSign = 1;
        Vec3d speedFrom; double speedTimer; bool forwardHeld;
        /// <summary>Blocks a second over the last second.</summary>
        public double Speed;
        /// <summary>Blocks a second while sailing with the bow on the way, averaged: what sailing out of sight goes by.</summary>
        public double CruiseSpeed;
        double statusIn;
        bool sailSetByUs;
        double waitLocal = -1;
        int edgeWaits;
        bool wokeBoat;

        /// <summary>What the pilots cost the server, all boats together: ticks, milliseconds in all, the longest tick.</summary>
        public static long CostTicks; public static double CostMs, CostMaxMs;

        /// <summary>Told when the boat reaches a dock (the self test listens).</summary>
        public static Action<EntitySailorLocust, Marker> Arrived;
        /// <summary>Told when a buoy is passed.</summary>
        public static Action<EntitySailorLocust, Marker> Passed;

        public Pilot(EntitySailorLocust locust, SailorLocustSystem system)
        {
            this.locust = locust;
            this.system = system;
            sapi = system.sapi;
            ReloadOrders();
        }

        public bool Running
        {
            get { return locust.WatchedAttributes.GetBool("running"); }
            set { locust.WatchedAttributes.SetBool("running", value); }
        }

        public int StopIndex
        {
            get { return locust.WatchedAttributes.GetInt("stopIndex"); }
            set { locust.WatchedAttributes.SetInt("stopIndex", value); }
        }

        public bool Waiting
        {
            get { return locust.WatchedAttributes.GetBool("waiting"); }
            set { locust.WatchedAttributes.SetBool("waiting", value); }
        }

        public double WaitLeft
        {
            get { return locust.WatchedAttributes.GetDouble("waitLeft"); }
            set { locust.WatchedAttributes.SetDouble("waitLeft", value); }
        }

        public void ReloadOrders()
        {
            Stops = Orders.Parse(locust.WatchedAttributes.GetString("orders", ""));
            Route = null; path = null;
            if (Stops.Count > 0 && StopIndex >= Stops.Count) StopIndex = 0;
        }

        public Stop CurrentStop { get { return Stops.Count == 0 ? null : Stops[((StopIndex % Stops.Count) + Stops.Count) % Stops.Count]; } }

        void SetStatus(string key, params object[] args)
        {
            string joined = key;
            foreach (object a in args) joined += "|" + a;
            if (locust.WatchedAttributes.GetString("status", "") != joined) locust.WatchedAttributes.SetString("status", joined);
        }

        static bool HasSails(Entity boat) { return boat.Properties?.Attributes != null && boat.Properties.Attributes["unfurlSails"].AsBool(false); }

        void SetSail(Entity boat, int position)
        {
            if (!HasSails(boat)) return;
            if (boat.WatchedAttributes.GetInt("sailPosition") != position)
            {
                boat.WatchedAttributes.SetInt("sailPosition", position);
                sailSetByUs = position != 0;
            }
        }

        bool heldLeft, heldRight;

        /// <summary>
        /// The helm, as keys: forward, back, left, right. They are turned into the boat's own forward speed and turning
        /// speed, at the values the same keys give a player on that boat (BoatDrive).
        /// </summary>
        void Keys(bool forward, bool back, bool left, bool right)
        {
            heldLeft = left; heldRight = right;
            forwardHeld = forward && !back;
            Entity boat = locust.Boat;
            if (boat == null) return;
            BoatDrive drive = BoatDrive.For(boat);
            if (drive == null) return;
            bool sails = HasSails(boat);
            int sail = sails ? boat.WatchedAttributes.GetInt("sailPosition") : 0;
            // a player's paddling gives 1; on a boat with sails, paddling gives 0.2 and the sail 0.75 for each step
            double throttle = 0;
            if (back) throttle = sails ? -0.4 : -0.6;
            else if (sails && sail > 0) throttle = 0.75 * sail;
            else if (forward) throttle = sails ? 0.3 : 1.0;
            if (gentle && throttle > 0) throttle = sails ? 0.3 : 0.5;
            // under sail counts as going forward too, for the measure of the boat's speed
            forwardHeld = throttle > 0;
            drive.Set(boat, throttle, left == right ? 0 : (left ? 1 : -1));
        }

        /// <summary>Hands off: no key held, the sail it set taken in.</summary>
        public void Release(Entity boat)
        {
            Keys(false, false, false, false);
            if (boat != null && sailSetByUs) SetSail(boat, 0);
            havePrev = false;
        }

        Marker dockingFor;
        int berthPhase;
        double berthX, berthZ, entryX, entryZ, outerX, outerZ, axisX, axisZ, glideTimer, glideFrom, nearTimer;
        /// <summary>Where it comes in to: the berth, or a little further out from the pier when its line in must be.</summary>
        double glideX, glideZ;
        int retries;
        double spinFor, berthTakenFor, noFitFor, unseenIn = 3, berthRest;
        int shortTries;

        /// <summary>A player (alive, in the world) within that many blocks of the point, across the water.</summary>
        public static bool PlayerWithin(ICoreServerAPI sapi, double x, double z, double reach)
        {
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                Entity e = player?.Entity;
                if (e == null) continue;
                if ((e.Pos.X - x) * (e.Pos.X - x) + (e.Pos.Z - z) * (e.Pos.Z - z) <= reach * reach) return true;
            }
            return false;
        }
        int loops;
        /// <summary>Times it went round again or backed out for another try at a berth, in all (the self test reads it).</summary>
        public int Retries;
        bool berthLoaded;

        /// <summary>Water all the way between two points, in the last look at the blocks.</summary>
        static bool LineClear(LocalNav nav, double ax, double az, double bx, double bz)
        {
            double len = Math.Sqrt(Sq(bx - ax) + Sq(bz - az));
            int steps = Math.Max(1, (int)Math.Ceiling(len * 2));
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps;
                if (nav.Clearance(ax + (bx - ax) * t, az + (bz - az) * t) < 1) return false;
            }
            return true;
        }
        bool gentle, moored;
        double moorX, moorZ; float moorYaw;
        /// <summary>The last mooring: blocks the boat was moved and degrees it was turned to lie in the berth (the self test reads them).</summary>
        public double LastSnapDist, LastSnapTurn, LastBerthX, LastBerthZ, LastPullIn;

        /// <summary>
        /// Where the boat lies at that dock and how it gets there. The berth is against the edge of the water in front of
        /// the post, the boat along the pier; it is entered from the end the boat comes from, at the head of the berth
        /// some way out along the pier, so the boat turns in open water and slides in alongside, as a boat is brought in.
        /// </summary>
        void PlanBerth(Entity boat, Marker marker)
        {
            dockingFor = marker; berthPhase = 0; glideTimer = 0; nearTimer = 0; arriveWhy = null; gentle = false; retries = 0; spinFor = 0; loops = 0;
            LocalNav nav = system.SharedNav;
            berthLoaded = nav.Loaded(marker.X, marker.Y, marker.Z);
            Docking.Dir(marker.Facing, out double fx, out double fz);
            double flen = Math.Sqrt(fx * fx + fz * fz); fx /= flen; fz /= flen;
            double edge = Docking.EdgeDistance(marker.X, marker.Z, marker.Facing, (x, z) => { int wy = nav.FindWaterY(x + 0.5, marker.Y, z + 0.5); return wy >= 0 && nav.IsWater(x, wy, z); });
            Docking.BoatSize(boat, out double halfLength, out double halfWidth);
            Docking.BerthFor(marker.X, marker.Z, marker.Facing, edge, halfWidth, out berthX, out berthZ);
            axisX = -fz; axisZ = fx;
            // the berth slides along the pier until the whole hull lies on open water (a long boat at a post near a
            // wall or the root of the pier)
            double freeUp = FreeAlong(nav, marker, 1, halfLength + 3, halfWidth), freeDown = FreeAlong(nav, marker, -1, halfLength + 3, halfWidth);
            double need = halfLength + 0.6, shift = 0;
            if (freeDown < need && freeUp > need) shift = Math.Min(need - freeDown, freeUp - need);
            else if (freeUp < need && freeDown > need) shift = -Math.Min(need - freeUp, freeDown - need);
            berthX += axisX * shift; berthZ += axisZ * shift;
            // and out from the pier, half a block at a time, while any of the hull would still lie on something solid
            // (a T-head wider than the post's edge, a pier corner under a long boat's end)
            double pushed = 0;
            while (berthLoaded && pushed < 4 && !HullClear(nav, marker.Y, berthX, berthZ, axisX, axisZ, halfLength, halfWidth)) { berthX += fx * 0.5; berthZ += fz * 0.5; pushed += 0.5; }
            if (pushed > 0 && SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] berth at " + marker.Name + " pushed " + pushed.ToString("0.0") + " out from the pier to clear the hull");
            double side = (boat.Pos.X - berthX) * axisX + (boat.Pos.Z - berthZ) * axisZ >= 0 ? 1 : -1;
            double reach = halfLength + 9;
            // the run in from the point out to the head: long enough for the boat to come straight before it turns in
            double run = Math.Max(12, 1.5 * halfLength);
            // the lane in: from the berth out along the line of the pier, as wide as the boat and a block each side.
            // The end with less in the way is taken; with as little, the end the boat comes from
            int blockedHere = LaneBlocked(nav, marker, side, halfLength, halfWidth, reach + run, fx, fz);
            int blockedThere = LaneBlocked(nav, marker, -side, halfLength, halfWidth, reach + run, fx, fz);
            if (blockedThere < blockedHere) side = -side;
            // the line it comes in on: the berth's own, or as little further out from the pier as it takes to run clear
            // all the way out (past the arm of a T-head, say). Lying there, the lines pull it in the last blocks
            double offset = 0;
            if (berthLoaded)
            {
                for (double o = 0; o <= 4.5; o += 0.5)
                {
                    if (StripBlocked(nav, marker.Y, berthX + fx * o, berthZ + fz * o, side, halfWidth + 0.4, reach + run) == 0) { offset = o; break; }
                    if (o >= 4.5) offset = 0;
                }
            }
            glideX = berthX + fx * offset; glideZ = berthZ + fz * offset;
            bool found = false;
            foreach (double s in new[] { side, -side })
            {
                double ex = glideX + axisX * s * reach + fx * 1.0, ez = glideZ + axisZ * s * reach + fz * 1.0;
                int wy = nav.FindWaterY(ex, boat.Pos.Y, ez);
                if (wy >= 0 && nav.IsWater((int)Math.Floor(ex), wy, (int)Math.Floor(ez)))
                {
                    entryX = ex; entryZ = ez; found = true;
                    // and one further out on the same line, so the boat runs in straight and is in line when it gets there
                    double ox = glideX + axisX * s * (reach + run) + fx * 1.5, oz = glideZ + axisZ * s * (reach + run) + fz * 1.5;
                    int owy = nav.FindWaterY(ox, boat.Pos.Y, oz);
                    bool outerWater = owy >= 0 && nav.IsWater((int)Math.Floor(ox), owy, (int)Math.Floor(oz));
                    outerX = outerWater ? ox : ex; outerZ = outerWater ? oz : ez;
                    break;
                }
            }
            // no water at either end (or not loaded yet): straight out in front of the berth
            if (!found) { entryX = berthX + fx * (halfLength + 4); entryZ = berthZ + fz * (halfLength + 4); outerX = entryX; outerZ = entryZ; }
            berthPhase = outerX == entryX && outerZ == entryZ ? 1 : 0;
            LastBerthX = berthX; LastBerthZ = berthZ;
            if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] berth at " + marker.Name + " for " + boat.Code.Path + ": edge " + edge.ToString("0.0") + ", boat " + (2 * halfLength).ToString("0.0") + " x " + (2 * halfWidth).ToString("0.0") + ", entry " + (found ? "along the pier" : "straight out") + "; from the post: berth " + Rel(berthX, berthZ, marker) + ", head " + Rel(entryX, entryZ, marker) + ", out " + Rel(outerX, outerZ, marker) + ", boat " + Rel(boat.Pos.X, boat.Pos.Z, marker) + "; blocked lanes " + blockedHere + " (side it comes from) and " + blockedThere + ", slid " + shift.ToString("0.0") + " along the pier, comes in " + offset.ToString("0.0") + " out from the berth's line, phase " + berthPhase);
        }

        static string Rel(double x, double z, Marker m) { return (x - m.X - 0.5).ToString("0.0") + "/" + (z - m.Z - 0.5).ToString("0.0"); }

        /// <summary>Hulls longer than this (in blocks) are not given a dock kept for small boats.</summary>
        public const double SmallLength = 10;
        static readonly Dictionary<string, KeyValuePair<bool, long>> fitSeen = new Dictionary<string, KeyValuePair<bool, long>>();

        /// <summary>
        /// The boat can lie at that dock: not too long for a dock kept for small boats, and, where the dock's chunk is
        /// loaded, with open water along the pier for the whole hull (the berth may slide along it). Remembered a while.
        /// </summary>
        bool Fits(Marker dock, Entity boat)
        {
            Docking.BoatSize(boat, out double hl, out double hw);
            if (dock.SmallOnly && 2 * hl > SmallLength) return false;
            if (dock.Facing < 0) return true;
            LocalNav nav = system.SharedNav;
            if (!nav.Loaded(dock.X, dock.Y, dock.Z)) return true;
            string key = Berths.KeyOf(dock) + "|" + boat.Code;
            long now = sapi.World.ElapsedMilliseconds;
            if (fitSeen.TryGetValue(key, out var seen) && now - seen.Value < 20000) return seen.Key;
            Docking.Dir(dock.Facing, out double fx, out double fz);
            double fl = Math.Sqrt(fx * fx + fz * fz); fx /= fl; fz /= fl;
            double edge = Docking.EdgeDistance(dock.X, dock.Z, dock.Facing, (x, z) => { int wy = nav.FindWaterY(x + 0.5, dock.Y, z + 0.5); return wy >= 0 && nav.IsWater(x, wy, z); });
            Docking.BerthFor(dock.X, dock.Z, dock.Facing, edge, hw, out double bx, out double bz);
            double ax = -fz, az = fx, limit = 2 * hl + 2;
            double run = FreeRun(nav, dock.Y, bx, bz, ax, az, 1, limit, hw) + FreeRun(nav, dock.Y, bx, bz, ax, az, -1, limit, hw);
            bool fits = run >= 2 * hl + 0.5;
            // and a way in for this hull: a lane as wide as the boat, from the berth out along the pier as far as the boat
            // runs in straight, clear at one end or the other, on the berth's own line or a little further out (a slip
            // between two piers whose heads nearly close it takes a raft, not a drakkar)
            bool reachable = false;
            double lane = hl + 9 + Math.Max(12, 1.5 * hl);
            for (int side = -1; side <= 1 && fits && !reachable; side += 2)
            {
                for (double o = 0; o <= 4.5 && !reachable; o += 0.5)
                {
                    if (LaneClearFrom(nav, dock.Y, bx + fx * o, bz + fz * o, ax, az, side, hw + 0.4, lane)) reachable = true;
                }
            }
            if (fits && !reachable)
            {
                fits = false;
                if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] " + boat.Code.Path + " (" + (2 * hl).ToString("0.0") + " long) cannot get into the berth at " + dock.Name + " " + dock.X + " " + dock.Z + ": no clear way in along the pier at either end");
            }
            fitSeen[key] = new KeyValuePair<bool, long>(fits, now);
            if (!fits && SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] " + boat.Code.Path + " (" + (2 * hl).ToString("0.0") + " long) does not fit the berth at " + dock.Name + ": " + run.ToString("0.0") + " blocks of open water along the pier");
            return fits;
        }

        /// <summary>
        /// The boat lying at that dock would reach over another berth of the port that a boat has now, or over a boat
        /// lying there: two long boats at heads close together, say.
        /// </summary>
        bool Crowded(Marker dock, Entity boat, string ship)
        {
            Berths.Lane mine = BerthRect(dock, boat);
            if (mine == null) return false;
            // its hull over another's berth or another's way in; its way in over another's berth (a boat lying across a
            // T-head closes the mouth of the slip beside it)
            foreach (var other in system.Berths.OtherBerths(ship)) if (Berths.Cross(other, mine)) return true;
            foreach (var other in system.Berths.OtherWays(ship)) if (Berths.Cross(other, mine)) return true;
            var lying = new List<Berths.Lane>();
            foreach (Hull h in OtherHulls(boat, 80))
            {
                if (h.Pilot != null && !h.Pilot.Waiting) continue;   // under way: not lying anywhere
                var hull = new Berths.Lane { X = h.X, Z = h.Z, Ax = h.Ax, Az = h.Az, HalfLength = h.Hl, HalfWidth = h.Hw };
                if (Berths.Cross(hull, mine)) return true;
                lying.Add(hull);
            }
            if (lying.Count == 0) return false;
            // every way in that the blocks leave open runs over a boat lying there (one tied across a T-head closes the
            // mouth of the slip beside it): crowded until it leaves
            LocalNav nav = system.SharedNav;
            Docking.BoatSize(boat, out double hl, out double hw);
            double fx = -mine.Az, fz = mine.Ax;
            Docking.Dir(dock.Facing, out double dfx, out double dfz);
            if (fx * dfx + fz * dfz < 0) { fx = -fx; fz = -fz; }
            double lane = hl + 9 + Math.Max(12, 1.5 * hl);
            bool anyOpen = false;
            for (int side = -1; side <= 1; side += 2)
            {
                for (double o = 0; o <= 4.5; o += 0.5)
                {
                    double x0 = mine.X + fx * o, z0 = mine.Z + fz * o;
                    if (!LaneClearFrom(nav, dock.Y, x0, z0, mine.Ax, mine.Az, side, hw + 0.4, lane)) continue;
                    var way = new Berths.Lane { X = x0 + mine.Ax * side * lane / 2, Z = z0 + mine.Az * side * lane / 2, Ax = mine.Ax, Az = mine.Az, HalfLength = lane / 2, HalfWidth = hw + 0.4 };
                    bool crossed = false;
                    foreach (var h in lying) if (Berths.Cross(h, way)) { crossed = true; break; }
                    if (!crossed) return false;
                    anyOpen = true;
                }
            }
            return anyOpen;
        }

        /// <summary>
        /// The way in to the berth at that dock, roughly, before the boat has worked it out: along the pier from the
        /// hull out to the end with more open water, a boat's length and more, as wide as the boat and a block each side.
        /// </summary>
        Berths.Lane BerthApproach(Marker dock, Entity boat, Berths.Lane rect)
        {
            LocalNav nav = system.SharedNav;
            Docking.BoatSize(boat, out double hl, out double hw);
            double ax = rect.Ax, az = rect.Az, limit = hl + 20;
            double up = FreeRun(nav, dock.Y, rect.X, rect.Z, ax, az, 1, limit, hw), down = FreeRun(nav, dock.Y, rect.X, rect.Z, ax, az, -1, limit, hw);
            double s = up >= down ? 1 : -1;
            // from the far end of the hull to the boat's length plus some past the near one
            double from = hl, to = hl + Math.Max(12, hl + 6);
            double c = (to - from) / 2;
            return new Berths.Lane { X = rect.X + ax * s * c, Z = rect.Z + az * s * c, Ax = ax, Az = az, HalfLength = (to + from) / 2, HalfWidth = hw + 1 };
        }

        /// <summary>Where the hull of this boat would lie at that dock, with half a block round it. Null when the dock's water is not loaded.</summary>
        Berths.Lane BerthRect(Marker dock, Entity boat)
        {
            if (dock.Facing < 0) return null;
            LocalNav nav = system.SharedNav;
            if (!nav.Loaded(dock.X, dock.Y, dock.Z)) return null;
            Docking.BoatSize(boat, out double hl, out double hw);
            Docking.Dir(dock.Facing, out double fx, out double fz);
            double fl = Math.Sqrt(fx * fx + fz * fz); fx /= fl; fz /= fl;
            double edge = Docking.EdgeDistance(dock.X, dock.Z, dock.Facing, (x, z) => { int wy = nav.FindWaterY(x + 0.5, dock.Y, z + 0.5); return wy >= 0 && nav.IsWater(x, wy, z); });
            Docking.BerthFor(dock.X, dock.Z, dock.Facing, edge, hw, out double bx, out double bz);
            return new Berths.Lane { X = bx, Z = bz, Ax = -fz, Az = fx, HalfLength = hl + 0.5, HalfWidth = hw + 0.5 };
        }

        /// <summary>Open water all along a strip of that half width, from a point out along the pier one way.</summary>
        static bool LaneClearFrom(LocalNav nav, int y, double x0, double z0, double ax, double az, double s, double halfWidth, double length)
        {
            for (double t = 0; t <= length; t += 0.5)
            {
                for (double c = -halfWidth; c <= halfWidth + 0.01; c += 0.5)
                {
                    double x = x0 + ax * s * t + az * c, z = z0 + az * s * t - ax * c;
                    int wy = nav.FindWaterY(x, y, z);
                    if (wy < 0 || !nav.IsWater((int)Math.Floor(x), wy, (int)Math.Floor(z))) return false;
                }
            }
            return true;
        }

        static double FreeRun(LocalNav nav, int y, double bx, double bz, double ax, double az, double s, double limit, double halfWidth)
        {
            for (double t = 0.5; t <= limit; t += 0.5)
            {
                for (double c = -halfWidth + 0.2; c <= halfWidth - 0.19; c += 0.5)
                {
                    double x = bx + ax * s * t + az * c, z = bz + az * s * t - ax * c;
                    int wy = nav.FindWaterY(x, y, z);
                    if (wy < 0 || !nav.IsWater((int)Math.Floor(x), wy, (int)Math.Floor(z))) return t - 0.5;
                }
            }
            return limit;
        }

        /// <summary>Blocks of open water from the middle of the berth along the pier, one way, as wide as the boat (up to a limit).</summary>
        double FreeAlong(LocalNav nav, Marker marker, double s, double limit, double halfWidth)
        {
            for (double t = 0.5; t <= limit; t += 0.5)
            {
                for (double c = -halfWidth + 0.2; c <= halfWidth - 0.19; c += 0.5)
                {
                    double x = berthX + axisX * s * t + (axisZ) * c, z = berthZ + axisZ * s * t - (axisX) * c;
                    int wy = nav.FindWaterY(x, marker.Y, z);
                    if (wy < 0 || !nav.IsWater((int)Math.Floor(x), wy, (int)Math.Floor(z))) return t - 0.5;
                }
            }
            return limit;
        }

        /// <summary>Open water under the whole hull of a boat lying there along that axis.</summary>
        static bool HullClear(LocalNav nav, int y, double x, double z, double ax, double az, double halfLength, double halfWidth)
        {
            for (double a = -halfLength + 0.2; a <= halfLength - 0.19; a += 0.5)
            {
                for (double c = -halfWidth + 0.2; c <= halfWidth - 0.19; c += 0.5)
                {
                    double px = x + ax * a + az * c, pz = z + az * a - ax * c;
                    int wy = nav.FindWaterY(px, y, pz);
                    if (wy < 0 || !nav.IsWater((int)Math.Floor(px), wy, (int)Math.Floor(pz))) return false;
                }
            }
            return true;
        }

        /// <summary>Cells not open water in a strip of that half width from a point out along the pier, one way.</summary>
        int StripBlocked(LocalNav nav, int y, double x0, double z0, double s, double halfWidth, double length)
        {
            int blocked = 0;
            for (double t = 0; t <= length; t += 0.5)
            {
                for (double c = -halfWidth; c <= halfWidth + 0.01; c += 0.5)
                {
                    double x = x0 + axisX * s * t + axisZ * c, z = z0 + axisZ * s * t - axisX * c;
                    int wy = nav.FindWaterY(x, y, z);
                    if (wy < 0 || !nav.IsWater((int)Math.Floor(x), wy, (int)Math.Floor(z))) blocked++;
                }
            }
            return blocked;
        }

        int LaneBlocked(LocalNav nav, Marker marker, double s, double halfLength, double halfWidth, double length, double fx, double fz)
        {
            int blocked = 0;
            for (double t = 1; t <= length; t += 1)
            {
                for (double c = -(halfWidth + 1); c <= halfWidth + 1.01; c += 1)
                {
                    double x = berthX + axisX * s * t + fx * c, z = berthZ + axisZ * s * t + fz * c;
                    int wy = nav.FindWaterY(x, marker.Y, z);
                    if (wy < 0 || !nav.IsWater((int)Math.Floor(x), wy, (int)Math.Floor(z))) blocked++;
                }
            }
            return blocked;
        }

        /// <summary>Ties the boat in its berth: in the middle of it, along the pier, still. It stays so while it waits.</summary>
        void Moor(Entity boat, Marker marker)
        {
            float yaw = Docking.YawAlong(axisX, axisZ, boat.Pos.Yaw);
            LastSnapDist = Math.Sqrt(Sq(glideX - boat.Pos.X) + Sq(glideZ - boat.Pos.Z));
            LastPullIn = Math.Sqrt(Sq(glideX - berthX) + Sq(glideZ - berthZ));
            LastSnapTurn = Math.Abs(GameMath.AngleRadDistance(boat.Pos.Yaw, yaw)) * 180 / Math.PI;
            moorX = berthX; moorZ = berthZ; moorYaw = yaw; moored = true;
            HoldMoored(boat);
        }

        void HoldMoored(Entity boat)
        {
            boat.Pos.SetPos(moorX, boat.Pos.Y, moorZ);
            boat.Pos.Yaw = moorYaw;
            boat.Pos.Motion.X = 0; boat.Pos.Motion.Z = 0;
            BoatDrive.For(boat)?.Set(boat, 0, 0);
        }

        /// <summary>
        /// Turns the boat on the spot until it lies along that line, bow either way (the nearer one). Returns how far off
        /// it still is, in radians.
        /// </summary>
        double TurnAlong(Entity boat, double ax, double az)
        {
            SetSail(boat, 0);
            Vec3f view = boat.Pos.GetViewVector();
            double fx = -view.X * headingSign, fz = -view.Z * headingSign;
            double flen = Math.Sqrt(fx * fx + fz * fz);
            if (flen < 1e-6) { Keys(false, false, false, false); return 0; }
            fx /= flen; fz /= flen;
            if (fx * ax + fz * az < 0) { ax = -ax; az = -az; }
            double angle = Math.Atan2(fx * az - fz * ax, fx * ax + fz * az);
            bool positive = angle > 0.06, negative = angle < -0.06;
            Keys(false, false, leftTurnsPositive ? positive : negative, leftTurnsPositive ? negative : positive);
            return Math.Abs(angle);
        }

        /// <summary>The way it follows now through the water in sight, or the long route when it has none (for the route wand).</summary>
        public List<Vec2d> CurrentWay(Entity boat)
        {
            var way = new List<Vec2d> { new Vec2d(boat.Pos.X, boat.Pos.Z) };
            if (path != null && path.Count > 1) { for (int i = Math.Max(1, pathIdx); i < path.Count; i++) way.Add(path[i]); return way; }
            if (Route != null && Route.Count > 1) { for (int i = Math.Max(1, routeIdx); i < Route.Count; i++) way.Add(Route[i]); return way; }
            return null;
        }

        /// <summary>The points of its coming in, while it comes in: out on the line of the pier, the head of the berth, the berth.</summary>
        public List<Vec2d> BerthPoints()
        {
            if (dockingFor == null) return null;
            return new List<Vec2d> { new Vec2d(outerX, outerZ), new Vec2d(entryX, entryZ), new Vec2d(glideX, glideZ), new Vec2d(berthX, berthZ) };
        }

        /// <summary>Back on the water while waiting at that dock (from out of sight): planned and tied in its berth.</summary>
        public void TieUpAt(Marker dock)
        {
            Entity boat = locust.Boat;
            if (boat == null || dock == null || !dock.Dock || dock.Facing < 0) return;
            PlanBerth(boat, dock);
            Docking.BoatSize(boat, out double hl, out double hw);
            boat.Pos.SetPos(berthX, boat.Pos.Y, berthZ);
            Moor(boat, dock);
            system.Berths.Keep(dock, ShipKey(boat));
        }

        /// <summary>Ends the wait at the dock now (new orders that send it elsewhere).</summary>
        public void Depart()
        {
            Waiting = false; WaitLeft = 0; waitLocal = -1; leaving = false; exitVia = null;
            Route = null; path = null; moored = false; dockingFor = null; gentle = false;
        }

        void Advance()
        {
            moored = false; dockingFor = null; gentle = false;
            if (Stops.Count > 0) StopIndex = (StopIndex + 1) % Stops.Count;
            Route = null; path = null; StuckCount = 0; progressTimer = 0; progressFrom = null;
        }

        public void Tick(float dt)
        {
            Entity boat = locust.Boat;
            if (boat == null) { SetStatus("status-noboat"); return; }
            byBoat[boat.EntityId] = this;
            Yielding = false; viaKind = 0;
            if (!Running || Stops.Count == 0)
            {
                Release(boat);
                SetStatus(Stops.Count == 0 ? "status-noorders" : "status-idle");
                // at anchor it sleeps and wakes as any boat does
                if (wokeBoat) { boat.AlwaysActive = false; locust.AlwaysActive = false; wokeBoat = false; }
                return;
            }
            // under orders the boat keeps sailing while its chunk is loaded, whether or not a player stands near
            Wake(boat);
            Wake(locust);
            wokeBoat = true;
            OwnPhysics(boat, dt);

            // with no player near enough to see it, the boat need not be in the world: it sails on by time, as out of
            // sight (OffscreenFleet), and comes back on the water when someone comes near
            if (SailorLocustSystem.Config.HideUnseen)
            {
                unseenIn -= dt;
                if (unseenIn <= 0)
                {
                    unseenIn = 3;
                    if (!PlayerWithin(sapi, boat.Pos.X, boat.Pos.Z, SailorLocustSystem.Config.UnseenDistance) && system.Fleet.TryVirtualize(boat, locust, "nobody within " + SailorLocustSystem.Config.UnseenDistance + " blocks")) return;
                }
            }

            Stop stop = CurrentStop;
            double bx = boat.Pos.X, bz = boat.Pos.Z;
            Marker marker = system.Markers.Find(stop.Name, bx, bz);
            if (marker == null) { Release(boat); SetStatus("status-unknown", stop.Name); return; }

            // a port: docks of the same name are its berths. The boat has one, asks for one as it nears, or waits off the port
            if (marker.Dock)
            {
                List<Marker> docks = system.Markers.FindAll(stop.Name);
                docks.RemoveAll(m => !m.Dock);
                string ship = boat.WatchedAttributes.GetString(OffscreenFleet.IdAttr, null) ?? ("locust" + locust.EntityId);
                Marker mine = system.Berths.HeldBy(docks, ship);
                if (mine != null)
                {
                    marker = mine; system.Berths.Keep(mine, ship);
                    // its hull at the berth, and while it is still coming in, its way in: a boat that asks later is not given
                    // a berth that lies across it (it would close the way in of the boat that had it first)
                    if (dockingFor == mine) { Docking.BoatSize(boat, out double rhl, out double rhw); system.Berths.SetBerth(ship, new Berths.Lane { X = berthX, Z = berthZ, Ax = axisX, Az = axisZ, HalfLength = rhl + 0.5, HalfWidth = rhw + 0.5 }, Waiting ? null : BerthLane(rhl, rhw)); }
                    else { Berths.Lane r = BerthRect(mine, boat); if (r != null) system.Berths.SetBerth(ship, r, Waiting ? null : BerthApproach(mine, boat, r)); }
                }
                else if (Waiting) system.Berths.Keep(marker, ship);
                else if (Math.Sqrt(Sq(marker.X + 0.5 - bx) + Sq(marker.Z + 0.5 - bz)) <= SailorLocustSystem.Config.BerthAskDistance || NearWaitArea(stop.Name, bx, bz))
                {
                    // no berth of the port takes a boat this size: it says so, and after a while goes on to its next stop
                    if (docks.TrueForAll(d => !Fits(d, boat)))
                    {
                        noFitFor += dt;
                        Brake(boat);
                        SetStatus("status-nofit", stop.Name);
                        if (noFitFor > 12)
                        {
                            sapi.Logger.Notification("[sailorlocust] no berth at " + stop.Name + " takes " + boat.Code.Path + "; on to the next stop");
                            noFitFor = 0; Advance();
                        }
                        return;
                    }
                    noFitFor = 0;
                    if (berthRest > 0) berthRest -= dt;
                    Marker berth = berthRest > 0 ? null : system.Berths.Ask(stop.Name, docks, ship, bx, bz, dock => BoatLyingAt(dock, boat) || !Fits(dock, boat) || Crowded(dock, boat, ship));
                    if (berth == null)
                    {
                        // to the waiting area of the port when it has one; else it stops where it is, rather than run on
                        // into the port under its own way
                        Marker area = system.Markers.FindRole(stop.Name, Marker.RoleWait, bx, bz) ?? OwnWaitSpot(boat, marker, ship);
                        if (area != null) { marker = area; viaKind = Marker.RoleWait; }
                        else
                        {
                            Brake(boat);
                            progressTimer = 0; path = null;
                            SetStatus("status-berth", stop.Name);
                            return;
                        }
                    }
                    else { marker = berth; waitSpot = null; }
                }
                // a berth of its own, but the water of it is worked in by another boat: to the waiting area meanwhile
                if (marker.Dock && !Waiting && dockingFor == marker && berthPhase >= 1)
                {
                    Docking.BoatSize(boat, out double whl, out double whw);
                    if (!system.Berths.Manoeuvre(ShipKey(boat), BerthLane(whl, whw)))
                    {
                        Marker area = system.Markers.FindRole(stop.Name, Marker.RoleWait, bx, bz);
                        if (area != null) { marker = area; viaKind = Marker.RoleWait; }
                    }
                }
            }
            // leaving a port by its way out: that buoy first, then on
            if (!Waiting && exitVia != null && viaKind == 0) { marker = exitVia; viaKind = Marker.RoleExit; }
            // bound for a port with a way in: that buoy first, while the boat is further from the port than the buoy is
            if (!Waiting && viaKind == 0 && marker.Dock && entryPassedFor != StopIndex + "|" + stop.Name)
            {
                Marker way = system.Markers.FindRole(stop.Name, Marker.RoleEntry, bx, bz);
                if (way != null)
                {
                    double toPort = Math.Sqrt(Sq(marker.X - bx) + Sq(marker.Z - bz)), wayToPort = Math.Sqrt(Sq(marker.X - way.X) + Sq(marker.Z - way.Z));
                    if (toPort > wayToPort + 6) { marker = way; viaKind = Marker.RoleEntry; }
                    else entryPassedFor = StopIndex + "|" + stop.Name;
                }
            }

            if (Waiting)
            {
                Release(boat);
                if (moored) HoldMoored(boat);
                if (waitLocal < 0) waitLocal = WaitLeft;
                double before = waitLocal;
                waitLocal -= dt;
                if (waitLocal <= 0 && moored && dockingFor != null && !LeaveTurn(boat, marker))
                {
                    // the time is up but the water of the berth is not free: it stays tied until its turn
                    waitLocal = 0.01;
                    SetStatus("status-waitturn", marker.Name);
                    return;
                }
                if (waitLocal <= 0)
                {
                    // out of the berth the way it came in, straight along the pier (astern when it lies bow in), before it turns
                    if (moored && dockingFor != null)
                    {
                        double lx = entryX - berthX, lz = entryZ - berthZ, ll = Math.Sqrt(lx * lx + lz * lz);
                        if (ll > 1)
                        {
                            Docking.BoatSize(boat, out double lhl, out double lhw);
                            // with room to turn on the open side of the berth (a boat's length of water out from it), or a
                            // small boat, it simply sails off and turns; else it goes out along the pier first
                            Docking.Dir(dockingFor.Facing, out double ofx, out double ofz);
                            double ofl = Math.Sqrt(ofx * ofx + ofz * ofz); ofx /= ofl; ofz /= ofl;
                            double room = lhl * 1.2;
                            bool turnsFree = lhl <= 4 || HullClear(system.SharedNav, marker.Y, berthX + ofx * (lhw + room / 2 + 0.5), berthZ + ofz * (lhw + room / 2 + 0.5), axisX, axisZ, lhl + 2, room / 2);
                            laneHold = true; laneHoldX = berthX; laneHoldZ = berthZ; laneHoldFor = 0;
                            if (!turnsFree)
                            {
                                leaving = true; leaveFor = 0; leaveAx = lx / ll; leaveAz = lz / ll; leaveName = marker.Name;
                                leaveX = berthX + leaveAx * (lhl + 6); leaveZ = berthZ + leaveAz * (lhl + 6);
                            }
                            if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] " + boat.Code.Path + " leaves " + marker.Name + (turnsFree ? " turning off, it has room" : " along the pier first, " + (Bow(boat).X * leaveAx + Bow(boat).Y * leaveAz > 0 ? "bow first" : "astern")));
                        }
                    }
                    Marker way = system.Markers.FindRole(marker.Name, Marker.RoleExit, boat.Pos.X, boat.Pos.Z);
                    Waiting = false; WaitLeft = 0; waitLocal = -1; Advance();
                    exitVia = way;
                }
                else if (Math.Ceiling(waitLocal) != Math.Ceiling(before))
                {
                    // written back once a second: every write is sent to the clients
                    WaitLeft = waitLocal;
                    SetStatus("status-waiting", marker.Name, (int)Math.Ceiling(waitLocal));
                }
                return;
            }
            // a player at the helm sails the boat: the locust stands by
            IMountable mount = boat.GetBehavior<Vintagestory.GameContent.EntityBehaviorSeatable>();
            if (mount != null && MountableUtil.IsBeingControlled(mount)) { havePrev = false; path = null; SetStatus("status-helm"); return; }
            if (BoatDrive.For(boat) == null) { SetStatus("status-notafloat"); return; }
            if (!boat.Swimming) { Release(boat); SetStatus("status-notafloat"); return; }
            if (laneHold)
            {
                // the water of the berth stays its own until it is a length clear of it
                laneHoldFor += dt;
                Docking.BoatSize(boat, out double qhl, out _);
                if (laneHoldFor > 60 || Sq(boat.Pos.X - laneHoldX) + Sq(boat.Pos.Z - laneHoldZ) > Sq(2 * qhl + 8)) { laneHold = false; system.Berths.Done(ShipKey(boat)); }
                else if (heldLane != null) system.Berths.Manoeuvre(ShipKey(boat), heldLane);
            }
            if (leaving && LeaveBerth(boat, dt)) return;

            double tx = marker.X + 0.5, tz = marker.Z + 0.5;
            double dist = Math.Sqrt((tx - bx) * (tx - bx) + (tz - bz) * (tz - bz));
            SailorConfig cfg = SailorLocustSystem.Config;

            // the berth: in front of the post, the boat with its side to it. A post never told which way it looks finds
            // the way with the most water, once a boat is near enough for its chunk to be loaded
            bool berthed = false;
            double faceX = 0, faceZ = 0;
            if (marker.Dock)
            {
                if (marker.Facing < 0 && dist <= 48)
                {
                    int found = Docking.AutoFacing(system.SharedNav, marker);
                    if (found >= 0)
                    {
                        marker.Facing = found;
                        var be = sapi.World.BlockAccessor.GetBlockEntity(new BlockPos(marker.X, marker.Y, marker.Z, 0)) as BlockEntitySailorMarker;
                        if (be != null) { be.Facing = found; be.MarkDirty(true); }
                    }
                }
                if (marker.Facing >= 0)
                {
                    berthed = true;
                    Docking.Dir(marker.Facing, out faceX, out faceZ);
                    // planned again once the dock's chunk is loaded: a plan made from far off read its water as land
                    if (dockingFor != marker || (!berthLoaded && system.SharedNav.Loaded(marker.X, marker.Y, marker.Z))) PlanBerth(boat, marker);
                    // out along the line of the pier, then to the head of the berth, then along the pier into the berth
                    tx = berthPhase == 0 ? outerX : berthPhase == 1 ? entryX : glideX;
                    tz = berthPhase == 0 ? outerZ : berthPhase == 1 ? entryZ : glideZ;
                    dist = Math.Sqrt((tx - bx) * (tx - bx) + (tz - bz) * (tz - bz));
                }
            }
            if (!berthed && dockingFor != null && viaKind == 0) { dockingFor = null; gentle = false; }
            if (viaKind == Marker.RoleWait && dist <= 8)
            {
                // in the waiting area: still, out of the way of the boats coming and going, until its turn
                Brake(boat);
                progressTimer = 0; progressFrom = null; path = null;
                SetStatus("status-waitarea", stop.Name);
                viaKind = 0;
                return;
            }
            if (viaKind == Marker.RoleEntry && dist <= cfg.BuoyPassRadius)
            {
                entryPassedFor = StopIndex + "|" + stop.Name; viaKind = 0; Route = null; path = null;
                return;
            }
            if (viaKind == Marker.RoleExit && dist <= cfg.BuoyPassRadius)
            {
                exitVia = null; viaKind = 0; Route = null; path = null;
                return;
            }
            if (!marker.Dock && viaKind == 0 && dist <= cfg.BuoyPassRadius)
            {
                Passed?.Invoke(locust, marker);
                Advance();
                return;
            }
            Docking.BoatSize(boat, out double hl, out double hw);
            Vec2d bow = Bow(boat);
            // a long boat turns wide: a point it is to reach counts as reached nearer its own length, or once it has gone
            // by it (behind the bow and close), so it does not circle the point for ever
            bool passedIt = (tx - bx) * bow.X + (tz - bz) * bow.Y < 0 && dist < hl + 6;
            if (berthed && berthPhase >= 1 && !system.Berths.Manoeuvre(ShipKey(boat), BerthLane(hl, hw)))
            {
                // another boat works in the water of this berth (coming in or going out of one near): it waits its turn
                Brake(boat);
                progressTimer = 0; progressFrom = null; path = null; spinFor = 0;
                SetStatus("status-giveway", marker.Name);
                return;
            }
            if (berthed)
            {
                double toBerth = Math.Sqrt(Sq(glideX - bx) + Sq(glideZ - bz));
                if (berthPhase == 0 && (dist < Math.Max(6, hl * 0.75) || passedIt || spinFor > 10 || (arriveWhy != null && dist < Math.Max(12, hl))))
                {
                    if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] " + boat.Code.Path + " at the point out (" + dist.ToString("0.0") + " from it, " + toBerth.ToString("0.0") + " from the berth), to the head");
                    berthPhase = 1; arriveWhy = null; spinFor = 0;
                    path = null; replanIn = 0; Route = null;
                    tx = entryX; tz = entryZ; dist = Math.Sqrt(Sq(entryX - bx) + Sq(entryZ - bz));
                    passedIt = (tx - bx) * bow.X + (tz - bz) * bow.Y < 0 && dist < hl + 4;
                }
                else if (berthPhase == 0) arriveWhy = null;
                if (berthPhase == 1)
                {
                    // the head of the berth is where the boat turns to come in: it goes there even when it starts near the
                    // berth, unless it already lies about along the pier
                    Vec3f v0 = boat.Pos.GetViewVector();
                    double along = Math.Abs(v0.X * axisX + v0.Z * axisZ) / Math.Max(1e-6, Math.Sqrt(v0.X * v0.X + v0.Z * v0.Z));
                    if (toBerth < 15) nearTimer += dt;
                    bool atHead = dist < Math.Max(5, hl * 0.5) || (passedIt && along > 0.7) || (arriveWhy != null && dist < Math.Max(10, hl * 0.75));
                    bool alreadyIn = toBerth < Math.Max(4, hl * 0.4) && along > 0.9;
                    if (atHead && !alreadyIn && along < 0.8 && loops < 2 && nearTimer <= 45 && spinFor <= 10)
                    {
                        // at the head but across the line: not turned in from there (it would run its bow into the pier); out
                        // again to the point on the line, round, and in straight
                        loops++; Retries++;
                        berthPhase = 0; arriveWhy = null; path = null; replanIn = 0; Route = null;
                        if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] " + boat.Code.Path + " at the head of the berth at " + marker.Name + " but across the line (" + (Math.Acos(Math.Min(1, along)) * 180 / Math.PI).ToString("0") + " degrees off), round again");
                        return;
                    }
                    if (atHead || alreadyIn || nearTimer > 45 || spinFor > 10)
                    {
                        // in along the pier, sail in
                        if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] " + boat.Code.Path + " in along the pier: " + (atHead ? "at the head" : alreadyIn ? "already in line" : spinFor > 10 ? "turning on the spot too long" : "near the berth too long") + ", " + dist.ToString("0.0") + " from the head, " + toBerth.ToString("0.0") + " from the berth");
                        berthPhase = 2; glideTimer = 0; glideFrom = toBerth; spinFor = 0;
                        path = null; replanIn = 0; Route = null;
                        tx = glideX; tz = glideZ; dist = toBerth;
                    }
                    // a way that ends short out there is not the berth
                    arriveWhy = null;
                }
                if (berthPhase == 2)
                {
                    gentle = toBerth < 12;
                    glideTimer += dt;
                    if (arriveWhy != null && toBerth > 7) arriveWhy = null;
                    if (toBerth > 3 && retries < 1 && (arriveWhy != null || progressTimer > 6) && toBerth <= 12)
                    {
                        // short of the berth and not getting nearer: back out to its head and in again
                        retries++; Retries++;
                        berthPhase = 1; arriveWhy = null; progressTimer = 0; progressFrom = null;
                        path = null; replanIn = 0; Route = null; gentle = false;
                        if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] " + boat.Code.Path + " short of the berth at " + marker.Name + " (" + toBerth.ToString("0.0") + "), back to its head");
                        return;
                    }
                    if (toBerth <= 1.6 || glideTimer > 30 + 5 * glideFrom || arriveWhy != null || (toBerth <= 6 && progressTimer > 5))
                    {
                        string how = toBerth <= 1.6 ? "slid into the berth" : arriveWhy ?? (glideTimer > 30 + 5 * glideFrom ? "slow to get in" : "stopped short");
                        if (toBerth > 7 + Math.Sqrt(Sq(glideX - berthX) + Sq(glideZ - berthZ)))
                        {
                            // not in the berth: it is not arrived. Round again; a second time short, it lets the berth go and waits
                            shortTries++; Retries++;
                            arriveWhy = null; path = null; replanIn = 0; Route = null; gentle = false; progressTimer = 0; progressFrom = null;
                            if (shortTries < 2) { berthPhase = 0; loops = 0; retries = 0; }
                            else
                            {
                                shortTries = 0;
                                system.Berths.Leave(ShipKey(boat));
                                dockingFor = null;
                                berthRest = 60;
                            }
                            if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] " + boat.Code.Path + " " + how + " at " + marker.Name + ", " + toBerth.ToString("0.0") + " from the berth: " + (berthRest > 0 ? "lets the berth go for a minute" : "round again"));
                            return;
                        }
                        shortTries = 0;
                        Moor(boat, marker);
                        Arrive(boat, stop, marker, how + ", " + toBerth.ToString("0.0") + " from its middle" + (moored ? ", moored (moved " + LastSnapDist.ToString("0.0") + " blocks, turned " + LastSnapTurn.ToString("0") + " degrees)" : ", not moored"));
                        return;
                    }
                }
            }
            else if (marker.Dock && dist <= cfg.ArrivalRadius) { Arrive(boat, stop, marker, "within the arrival radius (no berth)"); return; }

            MeasureSpeed(boat, dt);

            // the other boats near: one with the way ahead is waited for; the berth is not entered while another's hull is on it
            List<Hull> others = OtherHulls(boat, 48);
            if (berthed && berthPhase >= 1 && BerthTaken(others, hl, hw))
            {
                // a boat lying there for good (a player's) is not waited for past a minute and a half
                berthTakenFor += dt;
                if (berthTakenFor < 90)
                {
                    Brake(boat);
                    progressTimer = 0; progressFrom = null; path = null;
                    SetStatus("status-berth", marker.Name);
                    return;
                }
            }
            else berthTakenFor = 0;
            if (yieldRest > 0) yieldRest -= dt;
            Hull giveWay = yieldRest > 0 ? null : GiveWayTo(boat, others, hl, hw);
            if (giveWay != null)
            {
                Yielding = true;
                yieldFor += dt;
                // held back long enough: it goes on, and its eyes take it round the other
                if (yieldFor > 30) { yieldFor = 0; yieldRest = 15; }
                Brake(boat);
                progressTimer = 0; progressFrom = null;
                if (cfg.Debug && yieldFor < 0.3) sapi.Logger.Notification("[sailorlocust] " + boat.Code.Path + " gives way to " + giveWay.Boat.Code.Path + " near " + marker.Name);
                SetStatus("status-giveway", marker.Name);
                return;
            }
            yieldFor = Math.Max(0, yieldFor - dt);

            string key = marker.X + "," + marker.Z + "," + StopIndex + "," + viaKind;
            if (Route == null || routeFor != key)
            {
                Route = system.Ocean.FindRoute(bx, bz, tx, tz, out RouteHow);
                routeIdx = 1; routeFor = key; path = null;
                if (cfg.Debug) sapi.Logger.Notification("[sailorlocust] route to " + marker.Name + ": " + RouteHow + ", " + Route.Count + " points, " + (int)OceanGrid.Length(Route) + " blocks");
            }
            // the route ends where the boat is to lie: the berth, even when the post was only told which way it looks after the route was made
            Route[Route.Count - 1] = new Vec2d(tx, tz);
            while (routeIdx < Route.Count - 1 && Sq(Route[routeIdx].X - bx) + Sq(Route[routeIdx].Y - bz) < 40 * 40) routeIdx++;
            Vec2d carrot = Route[routeIdx];
            double cdx = carrot.X - bx, cdz = carrot.Y - bz, clen = Math.Sqrt(cdx * cdx + cdz * cdz);
            double goalX = carrot.X, goalZ = carrot.Y;
            const double Reach = LocalNav.R - 6;
            if (clen > Reach) { goalX = bx + cdx / clen * Reach; goalZ = bz + cdz / clen * Reach; }

            int hard = HardRadius(boat), soft = hard + 3;
            // coming into the berth it may come as near the pier as the berth itself lies
            if (dockingFor != null && berthPhase == 2) { hard = 1; soft = 2; }

            // the way through the water in sight, looked at again every second or so
            replanIn -= dt;
            if (path == null || replanIn <= 0 || pathIdx >= path.Count - 1)
            {
                replanIn = 1.2;
                if (nav == null) nav = system.SharedNav;
                int waterY = nav.FindWaterY(bx, boat.Pos.Y, bz);
                if (waterY < 0) { Release(boat); SetStatus("status-notafloat"); return; }
                nav.Obstacles.Clear();
                // a boat tied up is passed close, as at a pier; one lying out on the water (waiting, giving way) is given room
                // for this boat's own swing: a long boat turning sweeps its ends wide of the way its middle follows
                foreach (Hull h in others)
                {
                    bool tied = h.Pilot != null && h.Pilot.Waiting;
                    double room = tied ? 0.6 : Math.Min(6, 1 + hl * 0.5);
                    nav.Obstacles.Add(new[] { h.X, h.Z, h.Ax, h.Az, h.Hl + room, h.Hw + room });
                }
                nav.Scan(bx, bz, waterY);
                nav.Obstacles.Clear();
                path = nav.Plan(bx, bz, goalX, goalZ, hard, soft, out pathReached);
                ownClear = nav.Clearance(bx, bz);   // read now: the look at the water is shared, and the next boat's look replaces it
                if (dockingFor != null && berthPhase == 2 && LineClear(nav, bx, bz, glideX, glideZ))
                {
                    // straight in along the pier: the water between is clear
                    // a point every two blocks: a way of fewer than three points reads as no way at all
                    // not at the berth itself but at the line of the pier some way ahead, then down the line: a boat a little
                    // off the line comes onto it as it goes, rather than angle in at the pier
                    double s0 = (bx - glideX) * axisX + (bz - glideZ) * axisZ;
                    double lead = Math.Min(Math.Abs(s0), Math.Max(4, hl * 0.6));
                    double t1 = s0 - Math.Sign(s0) * lead;
                    path = new List<Vec2d> { new Vec2d(bx, bz) };
                    int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(t1) / 2));
                    for (int k = 0; k <= n; k++) { double t = t1 * (n - k) / n; path.Add(new Vec2d(glideX + axisX * t, glideZ + axisZ * t)); }
                    pathIdx = 0; pathReached = true;
                }
                pathIdx = 0;
                bool edge = nav.UnknownCells > LocalNav.N * LocalNav.N / 8;
                // the stop itself is in reach, not a point of the long route on the way to it
                bool lastStretch = routeIdx >= Route.Count - 1;
                if (!edge && !lastStretch && (path == null || path.Count < 3 || (!pathReached && path.Count < 6)))
                {
                    // a point of the long route that the real water does not reach (the ocean map is coarse): on to the next one
                    routeIdx++;
                    path = null; replanIn = 0;
                    return;
                }
                if (path == null || path.Count < 3)
                {
                    path = null;
                    if (edge) { AtTheEdge(boat); return; }
                    if (marker.Dock && dist <= 32) { if (berthed) { arriveWhy = "no way nearer"; } else { Arrive(boat, stop, marker, "as near as water goes"); return; } }
                    if (backoffLeft <= 0) { Stuck(boat, marker); }
                }
                else if (!pathReached && path.Count < 6)
                {
                    if (cfg.Debug && marker.Dock && dist <= 32) sapi.Logger.Notification("[sailorlocust] way ends short: " + boat.Code.Path + " at " + bx.ToString("0.0") + " " + bz.ToString("0.0") + " y " + boat.Pos.Y.ToString("0.00") + ", water level " + nav.WaterY + ", goal " + goalX.ToString("0.0") + " " + goalZ.ToString("0.0") + " clear " + nav.Clearance(goalX, goalZ) + ", own clear " + nav.Clearance(bx, bz) + ", way of " + path.Count + " ending " + path[path.Count - 1].X.ToString("0.0") + " " + path[path.Count - 1].Y.ToString("0.0") + ", unknown " + nav.UnknownCells + ", route point " + routeIdx + "/" + Route.Count + ", hard " + hard);
                    if (edge) { path = null; AtTheEdge(boat); return; }
                    if (marker.Dock && dist <= 32) { if (berthed) { arriveWhy = "way ends short"; } else { Arrive(boat, stop, marker, "way ends short"); return; } }
                }
                else edgeWaits = 0;
            }

            // no way made for a while: back off and look again
            if (progressFrom == null) progressFrom = boat.Pos.XYZ;
            if (boat.Pos.XYZ.SquareDistanceTo(progressFrom) > 2.5 * 2.5) { progressFrom = boat.Pos.XYZ; progressTimer = 0; if (StuckCount > 0 && backoffLeft <= 0) StuckCount = Math.Max(0, StuckCount - 1); }
            else progressTimer += dt;
            if (backoffLeft <= 0 && progressTimer > 9) Stuck(boat, marker);
            if (backoffLeft > 0)
            {
                backoffLeft -= dt;
                SetSail(boat, 0);
                Keys(false, true, backoffSide > 0, backoffSide < 0);
                if (backoffLeft <= 0) { path = null; replanIn = 0; progressTimer = 0; progressFrom = boat.Pos.XYZ; }
                return;
            }
            if (path == null) { Keys(false, false, false, false); return; }

            // aim at the point of the way a few blocks ahead
            double look = 5 + 2 * hard;
            while (pathIdx < path.Count - 1 && Sq(path[pathIdx].X - bx) + Sq(path[pathIdx].Y - bz) < look * look) pathIdx++;
            Vec2d aim = path[pathIdx];

            Vec3f view = boat.Pos.GetViewVector();
            double fx = -view.X * headingSign, fz = -view.Z * headingSign;
            double flen = Math.Sqrt(fx * fx + fz * fz);
            if (flen < 1e-6) return;
            fx /= flen; fz /= flen;
            double ax = aim.X - bx, az = aim.Y - bz;
            double angle = Math.Atan2(fx * az - fz * ax, fx * ax + fz * az);   // positive: the aim lies on the side the bow turns to when it turns "positive"
            double absAngle = Math.Abs(angle);
            // turning on the spot for long near a point of the coming in: the point is taken as reached
            if (dockingFor != null && berthPhase < 2 && absAngle > 1.0 && dist < hl + 12) spinFor += dt; else spinFor = Math.Max(0, spinFor - dt);

            // which key turns the bow towards positive: read off the bow's own turning while a key is held
            if (havePrev)
            {
                double rot = prevFx * fz - prevFz * fx;
                if (heldLeft && !heldRight) leftEvidence += rot;
                if (heldRight && !heldLeft) rightEvidence += rot;
                if (Math.Abs(leftEvidence) > 0.08) { leftTurnsPositive = leftEvidence > 0; leftEvidence *= 0.5; }
                else if (Math.Abs(rightEvidence) > 0.08) { leftTurnsPositive = rightEvidence < 0; rightEvidence *= 0.5; }
            }
            prevFx = fx; prevFz = fz; havePrev = true;

            bool turnPositive = angle > 0.05, turnNegative = angle < -0.05;
            bool left = leftTurnsPositive ? turnPositive : turnNegative;
            bool right = leftTurnsPositive ? turnNegative : turnPositive;

            bool sails = HasSails(boat);
            bool forward;
            if (sails)
            {
                // the sail is set as a sailor would: for the stretch ahead, not for the instant. What it should be has
                // slack between going up and coming down, has to hold for a while before the sail is touched, and the
                // sail is left alone for some seconds after each change. Only coming into the berth takes it in at once
                int have = GameMath.Clamp(boat.WatchedAttributes.GetInt("sailPosition"), 0, 2);
                int cruise = GameMath.Clamp(cfg.CruiseSail, 1, 2);
                int want;
                if (dist < 14 || absAngle > 1.1 || gentle) want = 0;
                else if (dist > 50 && ownClear >= soft && absAngle < 0.45) want = cruise;
                else want = 1;
                if (have == 2 && want == 1 && dist > 38 && ownClear > hard && absAngle < 0.8) want = 2;       // a little worse is not reason to shorten sail
                if (have == 0 && want >= 1 && absAngle > 0.7) want = 0;                                         // sail goes up once the bow is about on the way
                if (have >= 1 && want == 0 && dist >= 14 && absAngle < 1.3) want = have;                        // a wide turn is taken under the sail it has
                sailSince += dt;
                if (want == have) { sailWanted = have; sailWantFor = 0; }
                else
                {
                    if (want != sailWanted) { sailWanted = want; sailWantFor = 0; }
                    sailWantFor += dt;
                    bool berthing = want == 0 && (dist < 14 || gentle);
                    if (berthing || (sailWantFor >= (want > have ? 2.5 : 1.5) && sailSince >= 6))
                    {
                        // one step at a time, as the game's own sail goes
                        SetSail(boat, berthing ? 0 : have + Math.Sign(want - have));
                        sailSince = 0; sailWantFor = 0;
                    }
                }
                forward = boat.WatchedAttributes.GetInt("sailPosition") == 0 && absAngle < 1.0;
            }
            else forward = absAngle < 1.0;
            Keys(forward, false, left, right);

            statusIn -= dt;
            if (statusIn <= 0)
            {
                statusIn = 2;
                if (StuckCount >= 4) SetStatus("status-stuck", marker.Name);
                else SetStatus("status-sailing", marker.Name, (int)dist);
            }
        }

        /// <summary>
        /// The water ahead is not loaded. The boat waits; after a while with no player near it leaves the world and
        /// sails on by time (OffscreenFleet), so a line keeps running past the edge of what the players keep loaded.
        /// </summary>
        void AtTheEdge(Entity boat)
        {
            Release(boat);
            SetStatus("status-chunks");
            progressTimer = 0;
            if (++edgeWaits < 10) return;
            edgeWaits = 0;
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                if (player?.Entity != null && player.Entity.Pos.SquareDistanceTo(boat.Pos.XYZ) < 64 * 64) return;
            }
            system.Fleet.TryVirtualize(boat, locust, "the loaded water ends here");
        }

        string arriveWhy;

        void Arrive(Entity boat, Stop stop, Marker marker, string why = "")
        {
            system.Berths.Done(ShipKey(boat));
            if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] arrives at " + marker.Name + " (" + boat.Code.Path + "): " + why);
            arriveWhy = null;
            Release(boat);
            Waiting = true;
            WaitLeft = stop.Wait >= 0 ? stop.Wait : SailorLocustSystem.Config.DefaultWaitSeconds;
            waitLocal = WaitLeft;
            StuckCount = 0; path = null; Route = null;
            SetStatus("status-waiting", marker.Name, (int)Math.Ceiling(WaitLeft));
            Arrived?.Invoke(locust, marker);
        }

        void Stuck(Entity boat, Marker marker)
        {
            StuckCount++;
            backoffLeft = 3.5;
            backoffSide = -backoffSide;
            progressTimer = 0;
            if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] no way made near " + (int)boat.Pos.X + " " + (int)boat.Pos.Z + ", backing off (" + StuckCount + ")");
            if (StuckCount >= 4) SetStatus("status-stuck", marker.Name);
        }

        void MeasureSpeed(Entity boat, float dt)
        {
            if (speedFrom == null) { speedFrom = boat.Pos.XYZ; speedTimer = 0; return; }
            speedTimer += dt;
            if (speedTimer < 1) return;
            Vec3d now = boat.Pos.XYZ;
            double mx = now.X - speedFrom.X, mz = now.Z - speedFrom.Z;
            Speed = Math.Sqrt(mx * mx + mz * mz) / speedTimer;
            // the bow is where the boat goes when "forward" is held: if it went the other way, the bow is the other end
            if (forwardHeld && Speed > 0.4 && backoffLeft <= 0)
            {
                Vec3f view = boat.Pos.GetViewVector();
                double along = mx * -view.X * headingSign + mz * -view.Z * headingSign;
                if (along < -0.3 * Speed * speedTimer) { headingSign = -headingSign; havePrev = false; }
                else if (along > 0.7 * Speed * speedTimer)
                {
                    // the speed of the open stretches: the slow work near docks and shores is left out of the average
                    if (Speed > topSpeed) topSpeed = Speed;
                    if (Speed >= 0.7 * topSpeed) CruiseSpeed = CruiseSpeed <= 0 ? Speed : CruiseSpeed * 0.7 + Speed * 0.3;
                }
            }
            speedFrom = now; speedTimer = 0;
        }

        /// <summary>
        /// Keeps the entity simulated with no player near. The server leaves the state of an always active entity as it
        /// finds it, so one that was asleep is woken here, and its behaviours (the physics among them) are told.
        /// </summary>
        static void Wake(Entity entity)
        {
            entity.AlwaysActive = true;
            if (entity.State == EnumEntityState.Inactive)
            {
                entity.State = EnumEntityState.Active;
                entity.OnStateChanged(EnumEntityState.Inactive);
            }
            // the physics of an entity starts with the first tick the server gives it as it sends it to the players;
            // with nobody connected that never came
            if (entity.SidedProperties?.Behaviors != null)
            {
                foreach (EntityBehavior behavior in entity.SidedProperties.Behaviors)
                {
                    if (behavior is IPhysicsTickable tickable && !tickable.Ticking) tickable.Ticking = true;
                }
            }
        }

        /// <summary>A boat other than this one lies at that dock: one of a player, that asked for no berth. Seen only where the chunk is loaded.</summary>
        bool BoatLyingAt(Marker dock, Entity self)
        {
            float reach = (float)SailorLocustSystem.Config.ArrivalRadius + 1;
            Entity[] near = sapi.World.GetEntitiesAround(new Vec3d(dock.X + 0.5, dock.Y, dock.Z + 0.5), reach, 6, e => e != self && !(e is EntityAgent) && e.Alive && BoatDrive.For(e) != null);
            return near != null && near.Length > 0;
        }

        double physicsOwed;
        double topSpeed;
        int ownClear;
        int sailWanted;
        double sailWantFor, sailSince = 100;

        /// <summary>
        /// The server runs the physics only of entities some player is following (measured: a boat in a loaded chunk
        /// with no player in range is active, set to tick, and never moved). While nobody follows the boat, its physics
        /// steps are given here, on the game thread, at the game's own step; with a player in range the server does it.
        /// </summary>
        void OwnPhysics(Entity boat, float dt)
        {
            if (boat.IsTracked != 0) { physicsOwed = 0; return; }
            float frame = Vintagestory.API.Config.GlobalConstants.PhysicsFrameTime;
            physicsOwed = Math.Min(physicsOwed + dt, frame * 6);
            if (boat.SidedProperties?.Behaviors == null) return;
            while (physicsOwed >= frame)
            {
                physicsOwed -= frame;
                foreach (EntityBehavior behavior in boat.SidedProperties.Behaviors)
                {
                    if (!(behavior is IPhysicsTickable tickable)) continue;
                    tickable.OnPhysicsTick(frame);
                    tickable.AfterPhysicsTick(frame);
                }
            }
        }

        // ---------------------------------------------------------------- the other boats

        /// <summary>The pilot sailing each boat, by the boat's entity id (an entry is good while that pilot still has that boat).</summary>
        static readonly Dictionary<long, Pilot> byBoat = new Dictionary<long, Pilot>();

        static Pilot PilotOf(Entity boat)
        {
            return byBoat.TryGetValue(boat.EntityId, out Pilot p) && p.locust.Boat == boat && p.Running && p.locust.Alive ? p : null;
        }

        class Hull { public Entity Boat; public double X, Z, Ax, Az, Hl, Hw; public Pilot Pilot; }

        List<Hull> OtherHulls(Entity boat, double radius)
        {
            var list = new List<Hull>();
            Entity[] near = sapi.World.GetEntitiesAround(boat.Pos.XYZ, (float)radius, 8, e => e != boat && e.Alive && !(e is EntityAgent) && BoatDrive.For(e) != null);
            if (near == null) return list;
            foreach (Entity e in near)
            {
                Vec3f v = e.Pos.GetViewVector();
                double l = Math.Sqrt(v.X * v.X + v.Z * v.Z);
                if (l < 1e-6) continue;
                Docking.BoatSize(e, out double hl, out double hw);
                list.Add(new Hull { Boat = e, X = e.Pos.X, Z = e.Pos.Z, Ax = v.X / l, Az = v.Z / l, Hl = hl, Hw = hw, Pilot = PilotOf(e) });
            }
            return list;
        }

        /// <summary>The way the bow of that boat points, as its pilot has measured it (either end for a boat nobody sails).</summary>
        Vec2d Bow(Entity boat)
        {
            Vec3f v = boat.Pos.GetViewVector();
            double l = Math.Max(1e-6, Math.Sqrt(v.X * v.X + v.Z * v.Z));
            return new Vec2d(-v.X * headingSign / l, -v.Z * headingSign / l);
        }

        /// <summary>
        /// That hull lies in the water ahead of a boat at x, z whose bow points f, half length hl and half width hw: in
        /// a lane as wide as both boats and a block, from the middle of the boat out to reach blocks past its bow.
        /// </summary>
        static bool InLane(double x, double z, double fx, double fz, double hl, double hw, double reach, Hull h)
        {
            double dx = h.X - x, dz = h.Z - z;
            double ahead = dx * fx + dz * fz, side = Math.Abs(dx * fz - dz * fx);
            double cos = Math.Abs(h.Ax * fx + h.Az * fz), sin = Math.Abs(h.Ax * fz - h.Az * fx);
            double along = cos * h.Hl + sin * h.Hw, across = sin * h.Hl + cos * h.Hw;
            if (ahead + along < 0 || ahead - along > hl + reach) return false;
            return side - across <= hw + 1;
        }

        /// <summary>Seconds this boat has held back for another; past a limit it goes on and leaves the way to its eyes.</summary>
        double yieldFor, yieldRest;
        /// <summary>It is holding back for another boat now.</summary>
        public bool Yielding;

        /// <summary>
        /// Another boat under sail is in the water ahead and has the way: this one holds back. The way goes to the boat
        /// that does not have this one ahead of it; between two that have each other ahead, to the one docking, then to
        /// the older boat. A boat lying still, or one that holds back itself, is not waited for: the way goes round it.
        /// </summary>
        Hull GiveWayTo(Entity boat, List<Hull> others, double hl, double hw)
        {
            Vec2d f = Bow(boat);
            double reach = 8 + Math.Max(0, Speed) * 3;
            foreach (Hull h in others)
            {
                Pilot p = h.Pilot;
                if (p == null || p.Waiting || p.Yielding || p.backoffLeft > 0) continue;
                if (!InLane(boat.Pos.X, boat.Pos.Z, f.X, f.Y, hl, hw, reach, h)) continue;
                Vec2d of = p.Bow(h.Boat);
                var me = new Hull { Boat = boat, X = boat.Pos.X, Z = boat.Pos.Z, Ax = f.X, Az = f.Y, Hl = hl, Hw = hw };
                bool mutual = InLane(h.X, h.Z, of.X, of.Y, h.Hl, h.Hw, 8 + Math.Max(0, p.Speed) * 3, me);
                if (!mutual) return h;
                bool iDock = dockingFor != null && berthPhase >= 1, itDocks = p.dockingFor != null && p.berthPhase >= 1;
                if (itDocks && !iDock) return h;
                if (iDock && !itDocks) continue;
                if (h.Boat.EntityId < boat.EntityId) return h;
            }
            return null;
        }

        bool leaving, laneHold;
        /// <summary>The way out of the port it is leaving, passed before it sails on; and what the marker of this tick is (0 the stop itself).</summary>
        Marker exitVia; int viaKind;
        string entryPassedFor;

        Marker waitSpot;

        /// <summary>
        /// A port with no waiting area of its own: each boat waits at a spot of its own, out on open water some fifty
        /// blocks off the port on the side it came from, spread round so that boats waiting do not lie on top of one
        /// another nor on a buoy the others pass. Null when no open water is found there (it then stops where it is).
        /// </summary>
        Marker OwnWaitSpot(Entity boat, Marker dock, string ship)
        {
            if (waitSpot != null && string.Equals(waitSpot.Name, dock.Name, StringComparison.OrdinalIgnoreCase)) return waitSpot;
            LocalNav nav = system.SharedNav;
            double baseAngle = Math.Atan2(boat.Pos.Z - dock.Z - 0.5, boat.Pos.X - dock.X - 0.5);
            int slot = Math.Abs((ship ?? "").GetHashCode()) % 7;
            Docking.BoatSize(boat, out double hl, out _);
            int clear = Math.Min(6, (int)Math.Ceiling(hl * 0.6) + 2);
            for (int i = 0; i < 14; i++)
            {
                int k = (slot + i) % 7 - 3;
                double radius = 50 + (i / 7) * 14;
                double a = baseAngle + k * 0.32;
                double x = dock.X + 0.5 + Math.Cos(a) * radius, z = dock.Z + 0.5 + Math.Sin(a) * radius;
                int wy = nav.FindWaterY(x, dock.Y, z);
                if (wy < 0 || !nav.Loaded((int)x, wy, (int)z)) continue;
                bool open = true;
                for (int dz = -clear; dz <= clear && open; dz += 2) for (int dx = -clear; dx <= clear && open; dx += 2) if (!nav.IsWater((int)Math.Floor(x) + dx, wy, (int)Math.Floor(z) + dz)) open = false;
                if (!open) continue;
                // not on a buoy of the route
                bool onBuoy = false;
                foreach (Marker m in system.Markers.Nearest(x, z, 3)) if (!m.Dock && Sq(m.X - x) + Sq(m.Z - z) < 16 * 16) onBuoy = true;
                if (onBuoy) continue;
                waitSpot = new Marker { Name = dock.Name, X = (int)Math.Floor(x), Y = wy, Z = (int)Math.Floor(z), Dock = false, Role = Marker.RoleWait };
                return waitSpot;
            }
            return null;
        }

        bool NearWaitArea(string port, double x, double z)
        {
            if (waitSpot != null && string.Equals(waitSpot.Name, port, StringComparison.OrdinalIgnoreCase) && Sq(waitSpot.X + 0.5 - x) + Sq(waitSpot.Z + 0.5 - z) <= 30 * 30) return true;
            Marker area = system.Markers.FindRole(port, Marker.RoleWait, x, z);
            return area != null && Sq(area.X + 0.5 - x) + Sq(area.Z + 0.5 - z) <= 30 * 30;
        }
        double leaveX, leaveZ, leaveAx, leaveAz, leaveFor, laneHoldX, laneHoldZ, laneHoldFor;
        Berths.Lane heldLane;

        string ShipKey(Entity boat) { return boat.WatchedAttributes.GetString(OffscreenFleet.IdAttr, null) ?? ("locust" + locust.EntityId); }

        /// <summary>
        /// The water a boat works in at this berth: from the far end of the hull lying there out along the pier to the
        /// point out where it lines up, as wide as the boat and two blocks each side.
        /// </summary>
        Berths.Lane BerthLane(double hl, double hw)
        {
            double far = (outerX - berthX) * axisX + (outerZ - berthZ) * axisZ;
            if (Math.Abs(far) < hl + 2)
            {
                // a berth come at straight from the front: the water in front of it
                double ox = outerX - berthX, oz = outerZ - berthZ, ol = Math.Max(1e-6, Math.Sqrt(ox * ox + oz * oz));
                return new Berths.Lane { X = berthX + ox / 2, Z = berthZ + oz / 2, Ax = axisX, Az = axisZ, HalfLength = hl + 2, HalfWidth = hw + 2 + ol / 2 };
            }
            double near = -Math.Sign(far) * (hl + 1);
            double c = (far + near) / 2;
            return new Berths.Lane { X = berthX + axisX * c, Z = berthZ + axisZ * c, Ax = axisX, Az = axisZ, HalfLength = Math.Abs(far - near) / 2, HalfWidth = hw + 2 };
        }

        /// <summary>The water to leave the berth by is free and now its own (the lane it came in by, and the room to turn).</summary>
        bool LeaveTurn(Entity boat, Marker marker)
        {
            Docking.BoatSize(boat, out double hl, out double hw);
            Berths.Lane lane = BerthLane(hl, hw);
            Docking.Dir(dockingFor.Facing, out double fx, out double fz);
            double fl = Math.Sqrt(fx * fx + fz * fz); fx /= fl; fz /= fl;
            // and the water off the side of the berth it may turn in
            var room = new Berths.Lane { X = berthX + fx * (hw + hl * 0.6), Z = berthZ + fz * (hw + hl * 0.6), Ax = axisX, Az = axisZ, HalfLength = hl + 2, HalfWidth = hl * 0.6 + 1 };
            // one lane over both: the box around the two
            var both = new Berths.Lane
            {
                X = (lane.X + room.X) / 2, Z = (lane.Z + room.Z) / 2, Ax = axisX, Az = axisZ,
                HalfLength = Math.Max(lane.HalfLength + Math.Abs((lane.X - room.X) * axisX + (lane.Z - room.Z) * axisZ) / 2, room.HalfLength),
                HalfWidth = Math.Max(lane.HalfWidth, room.HalfWidth) + Math.Abs((lane.X - room.X) * -axisZ + (lane.Z - room.Z) * axisX) / 2
            };
            if (!system.Berths.Manoeuvre(ShipKey(boat), both)) return false;
            heldLane = both;
            return true;
        }
        string leaveName;

        /// <summary>
        /// Leaving a berth: straight along the pier to clear of it, bow first if the bow points out, astern if it came in
        /// bow first, and only then free to turn. Waits while another boat is in the lane out. True while it is at it.
        /// </summary>
        bool LeaveBerth(Entity boat, float dt)
        {
            double togo = (leaveX - boat.Pos.X) * leaveAx + (leaveZ - boat.Pos.Z) * leaveAz;
            leaveFor += dt;
            if (togo <= 0.5 || leaveFor > 45)
            {
                leaving = false; path = null; Route = null; progressTimer = 0; progressFrom = null; havePrev = false;
                if (SailorLocustSystem.Config.Debug) sapi.Logger.Notification("[sailorlocust] " + boat.Code.Path + " clear of the berth at " + leaveName + (togo <= 0.5 ? "" : " (gave up backing out, " + togo.ToString("0.0") + " to go)") + " after " + leaveFor.ToString("0") + " s");
                return false;
            }
            Docking.BoatSize(boat, out double hl, out double hw);
            foreach (Hull h in OtherHulls(boat, 40))
            {
                if (InLane(boat.Pos.X, boat.Pos.Z, leaveAx, leaveAz, hl, hw, 6, h)) { Brake(boat); leaveFor -= dt * 0.5; SetStatus("status-giveway", leaveName); return true; }
            }
            Vec2d bow = Bow(boat);
            bool bowOut = bow.X * leaveAx + bow.Y * leaveAz > 0;
            SetSail(boat, 0);
            // kept along the line of the pier: a touch of helm when the hull swings off it
            double headOff = Math.Atan2(bow.X * (bowOut ? leaveAz : -leaveAz) - bow.Y * (bowOut ? leaveAx : -leaveAx), bow.X * (bowOut ? leaveAx : -leaveAx) + bow.Y * (bowOut ? leaveAz : -leaveAz));
            bool l = false, r = false;
            if (Math.Abs(headOff) > 0.08) { bool pos = headOff > 0; l = leftTurnsPositive ? pos : !pos; r = !l; }
            Keys(bowOut, !bowOut, l, r);
            SetStatus("status-leaving", leaveName);
            return true;
        }

        /// <summary>Some other boat's hull lies over the berth: the boat that left it is still on its way out.</summary>
        bool BerthTaken(List<Hull> others, double hl, double hw)
        {
            foreach (Hull h in others)
            {
                double dx = h.X - berthX, dz = h.Z - berthZ;
                double cos = Math.Abs(h.Ax * axisX + h.Az * axisZ), sin = Math.Abs(h.Ax * axisZ - h.Az * axisX);
                double along = cos * h.Hl + sin * h.Hw, across = sin * h.Hl + cos * h.Hw;
                if (Math.Abs(dx * axisX + dz * axisZ) < hl + along + 1 && Math.Abs(dx * axisZ - dz * axisX) < hw + across + 0.5) return true;
            }
            return false;
        }

        /// <summary>Takes the way off the boat: the sail in, and the oars backing while it still runs on.</summary>
        void Brake(Entity boat)
        {
            SetSail(boat, 0);
            Vec2d f = Bow(boat);
            double run = boat.Pos.Motion.X * f.X + boat.Pos.Motion.Z * f.Y;
            Keys(false, run > 0.002, false, false);
            havePrev = false;
        }

        static int HardRadius(Entity boat)
        {
            float half = 1f;
            if (boat.CollisionBox != null) half = Math.Max(half, (boat.CollisionBox.X2 - boat.CollisionBox.X1) / 2f);
            if (boat.SelectionBox != null) half = Math.Max(half, (boat.SelectionBox.X2 - boat.SelectionBox.X1) / 2f);
            return GameMath.Clamp((int)Math.Ceiling(half), 1, 3);
        }

        static double Sq(double v) { return v * v; }
    }
}
